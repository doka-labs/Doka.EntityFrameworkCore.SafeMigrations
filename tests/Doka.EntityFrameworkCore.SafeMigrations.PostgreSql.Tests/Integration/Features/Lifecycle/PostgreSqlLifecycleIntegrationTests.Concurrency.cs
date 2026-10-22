namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlLifecycleIntegrationTests
{
    /// <summary>Observes the second migrator waiting on the same database's native history lock.</summary>
    [Fact]
    public async Task ConcurrentMigrators_SerializeOnTheSameDatabaseHistoryLock()
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = watchdog.Token;
        var connectionString = await CreateDatabaseAsync(token);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE \"__LegacyMigrationsHistory\" (\"MigrationId\" text NOT NULL); "
            + "INSERT INTO \"__LegacyMigrationsHistory\" VALUES ('legacy-unchanged');");
        var barrier = new MigrationAdmissionBarrier(expectedArrivals: 1);
        var secondIdentity = $"sm-second-{Guid.NewGuid():N}";
        var secondConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = secondIdentity,
        }.ConnectionString;

        await using var first = CreateAdmissionContext(connectionString, barrier);
        await using var second = CreateContext(secondConnection);
        var waiterObserved = false;
        var secondIncomplete = false;

        // Act
        var firstMigration = first.Database.MigrateAsync(token);
        var secondMigration = Task.CompletedTask;
        try
        {
            await barrier.Admitted.WaitAsync(token);
            secondMigration = second.GetService<IMigrator>().MigrateAsync(cancellationToken: token);
            waiterObserved = await ObserveHistoryLockWaiterAsync(connectionString, secondIdentity, token);
            secondIncomplete = !secondMigration.IsCompleted;
        }
        finally
        {
            barrier.Release();
            await Task.WhenAll(firstMigration, secondMigration);
        }

        var historyRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM \"__ApplicationDbContextMigrationsHistory\" "
            + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';");
        var legacyRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM \"__LegacyMigrationsHistory\" WHERE \"MigrationId\" = 'legacy-unchanged';");

        // Assert
        Assert.True(waiterObserved);
        Assert.True(secondIncomplete);
        Assert.Equal(1, barrier.ArrivalCount);
        Assert.Equal(1, historyRows);
        Assert.Equal(1, legacyRows);
    }

    /// <summary>Requires two different database migrations to reach real DDL before either is released.</summary>
    [Fact]
    public async Task ConcurrentMigrators_AdmitDifferentDatabasesIndependently()
    {
        // Arrange
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = watchdog.Token;
        var leftConnectionString = await CreateDatabaseAsync(token);
        var rightConnectionString = await CreateDatabaseAsync(token);
        var barrier = new MigrationAdmissionBarrier(expectedArrivals: 2);
        await using var left = CreateAdmissionContext(leftConnectionString, barrier);
        await using var right = CreateAdmissionContext(rightConnectionString, barrier);
        var bothIncomplete = false;

        // Act
        var leftMigration = left.Database.MigrateAsync(token);
        var rightMigration = right.Database.MigrateAsync(token);
        try
        {
            await barrier.Admitted.WaitAsync(token);
            bothIncomplete = !leftMigration.IsCompleted && !rightMigration.IsCompleted;
        }
        finally
        {
            barrier.Release();
            await Task.WhenAll(leftMigration, rightMigration);
        }

        var leftTables = await ScalarIntAsync(leftConnectionString,
            "SELECT COUNT(*) FROM information_schema.tables "
            + "WHERE table_schema = current_schema() AND table_name = 'pipeline_state';");
        var rightTables = await ScalarIntAsync(rightConnectionString,
            "SELECT COUNT(*) FROM information_schema.tables "
            + "WHERE table_schema = current_schema() AND table_name = 'pipeline_state';");

        // Assert
        Assert.True(bothIncomplete);
        Assert.Equal(2, barrier.ArrivalCount);
        Assert.Equal(1, leftTables);
        Assert.Equal(1, rightTables);
    }

    /// <summary>Attaches only a test-owned admission observer without changing migration services.</summary>
    private static SafeMigrationDbContext CreateAdmissionContext(
        string connectionString,
        MigrationAdmissionBarrier barrier)
    {
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseNpgsql(connectionString, provider => provider
                .MigrationsAssembly(typeof(SafeMigrationDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__ApplicationDbContextMigrationsHistory"))
            .UsePostgreSqlSafeMigrations<SafeMigrationDbContext>()
            .AddInterceptors(barrier)
            .Options;

        return new SafeMigrationDbContext(options);
    }

    /// <summary>Polls native lock state for the exact second session; the deadline only prevents a hung test.</summary>
    private static async Task<bool> ObserveHistoryLockWaiterAsync(
        string connectionString,
        string applicationName,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity "
            + "WHERE datname = current_database() AND application_name = @application "
            + "AND wait_event_type = 'Lock' "
            + "AND query LIKE '%__ApplicationDbContextMigrationsHistory%');";
        command.Parameters.AddWithValue("application", applicationName);

        while (true)
        {
            var waiting = Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
            if (waiting)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    /// <summary>
    /// Holds real admitted migration DDL so native serialization and cross-database overlap are observable.
    /// </summary>
    private sealed class MigrationAdmissionBarrier(int expectedArrivals) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivalCount;

        /// <summary>Completes only when every expected migration has reached its first application DDL.</summary>
        public Task Admitted => _admitted.Task;

        /// <summary>Counts actual DDL admissions rather than task scheduling.</summary>
        public int ArrivalCount => Volatile.Read(ref _arrivalCount);

        /// <summary>Releases all held commands, including failure-cleanup paths.</summary>
        public void Release() => _release.TrySetResult();

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("CREATE TABLE pipeline_probe", StringComparison.Ordinal))
            {
                return result;
            }

            // WHY: The migrator owns its database history lock before application
            // DDL. Holding this command exposes that ownership without sleep-based
            // performance assertions or replacing the provider's locking protocol.
            if (Interlocked.Increment(ref _arrivalCount) == expectedArrivals)
            {
                _admitted.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken);

            return result;
        }
    }
}
