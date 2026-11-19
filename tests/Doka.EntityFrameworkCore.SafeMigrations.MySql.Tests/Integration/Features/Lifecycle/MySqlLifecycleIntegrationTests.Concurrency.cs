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
            """
            CREATE TABLE `__LegacyMigrationsHistory` (`MigrationId` varchar(150) NOT
            NULL); INSERT INTO `__LegacyMigrationsHistory` VALUES ('legacy-unchanged');
            """,
            cancellationToken: CancellationToken.None);

        var firstGate = new MigrationAdmissionGate();
        var secondGate = new MigrationAdmissionGate();
        await using var first = CreateAdmissionContext(connectionString, firstGate);
        await using var second = CreateAdmissionContext(connectionString, secondGate);

        var secondMigration = Task.CompletedTask;
        bool secondEnteredBeforeRelease;

        // Act.
        var firstMigration = first.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        try
        {
            await AwaitMigrationAdmissionAsync(firstMigration, firstGate.Entered, CancellationToken.None);
            secondMigration = second
                .GetService<IMigrator>()
                .MigrateAsync(cancellationToken: CancellationToken.None);

            await AwaitNativeMigrationLockWaitAsync(connectionString, secondMigration, CancellationToken.None);
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
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `__ApplicationDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `__LegacyMigrationsHistory` WHERE `MigrationId` = 'legacy-unchanged';",
                cancellationToken: CancellationToken.None));
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

        // Act.
        var leftMigration = left.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        var rightMigration = right.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        try
        {
            await AwaitMigrationAdmissionsAsync(
                leftMigration,
                rightMigration,
                Task.WhenAll(leftGate.Entered, rightGate.Entered),
                CancellationToken.None);
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
        Assert.Equal(
            1,
            await ScalarIntAsync(
                leftConnectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                rightConnectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';",
                cancellationToken: CancellationToken.None));
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
    /// <param name="migration">The actual owned native migration task, not a timer proxy.</param>
    /// <param name="admission">The exact DDL-admission signal.</param>
    /// <param name="cancellationToken">The caller cancellation of the wait.</param>
    internal static async Task AwaitMigrationAdmissionAsync(
        Task migration,
        Task admission,
        CancellationToken cancellationToken
    )
    {
        var completed = await Task
            .WhenAny(migration, admission)
            .WaitAsync(cancellationToken);

        if (completed == migration)
        {
            await migration;

            throw new InvalidOperationException("The migrator completed without entering its target DDL.");
        }

        await admission;
    }

    /// <summary>Waits for both real admissions without hiding one failed owner behind the other's held gate.</summary>
    /// <param name="firstMigration">The first actual owned migration.</param>
    /// <param name="secondMigration">The second actual owned migration.</param>
    /// <param name="admissions">Completion of both exact admission signals.</param>
    /// <param name="cancellationToken">The caller cancellation of the wait.</param>
    internal static async Task AwaitMigrationAdmissionsAsync(
        Task firstMigration,
        Task secondMigration,
        Task admissions,
        CancellationToken cancellationToken
    )
    {
        // WHY: A combined producer WhenAll can hide one fault behind a peer waiting at its
        // deliberately held DDL gate. Race actual producers independently; the caller then
        // releases both gates and drains both migrations, without orphaned async admission work.
        var completed = await Task
            .WhenAny(firstMigration, secondMigration, admissions)
            .WaitAsync(cancellationToken);

        if (completed != admissions)
        {
            await completed;

            throw new InvalidOperationException("A migrator completed before both target DDL admissions.");
        }

        await admissions;
    }

    /// <summary>Observes the second migrator waiting for this test's exact native advisory-lock identity.</summary>
    private static async Task AwaitNativeMigrationLockWaitAsync(
        string connectionString,
        Task migration,
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
        command.CommandText = """
        SELECT COUNT(*) FROM INFORMATION_SCHEMA.PROCESSLIST
        WHERE ID <> CONNECTION_ID() AND COMMAND = 'Query'
        AND INFO LIKE 'SELECT GET_LOCK(%'
        AND LOCATE(@lockName, INFO) > 0;
        """;

        command.Parameters.AddWithValue("@lockName", lockName);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (migration.IsCompleted)
            {
                await migration;

                throw new InvalidOperationException(
                    "The migrator completed without waiting for the owned native lock.");
            }

            var count = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (count > 0)
            {
                return;
            }

            // WHY: This is native-state polling cadence, not a correctness deadline. A faulted
            // migrator is observed above; the caller always releases and drains both migrations.
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
