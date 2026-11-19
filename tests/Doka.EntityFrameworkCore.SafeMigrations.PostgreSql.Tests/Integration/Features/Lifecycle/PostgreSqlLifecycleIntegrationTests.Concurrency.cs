namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies PostgreSQL Lifecycle Integration contracts in independently owned test state.</summary>
public sealed partial class PostgreSqlLifecycleIntegrationTests
{
    /// <summary>Observes the second migrator waiting on the same database's native history lock.</summary>
    [Fact]
    public async Task ConcurrentMigrators_SerializeOnTheSameDatabaseHistoryLock()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var connectionString = await CreateDatabaseAsync(cancellationToken);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE "__LegacyMigrationsHistory" ("MigrationId" text NOT NULL);
            INSERT INTO "__LegacyMigrationsHistory" VALUES ('legacy-unchanged');
            """,
            cancellationToken: cancellationToken);

        var barrier = new MigrationAdmissionBarrier(expectedArrivals: 1);
        var secondIdentity = $"sm-second-{Guid.NewGuid():N}";
        var secondConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = secondIdentity,
        }.ConnectionString;

        await using var first = CreateAdmissionContext(connectionString, barrier);
        await using var second = CreateContext(secondConnection);
        bool waiterObserved;
        bool secondIncomplete;

        // Act
        var firstMigration = first.Database.MigrateAsync(cancellationToken);
        var secondMigration = Task.CompletedTask;
        Exception? primaryFailure = null;
        try
        {
            await WaitForMigrationAdmissionAsync(barrier.Admitted, [firstMigration], cancellationToken);
            secondMigration = second
                .GetService<IMigrator>()
                .MigrateAsync(cancellationToken: cancellationToken);

            waiterObserved = await ObserveHistoryLockWaiterAsync(
                connectionString,
                secondIdentity,
                secondMigration,
                cancellationToken);

            secondIncomplete = !secondMigration.IsCompleted;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            await OwnedTestOperationCleanup.CompleteAsync(
                primaryFailure,
                Task.WhenAll(firstMigration, secondMigration),
                () =>
                {
                    barrier.Release();

                    return Task.CompletedTask;
                },
                primaryFailure is null
                    ? null
                    : cancellation);
        }

        var historyRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM \"__ApplicationDbContextMigrationsHistory\" "
            + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';",
            cancellationToken: cancellationToken);

        var legacyRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM \"__LegacyMigrationsHistory\" WHERE \"MigrationId\" = 'legacy-unchanged';",
            cancellationToken: cancellationToken);

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
        using var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        var leftConnectionString = await CreateDatabaseAsync(cancellationToken);
        var rightConnectionString = await CreateDatabaseAsync(cancellationToken);
        var barrier = new MigrationAdmissionBarrier(expectedArrivals: 2);
        await using var left = CreateAdmissionContext(leftConnectionString, barrier);
        await using var right = CreateAdmissionContext(rightConnectionString, barrier);
        bool bothIncomplete;

        // Act
        var leftMigration = left.Database.MigrateAsync(cancellationToken);
        var rightMigration = right.Database.MigrateAsync(cancellationToken);
        Exception? primaryFailure = null;
        try
        {
            await WaitForMigrationAdmissionAsync(
                barrier.Admitted,
                [leftMigration, rightMigration],
                cancellationToken);

            bothIncomplete = !leftMigration.IsCompleted && !rightMigration.IsCompleted;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            await OwnedTestOperationCleanup.CompleteAsync(
                primaryFailure,
                Task.WhenAll(leftMigration, rightMigration),
                () =>
                {
                    barrier.Release();

                    return Task.CompletedTask;
                },
                primaryFailure is null
                    ? null
                    : cancellation);
        }

        var leftTables = await ScalarIntAsync(
            leftConnectionString,
            "SELECT COUNT(*) FROM information_schema.tables "
            + "WHERE table_schema = current_schema() AND table_name = 'pipeline_state';",
            cancellationToken: cancellationToken);

        var rightTables = await ScalarIntAsync(
            rightConnectionString,
            "SELECT COUNT(*) FROM information_schema.tables "
            + "WHERE table_schema = current_schema() AND table_name = 'pipeline_state';",
            cancellationToken: cancellationToken);

        // Assert
        Assert.True(bothIncomplete);
        Assert.Equal(2, barrier.ArrivalCount);
        Assert.Equal(1, leftTables);
        Assert.Equal(1, rightTables);
    }

    /// <summary>
    /// Observes admission without a speed threshold and fails promptly if a producer terminates first.
    /// </summary>
    private static async Task WaitForMigrationAdmissionAsync(
        Task admitted,
        IReadOnlyList<Task> producers,
        CancellationToken cancellationToken
    )
    {
        var completed = await Task
            .WhenAny(producers.Prepend(admitted))
            .WaitAsync(cancellationToken);

        if (!ReferenceEquals(completed, admitted))
        {
            await completed;

            throw new InvalidOperationException("A migration completed before the required native DDL admission.");
        }

        await admitted;
    }

    /// <summary>Attaches only a test-owned admission observer without changing migration services.</summary>
    private static SafeMigrationDbContext CreateAdmissionContext(
        string connectionString,
        MigrationAdmissionBarrier barrier
    )
    {
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseNpgsql(
                connectionString,
                provider => provider
                    .MigrationsAssembly(typeof(SafeMigrationDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__ApplicationDbContextMigrationsHistory"))
            .UsePostgreSqlSafeMigrations()
            .AddInterceptors(barrier)
            .Options;

        return new SafeMigrationDbContext(options);
    }

    /// <summary>Observes the exact second session until native lock admission or producer completion.</summary>
    private static async Task<bool> ObserveHistoryLockWaiterAsync(
        string connectionString,
        string applicationName,
        Task pending,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity WHERE datname =
        current_database()
        AND application_name = @application
        AND wait_event_type = 'Lock' AND query LIKE '%__ApplicationDbContextMigrationsHistory%');
        """;

        command.Parameters.AddWithValue("application", applicationName);

        while (!pending.IsCompleted)
        {
            var waiting = Convert.ToBoolean(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (waiting)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        await pending;

        return false;
    }

    /// <summary>
    /// Holds real admitted migration DDL so native serialization and cross-database overlap are observable.
    /// </summary>
    private sealed class MigrationAdmissionBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedArrivals;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivalCount;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        public MigrationAdmissionBarrier(
            int expectedArrivals
        )
        {
            _expectedArrivals = expectedArrivals;
        }

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
            CancellationToken cancellationToken = default
        )
        {
            if (!command.CommandText.Contains("CREATE TABLE pipeline_probe", StringComparison.Ordinal))
            {
                return result;
            }

            // WHY: The migrator owns its database history lock before application
            // DDL. Holding this command exposes that ownership without sleep-based
            // performance assertions or replacing the provider's locking protocol.
            if (Interlocked.Increment(ref _arrivalCount) == _expectedArrivals)
            {
                _admitted.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken);

            return result;
        }
    }
}
