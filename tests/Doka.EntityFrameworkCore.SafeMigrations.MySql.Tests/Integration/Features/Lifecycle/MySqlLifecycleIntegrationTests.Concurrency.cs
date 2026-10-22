namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlLifecycleIntegrationTests
{
    /// <summary>Serializes the same database while preserving application and legacy migration history.</summary>
    [Fact]
    public async Task ConcurrentMigratorsSerializePerDatabaseAndPreserveHistory()
    {
        // Arrange.
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `__LegacyMigrationsHistory` (`MigrationId` varchar(150) NOT NULL); "
            + "INSERT INTO `__LegacyMigrationsHistory` VALUES ('legacy-unchanged');");

        var firstGate = new MigrationAdmissionGate();
        var secondGate = new MigrationAdmissionGate();
        await using var first = CreateAdmissionContext(connectionString, firstGate);
        await using var second = CreateAdmissionContext(connectionString, secondGate);
        // WHY: This deadline only prevents a broken admission protocol from hanging the suite;
        // no duration or relative execution-speed assertion is used.
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var secondMigration = Task.CompletedTask;
        var secondEnteredBeforeRelease = false;

        // Act.
        var firstMigration = first.Database.MigrateAsync(cancellationToken: watchdog.Token);
        try
        {
            await AwaitMigrationAdmissionAsync(firstMigration, firstGate.Entered, watchdog.Token);
            secondMigration = second.GetService<IMigrator>().MigrateAsync(cancellationToken: watchdog.Token);
            await AwaitNativeMigrationLockWaitAsync(connectionString, watchdog.Token);
            secondEnteredBeforeRelease = secondGate.HasEntered;
        }
        finally
        {
            firstGate.Release();
            secondGate.Release();
            await Task.WhenAll(firstMigration, secondMigration);
        }

        // Assert.
        Assert.True(firstGate.HasEntered);
        Assert.False(secondEnteredBeforeRelease);
        Assert.Equal(1, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM `__ApplicationDbContextMigrationsHistory` "
            + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';"));
        Assert.Equal(1, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM `__LegacyMigrationsHistory` WHERE `MigrationId` = 'legacy-unchanged';"));
    }

    /// <summary>Admits both databases before either migrator is permitted to execute its first target DDL.</summary>
    [Fact]
    public async Task ConcurrentMigratorsAdmitDifferentDatabasesIndependently()
    {
        // Arrange.
        var leftConnectionString = await CreateDatabaseAsync(CancellationToken.None);
        var rightConnectionString = await CreateDatabaseAsync(CancellationToken.None);
        var leftGate = new MigrationAdmissionGate();
        var rightGate = new MigrationAdmissionGate();
        await using var left = CreateAdmissionContext(leftConnectionString, leftGate);
        await using var right = CreateAdmissionContext(rightConnectionString, rightGate);
        // WHY: Admission is the concurrency evidence; the deadline is only a deadlock watchdog.
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        // Act.
        var leftMigration = left.Database.MigrateAsync(cancellationToken: watchdog.Token);
        var rightMigration = right.Database.MigrateAsync(cancellationToken: watchdog.Token);
        try
        {
            await Task.WhenAll(
                AwaitMigrationAdmissionAsync(leftMigration, leftGate.Entered, watchdog.Token),
                AwaitMigrationAdmissionAsync(rightMigration, rightGate.Entered, watchdog.Token));
        }
        finally
        {
            leftGate.Release();
            rightGate.Release();
            await Task.WhenAll(leftMigration, rightMigration);
        }

        // Assert.
        Assert.True(leftGate.HasEntered);
        Assert.True(rightGate.HasEntered);
        Assert.Equal(1, await ScalarIntAsync(
            leftConnectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';"));
        Assert.Equal(1, await ScalarIntAsync(
            rightConnectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';"));
    }

    /// <summary>Uses the ordinary migration context with an operation-local test admission observer.</summary>
    private SafeMigrationDbContext CreateAdmissionContext(
        string connectionString,
        MigrationAdmissionGate gate
    )
    {
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseMySql(
                connectionString,
                Fixture.ServerVersion,
                provider => provider
                    .MigrationsAssembly(typeof(SafeMigrationDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__ApplicationDbContextMigrationsHistory"))
            .UseMySqlSafeMigrations()
            .AddInterceptors(gate)
            .Options;

        return new SafeMigrationDbContext(options);
    }

    /// <summary>Fails immediately when a migrator finishes or faults without reaching the expected DDL.</summary>
    private static async Task AwaitMigrationAdmissionAsync(
        Task migration,
        Task admission,
        CancellationToken cancellationToken
    )
    {
        var completed = await Task.WhenAny(migration, admission).WaitAsync(cancellationToken);
        if (completed == migration)
        {
            await migration;

            throw new InvalidOperationException("The migrator completed without entering its target DDL.");
        }

        await admission;
    }

    /// <summary>Observes the second migrator waiting for this test's exact native advisory-lock identity.</summary>
    private static async Task AwaitNativeMigrationLockWaitAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        // WHY: Fixture-owned sm_<guid> names fit the provider's un-hashed lock-name form.
        // Filtering both the native statement and exact owner excludes other parallel tests.
        var lockName = "__ef_migrations_lock:" + database;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.PROCESSLIST "
            + "WHERE ID <> CONNECTION_ID() AND COMMAND = 'Query' "
            + "AND INFO LIKE 'SELECT GET_LOCK(%' AND LOCATE(@lockName, INFO) > 0;";
        command.Parameters.AddWithValue("@lockName", lockName);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (count > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    /// <summary>Suspends only the first target DDL after the provider has acquired migration ownership.</summary>
    private sealed class MigrationAdmissionGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hasEntered;

        /// <summary>Gets the exact point at which the protected DDL is ready for dispatch.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Gets whether this migrator reached the protected target DDL.</summary>
        public bool HasEntered => Volatile.Read(ref _hasEntered) != 0;

        /// <summary>Releases this test's DDL without affecting any provider lock or other test.</summary>
        public void Release() => _release.TrySetResult();

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (command.CommandText.Contains("CREATE TABLE `pipeline_probe`", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _hasEntered, 1, 0) == 0)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
