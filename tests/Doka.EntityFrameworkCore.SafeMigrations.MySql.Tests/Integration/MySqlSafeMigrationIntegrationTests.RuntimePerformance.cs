namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the RuntimePerformance migration contracts in an independent class collection.</summary>
public sealed partial class MySqlRuntimePerformanceIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    private const int RuntimeOperationCount = MySqlRuntimeConvergenceMigration.TableCount * 18 - 1;

    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlRuntimePerformanceIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Verifies one real migration application and replay against each generic legacy starting state.</summary>
    /// <param name="initialState">The catalog shape prepared before migration execution.</param>
    [Theory]
    [InlineData("empty")]
    [InlineData("matching")]
    [InlineData("partial")]
    [InlineData("widening")]
    public async Task RuntimeWorkload_MigrateAsyncPreservesSchemaRowsAndHistory(
        string initialState
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await PrepareRuntimeCatalogAsync(connectionString, initialState, CancellationToken.None);
        var observer = new MySqlRuntimeCommandInterceptor();
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        // Act
        await context.Database.MigrateAsync(CancellationToken.None);
        var executedCommands = observer.CommandCount;
        var appliedBodies = observer.GuardedBodyCount;
        var beforeReplay = await ReadRuntimeVerificationAsync(connectionString, CancellationToken.None);
        observer.Reset();
        await context.Database.MigrateAsync(CancellationToken.None);
        var afterReplay = await ReadRuntimeVerificationAsync(connectionString, CancellationToken.None);

        // Assert
        // WHY: This is a deterministic transport-work budget, not a hardware-sensitive timing limit.
        Assert.InRange(executedCommands, 1, RuntimeOperationCount * 14 + 5);
        Assert.Equal(30, beforeReplay.Tables);
        Assert.Equal(390, beforeReplay.Columns);
        Assert.Equal(0, beforeReplay.InvalidColumnContracts);
        Assert.Equal(30, beforeReplay.PrimaryKeys);
        Assert.Equal(30, beforeReplay.UniqueConstraints);
        Assert.Equal(30, beforeReplay.ParentIndexes);
        Assert.Equal(29, beforeReplay.ForeignKeys);
        Assert.Equal(2, beforeReplay.HistoryRows);
        Assert.Equal(
            initialState == "empty"
                ? 0
                : 30,
            beforeReplay.Rows);

        Assert.Equal(beforeReplay.Rows, beforeReplay.TotalRows);
        Assert.Equal(0, observer.GuardedBodyCount);
        Assert.Equal(beforeReplay.Rows, afterReplay.Rows);
        Assert.Equal(beforeReplay.TotalRows, afterReplay.TotalRows);
        Assert.Equal(beforeReplay.HistoryRows, afterReplay.HistoryRows);
        Assert.Equal(beforeReplay.SchemaBeforeReplay, afterReplay.SchemaBeforeReplay);
        Assert.True(executedCommands > 0);
        Assert.Equal(RuntimeOperationCount, appliedBodies);
    }

    /// <summary>Verifies setup failure prevents the guarded body and leaves the same session reusable.</summary>
    [Fact]
    public async Task RuntimeSetupFailure_PreventsBodyAndRecoversSameSession()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var observer = new MySqlRuntimeCommandInterceptor();
        observer.InjectFirstSetup(
            "SELECT * FROM `runtime_missing_setup_probe`; SET @runtime_setup_after_error = 1;");

        await using var context = CreateRuntimeScopeProbeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        // Act
        var result = await FailAndRecoverRuntimeAsync(context, observer, CancellationToken.None);

        // Assert
        var exception = Assert.IsType<MySqlException>(result.Exception);
        Assert.Equal(1146, exception.Number);
        Assert.True(result.SetupWasInjected);
        Assert.Equal(0, result.SetupSentinelExecuted);
        Assert.Equal(0, result.BodiesBeforeRecovery);
        Assert.Equal(0, result.TablesBeforeRecovery);
        Assert.Equal(0, result.HistoryBeforeRecovery);
        Assert.Equal(1, result.CleanedSessionState);
        Assert.Equal(1146, result.Cleanup.TemporaryTableError);
        Assert.Equal(1243, result.Cleanup.PreparedStatementError);
        Assert.Equal(result.SessionBefore, result.SessionAfter);
        Assert.Equal(1, result.HistoryAfterRecovery);
    }

    /// <summary>Verifies cancellation cleanup without allowing a setup-delayed guarded body to execute.</summary>
    [Fact]
    public async Task RuntimeSetupCancellation_CleansStateAndRecoversSameSession()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var blocker = await AcquireRuntimeSetupLockAsync(
            connectionString,
            cancellationToken: CancellationToken.None);

        var observer = new MySqlRuntimeCommandInterceptor();
        using var cancellation = new CancellationTokenSource();
        observer.InjectFirstSetup(
            "SELECT `Id` FROM `runtime_setup_lock` WHERE `Id` = 1 FOR UPDATE; SET @runtime_setup_after_error = 1;");

        await using var context = CreateRuntimeScopeProbeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        // Act
        var result = await MySqlNativeCancellationCheckpoint.CancelWhenDispatchedAsync(
            connectionString,
            ((MySqlConnection)context.Database.GetDbConnection()).ServerThread,
            "runtime_setup_lock",
            cancellation,
            FailAndRecoverRuntimeAsync(context, observer, cancellation.Token),
            CancellationToken.None);

        // Assert
        Assert.IsType<OperationCanceledException>(result.Exception, exactMatch: false);
        Assert.True(result.SetupWasInjected);
        Assert.Equal(0, result.SetupSentinelExecuted);
        Assert.Equal(0, result.BodiesBeforeRecovery);
        Assert.Equal(0, result.TablesBeforeRecovery);
        Assert.Equal(1, result.CleanedSessionState);
        Assert.Equal(1146, result.Cleanup.TemporaryTableError);
        Assert.Equal(1243, result.Cleanup.PreparedStatementError);
        Assert.Equal(result.SessionBefore, result.SessionAfter);
        Assert.Equal(1, result.HistoryAfterRecovery);
    }

    /// <summary>Verifies command timeout cleans acquired resources before a same-session retry.</summary>
    [Fact]
    public async Task RuntimeSetupTimeout_CleansStateAndRecoversSameSession()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var blocker = await AcquireRuntimeSetupLockAsync(
            connectionString,
            cancellationToken: CancellationToken.None);

        var observer = new MySqlRuntimeCommandInterceptor();

        // WHY: A blocked locking query errors when cancelled; interrupted SLEEP can instead return normally on MariaDB.
        observer.InjectFirstSetup(
            "SELECT `Id` FROM `runtime_setup_lock` WHERE `Id` = 1 FOR UPDATE; SET @runtime_setup_after_error = 1;");

        await using var context = CreateRuntimeScopeProbeContext(connectionString, observer);
        context.Database.SetCommandTimeout(1);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        // Act
        var result = await FailAndRecoverRuntimeAsync(context, observer, CancellationToken.None);

        // Assert
        var exception = Assert.IsType<MySqlException>(result.Exception);
        Assert.Equal(MySqlErrorCode.CommandTimeoutExpired, exception.ErrorCode);
        Assert.Equal(1, observer.InjectedCommandTimeout);
        Assert.True(result.SetupWasInjected);
        Assert.Equal(0, result.SetupSentinelExecuted);
        Assert.Equal(0, result.BodiesBeforeRecovery);
        Assert.Equal(0, result.TablesBeforeRecovery);
        Assert.Equal(1, result.CleanedSessionState);
        Assert.Equal(1146, result.Cleanup.TemporaryTableError);
        Assert.Equal(1243, result.Cleanup.PreparedStatementError);
        Assert.Equal(result.SessionBefore, result.SessionAfter);
        Assert.Equal(1, result.HistoryAfterRecovery);
    }

    /// <summary>Verifies duplicate and orphan probes reject before creating their target constraints.</summary>
    /// <param name="violation">The invalid row condition introduced before actual migration application.</param>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("orphan")]
    public async Task RuntimeDataGuard_RejectsViolationAndRecoversAfterRepair(
        string violation
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await PrepareRuntimeCatalogAsync(connectionString, "partial", cancellationToken: CancellationToken.None);
        var table = MySqlRuntimeConvergenceMigration.TableName(
            violation == "duplicate"
                ? 0
                : 1);

        var offendingSql = violation == "duplicate"
            ? $"INSERT INTO `{table}` (`Id`, `Code`, `Value01`) VALUES (2, 'preserved', 'original');"
            : $"UPDATE `{table}` SET `ParentId` = 999;";

        await ExecuteSqlAsync(connectionString, offendingSql, cancellationToken: CancellationToken.None);
        var observer = new MySqlRuntimeCommandInterceptor();
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        // Act
        var result = await RejectAndRecoverRuntimeDataAsync(
            context,
            connectionString,
            table,
            violation,
            cancellationToken: CancellationToken.None);

        // Assert
        var exception = AssertOwnedGuardFailure(result.Exception);
        Assert.Equal(1062, exception.Number);
        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, result.TargetConstraintBeforeRecovery);
        Assert.Equal(0, result.HistoryBeforeRecovery);
        Assert.Equal(
            violation == "duplicate"
                ? 2
                : 1,
            result.RowsBeforeRecovery);

        Assert.Equal(1, result.CleanedSessionState);
        Assert.Equal(1146, result.Cleanup.TemporaryTableError);
        Assert.Equal(1243, result.Cleanup.PreparedStatementError);
        Assert.Equal(result.SessionBefore, result.SessionAfter);
        Assert.Equal(2, result.HistoryAfterRecovery);
        Assert.Equal(1, result.TargetConstraintAfterRecovery);
    }

    /// <summary>Verifies later raw DML cannot reuse an earlier operation's stale row-safety evidence.</summary>
    [Fact]
    public async Task RuntimeFreshDataGuard_RejectsAfterEarlierMigrationAndRollsBackLaterDml()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var options = new DbContextOptionsBuilder<MySqlRuntimeFreshStateDbContext>()
            .UseMySql(
                connectionString,
                Fixture.ServerVersion,
                provider => provider
                    .MigrationsAssembly(typeof(MySqlRuntimeFreshStateDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__RuntimeWorkloadHistory"))
            .UseMySqlSafeMigrations()
            .Options;

        await using var context = new MySqlRuntimeFreshStateDbContext(options);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        // Act
        var result = await RejectAndRecoverFreshRuntimeDataAsync(context, cancellationToken: CancellationToken.None);

        // Assert
        var exception = AssertOwnedGuardFailure(result.Exception);
        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, result.TargetConstraintBeforeRecovery);
        Assert.Equal(1, result.HistoryBeforeRecovery);

        // WHY: Rejection precedes DDL; EF rolls back later raw DML while preserving the earlier migration's history.
        Assert.Equal(1, result.RowsBeforeRecovery);
        Assert.Equal(1, result.CleanedSessionState);
        Assert.Equal(1146, result.Cleanup.TemporaryTableError);
        Assert.Equal(1243, result.Cleanup.PreparedStatementError);
        Assert.Equal(result.SessionBefore, result.SessionAfter);
        Assert.Equal(2, result.HistoryAfterRecovery);
        Assert.Equal(1, result.TargetConstraintAfterRecovery);
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Acquires a real held row lock whose interrupted wait cannot complete like the SLEEP function.</summary>
    /// <param name="connectionString">The database in which the returned owner holds a row-lock transaction.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    protected static async Task<RuntimeSetupLock> AcquireRuntimeSetupLockAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `runtime_setup_lock` (`Id` int NOT NULL, PRIMARY KEY (`Id`)); "
            + "INSERT INTO `runtime_setup_lock` VALUES (1);",
            cancellationToken: cancellationToken);

        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT `Id` FROM `runtime_setup_lock` WHERE `Id` = 1 FOR UPDATE;";
            _ = await command.ExecuteScalarAsync(cancellationToken);

            return new RuntimeSetupLock(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();

            throw;
        }
    }

    /// <summary>Configures the real provider pipeline with no explicit analysis runner.</summary>
    protected MySqlRuntimePerformanceDbContext CreateRuntimeContext(
        string connectionString,
        DbCommandInterceptor observer
    )
    {
        var options = new DbContextOptionsBuilder<MySqlRuntimePerformanceDbContext>()
            .UseMySql(
                connectionString,
                Fixture.ServerVersion,
                provider => provider
                    .MigrationsAssembly(typeof(MySqlRuntimePerformanceDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__RuntimeWorkloadHistory"))
            .UseMySqlSafeMigrations()
            .AddInterceptors(observer)
            .Options;

        return new MySqlRuntimePerformanceDbContext(options);
    }
}

public sealed partial class MySqlRuntimePerformanceIntegrationTests
{
    /// <summary>Configures a discovered one-operation migration for actual scope-failure and retry probes.</summary>
    private MySqlRuntimeScopeProbeDbContext CreateRuntimeScopeProbeContext(
        string connectionString,
        DbCommandInterceptor observer
    )
    {
        var options = new DbContextOptionsBuilder<MySqlRuntimeScopeProbeDbContext>()
            .UseMySql(
                connectionString,
                Fixture.ServerVersion,
                provider => provider
                    .MigrationsAssembly(typeof(MySqlRuntimeScopeProbeDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__RuntimeWorkloadHistory"))
            .UseMySqlSafeMigrations()
            .AddInterceptors(observer)
            .Options;

        return new MySqlRuntimeScopeProbeDbContext(options);
    }

    /// <summary>Creates generic legacy tables without altering default engine durability settings.</summary>
    /// <param name="connectionString">The isolated database receiving the legacy catalog and original rows.</param>
    /// <param name="initialState">Selects empty, widening, partial or matching catalog setup for the workload.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task PrepareRuntimeCatalogAsync(
        string connectionString,
        string initialState,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        MySqlLiveBenchmarkWorkload.RequireInitialState(initialState);
        if (initialState == "empty")
        {
            return;
        }

        for (var ordinal = 0; ordinal < MySqlRuntimeConvergenceMigration.TableCount; ordinal++)
        {
            await ExecuteSqlAsync(
                connectionString,
                MySqlLiveBenchmarkWorkload.BuildLegacyRuntimeTable(initialState, ordinal),
                cancellationToken);
        }
    }

    /// <summary>Checks catalog, history, and preserved-row totals outside the timed pipeline.</summary>
    /// <param name="connectionString">The workload database whose schema, history and rows are verified.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task<RuntimeVerification> ReadRuntimeVerificationAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        const string tables = "TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%'";
        const string constraints = "CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%'";
        var schema = await ReadRuntimeSchemaAsync(connectionString, cancellationToken: cancellationToken);
        var rows = await ReadRuntimeRowsAsync(connectionString, cancellationToken: cancellationToken);

        return new RuntimeVerification(
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE " + tables,
                cancellationToken: cancellationToken),
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE " + tables,
                cancellationToken: cancellationToken),
            await ScalarIntAsync(
                connectionString,
                $"""
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                WHERE {constraints}
                AND CONSTRAINT_TYPE = 'PRIMARY KEY'
                """,
                cancellationToken: cancellationToken),
            await ScalarIntAsync(
                connectionString,
                $"""
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                WHERE {constraints}
                AND CONSTRAINT_TYPE = 'UNIQUE'
                """,
                cancellationToken: cancellationToken),
            await ScalarIntAsync(
                connectionString,
                $"""
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
                WHERE {tables}
                AND INDEX_NAME LIKE 'IX_runtime_records_%_ParentId'
                """,
                cancellationToken: cancellationToken),
            await ScalarIntAsync(
                connectionString,
                $"""
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                WHERE {constraints}
                AND CONSTRAINT_TYPE = 'FOREIGN KEY'
                """,
                cancellationToken: cancellationToken),
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`",
                cancellationToken: cancellationToken),
            await ScalarIntAsync(
                connectionString,
                $"""
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE {tables}
                AND NOT ((COLUMN_NAME = 'Id' AND COLUMN_TYPE IN ('int','int(11)')
                AND IS_NULLABLE = 'NO') OR (COLUMN_NAME = 'ParentId'
                AND COLUMN_TYPE IN ('int','int(11)')
                AND IS_NULLABLE = 'YES') OR (COLUMN_NAME = 'Code'
                AND COLUMN_TYPE = 'varchar(80)'
                AND IS_NULLABLE = 'NO') OR (COLUMN_NAME LIKE 'Value%'
                AND COLUMN_TYPE = 'varchar(40)'
                AND IS_NULLABLE = 'YES') OR (COLUMN_NAME = 'Marker'
                AND COLUMN_TYPE = 'varchar(32)'
                AND IS_NULLABLE = 'YES'))
                """,
                cancellationToken: cancellationToken),
            rows.Preserved,
            rows.Total,
            schema);
    }

    /// <summary>Counts all rows and exact original values through one bounded verification connection.</summary>
    /// <param name="connectionString">The workload database whose runtime-table rows are aggregated.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task<(int Total, int Preserved)> ReadRuntimeRowsAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var total = 0;
        var preserved = 0;
        for (var ordinal = 0; ordinal < MySqlRuntimeConvergenceMigration.TableCount; ordinal++)
        {
            var table = MySqlRuntimeConvergenceMigration.TableName(ordinal);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT COUNT(*), COALESCE(SUM(`Id` = 1
                AND `Code` = 'preserved' AND `Value01` = 'original'
                AND `Marker` IS NULL), 0) FROM `{table}`;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The row verification returned no aggregate result.");
            }

            total += Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            preserved += Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return (total, preserved);
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Observes the failed operation, independent cleanup, and retry through the same open session.</summary>
    private protected static async Task<RuntimeRecovery> FailAndRecoverRuntimeAsync(
        DbContext context,
        MySqlRuntimeCommandInterceptor observer,
        CancellationToken cancellationToken
    )
    {
        var sessionBefore = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: cancellationToken);

        var exception = await Record.ExceptionAsync(() => context.Database.MigrateAsync(cancellationToken));

        // WHY: The attempted operation may have canceled its token. Recovery observations and retry
        // remain independent so cancellation cannot hide failed cleanup or poison the same-session retry.
        var bodies = observer.GuardedBodyCount;
        var tables = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%';",
            cancellationToken: CancellationToken.None);

        var historyBefore = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;",
            cancellationToken: CancellationToken.None);

        var cleanup = await ReadRuntimeCleanupProofAsync(
            context.Database.GetDbConnection(),
            cancellationToken: CancellationToken.None);

        var sentinel = await ContextScalarIntAsync(
            context,
            "SELECT @runtime_setup_after_error IS NOT NULL;",
            cancellationToken: CancellationToken.None);

        var acquired = await ContextScalarIntAsync(
            context,
            "SELECT COALESCE(@runtime_prepared_setup_acquired = 1, FALSE);",
            cancellationToken: CancellationToken.None);

        var columnsBefore = await ContextScalarIntAsync(
            context,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'runtime_records_probe'
            AND COLUMN_NAME = 'Code';
            """,
            cancellationToken: CancellationToken.None);

        var rowsBefore = tables == 0
            ? 0
            : await ContextScalarIntAsync(
                context,
                "SELECT COUNT(*) FROM `runtime_records_probe` WHERE `Id` = 1;",
                cancellationToken: CancellationToken.None);

        context.Database.SetCommandTimeout(60);
        await context.Database.MigrateAsync(CancellationToken.None);
        var sessionAfter = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: CancellationToken.None);

        var historyAfter = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;",
            cancellationToken: CancellationToken.None);

        var columnsAfter = await ContextScalarIntAsync(
            context,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'runtime_records_probe'
            AND COLUMN_NAME = 'Code';
            """,
            cancellationToken: CancellationToken.None);

        var rowsAfter = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `runtime_records_probe` WHERE `Id` = 1;",
            cancellationToken: CancellationToken.None);

        return new RuntimeRecovery(
            exception,
            bodies,
            tables,
            historyBefore,
            cleanup.VariablesCleared,
            sessionBefore,
            sessionAfter,
            historyAfter,
            observer.SetupWasInjected,
            sentinel,
            cleanup,
            columnsBefore,
            columnsAfter,
            rowsBefore,
            rowsAfter,
            acquired);
    }
}

public sealed partial class MySqlRuntimePerformanceIntegrationTests
{
    /// <summary>Observes authoritative row rejection and same-session retry after test-owned repair.</summary>
    /// <param name="context">The caller-owned migration context retained across rejection and retry.</param>
    /// <param name="connectionString">The database connection used to repair the blocking row independently.</param>
    /// <param name="table">The runtime fixture table containing the blocking row and target constraint.</param>
    /// <param name="violation">Selects the duplicate-key repair or the foreign-key repair probe.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task<RuntimeDataRecovery> RejectAndRecoverRuntimeDataAsync(
        MySqlRuntimePerformanceDbContext context,
        string connectionString,
        string table,
        string violation,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sessionBefore = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: cancellationToken);

        var exception = await Record.ExceptionAsync(() => context.Database.MigrateAsync(cancellationToken));

        // WHY: The attempted operation may have canceled its token. Recovery observations and retry
        // remain independent so cancellation cannot hide failed cleanup or poison the same-session retry.
        var name = violation == "duplicate"
            ? "UQ_" + table + "_Code"
            : "FK_" + table + "_Parent";

        var constraintQuery = $"""
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE()
            AND TABLE_NAME = '{table}'
            AND CONSTRAINT_NAME = '{name}';
            """;

        var targetBefore = await ContextScalarIntAsync(
            context,
            constraintQuery,
            cancellationToken: CancellationToken.None);

        var rowsBefore = await ContextScalarIntAsync(
            context,
            $"SELECT COUNT(*) FROM `{table}`;",
            cancellationToken: CancellationToken.None);

        var historyBefore = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;",
            cancellationToken: CancellationToken.None);

        var cleanup = await ReadRuntimeCleanupProofAsync(
            context.Database.GetDbConnection(),
            cancellationToken: CancellationToken.None);

        var repair = violation == "duplicate"
            ? $"UPDATE `{table}` SET `Code` = 'repaired' WHERE `Id` = 2;"
            : $"UPDATE `{table}` SET `ParentId` = 1;";

        await ExecuteSqlAsync(connectionString, repair, cancellationToken: CancellationToken.None);
        await context.Database.MigrateAsync(CancellationToken.None);
        var sessionAfter = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: CancellationToken.None);

        var historyAfter = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;",
            cancellationToken: CancellationToken.None);

        var targetAfter = await ContextScalarIntAsync(
            context,
            constraintQuery,
            cancellationToken: CancellationToken.None);

        return new RuntimeDataRecovery(
            exception,
            targetBefore,
            historyBefore,
            rowsBefore,
            cleanup.VariablesCleared,
            sessionBefore,
            sessionAfter,
            historyAfter,
            targetAfter,
            cleanup);
    }

    /// <summary>Captures live rejection after raw DML, then retries only the remaining pending migration.</summary>
    /// <param name="context">The caller-owned fresh-state context used for rejection, row repair and retry.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task<RuntimeDataRecovery> RejectAndRecoverFreshRuntimeDataAsync(
        MySqlRuntimeFreshStateDbContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        const string constraintQuery = """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE()
            AND TABLE_NAME = 'runtime_fresh_rows'
            AND CONSTRAINT_NAME = 'UQ_runtime_fresh_rows_Code';
            """;

        var sessionBefore = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: cancellationToken);

        var exception = await Record.ExceptionAsync(() => context.Database.MigrateAsync(cancellationToken));

        // WHY: The attempted operation may have canceled its token. Recovery observations and retry
        // remain independent so cancellation cannot hide failed cleanup or poison the same-session retry.
        var targetBefore = await ContextScalarIntAsync(
            context,
            constraintQuery,
            cancellationToken: CancellationToken.None);

        var rowsBefore = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `runtime_fresh_rows` WHERE `Code` = 'duplicate';",
            cancellationToken: CancellationToken.None);

        var historyBefore = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;",
            cancellationToken: CancellationToken.None);

        var cleanup = await ReadRuntimeCleanupProofAsync(
            context.Database.GetDbConnection(),
            cancellationToken: CancellationToken.None);

        await context.Database.ExecuteSqlRawAsync(
            "UPDATE `runtime_fresh_rows` SET `Code` = 'repaired' WHERE `Id` = 2;",
            CancellationToken.None);

        await context.Database.MigrateAsync(CancellationToken.None);
        var sessionAfter = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: CancellationToken.None);

        var historyAfter = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;",
            cancellationToken: CancellationToken.None);

        var targetAfter = await ContextScalarIntAsync(
            context,
            constraintQuery,
            cancellationToken: CancellationToken.None);

        return new RuntimeDataRecovery(
            exception,
            targetBefore,
            historyBefore,
            rowsBefore,
            cleanup.VariablesCleared,
            sessionBefore,
            sessionAfter,
            historyAfter,
            targetAfter,
            cleanup);
    }

    /// <summary>Reads the entire ordered physical column contract without a server-side concatenation limit.</summary>
    /// <param name="connectionString">The database whose workload schema contracts are read in full.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task<string> ReadRuntimeSchemaAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = MySqlLiveBenchmarkWorkload.RuntimeSchemaSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var schema = new StringBuilder();
        while (await reader.ReadAsync(cancellationToken))
        {
            // WHY: Read every ordinal individually; server GROUP_CONCAT limits must not hide replay drift.
            schema.AppendLine(reader.GetString(0));
        }

        return schema.ToString();
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Carries authoritative catalog, data-preservation, replay, and history checks.</summary>
    protected sealed record RuntimeVerification(
        int Tables,
        int Columns,
        int PrimaryKeys,
        int UniqueConstraints,
        int ParentIndexes,
        int ForeignKeys,
        int HistoryRows,
        int InvalidColumnContracts,
        int Rows,
        int TotalRows,
        string SchemaBeforeReplay
    );

    /// <summary>Carries acquired-resource failure and same-session recovery observations.</summary>
    protected sealed record RuntimeRecovery(
        Exception? Exception,
        long BodiesBeforeRecovery,
        int TablesBeforeRecovery,
        int HistoryBeforeRecovery,
        int CleanedSessionState,
        int SessionBefore,
        int SessionAfter,
        int HistoryAfterRecovery,
        bool SetupWasInjected,
        int SetupSentinelExecuted,
        RuntimeCleanupProof Cleanup,
        int TargetColumnsBeforeRecovery,
        int TargetColumnsAfterRecovery,
        int PreservedRowsBeforeRecovery,
        int PreservedRowsAfterRecovery,
        int CompactedPrepareAcquired
    );

    /// <summary>Carries duplicate/orphan rejection and same-session repair outcomes.</summary>
    protected sealed record RuntimeDataRecovery(
        Exception? Exception,
        int TargetConstraintBeforeRecovery,
        int HistoryBeforeRecovery,
        int RowsBeforeRecovery,
        int CleanedSessionState,
        int SessionBefore,
        int SessionAfter,
        int HistoryAfterRecovery,
        int TargetConstraintAfterRecovery,
        RuntimeCleanupProof Cleanup
    );

    /// <summary>Owns the independent blocking transaction and releases it with its physical connection.</summary>
    protected sealed class RuntimeSetupLock : IAsyncDisposable
    {
        private readonly MySqlConnection _connection;
        private readonly MySqlTransaction _transaction;

        /// <summary>Initializes the explicit dependencies of this isolated test helper.</summary>
        /// <param name="connection">The owned physical connection released after its transaction.</param>
        /// <param name="transaction">The independent blocking transaction released before its connection.</param>
        public RuntimeSetupLock(
            MySqlConnection connection,
            MySqlTransaction transaction
        )
        {
            _connection = connection;
            _transaction = transaction;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                await _transaction.DisposeAsync();
            }
            finally
            {
                await _connection.DisposeAsync();
            }
        }
    }
}
