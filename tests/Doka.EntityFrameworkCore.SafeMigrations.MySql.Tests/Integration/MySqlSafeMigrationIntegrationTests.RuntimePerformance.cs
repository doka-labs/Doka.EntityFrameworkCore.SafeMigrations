namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    private const int MeasuredRuntimeSampleCount = 3;
    private const int RuntimeOperationCount = (MySqlRuntimeConvergenceMigration.TableCount * 18) - 1;
    private static readonly JsonSerializerOptions s_runtimeSerializerOptions = new() { WriteIndented = true };

    /// <summary>Measures real migration application and replay against generic legacy starting states.</summary>
    /// <param name="initialState">The catalog shape prepared before the measured migration.</param>
    [Theory]
    [InlineData("empty")]
    [InlineData("matching")]
    [InlineData("partial")]
    [InlineData("widening")]
    public async Task RuntimeWorkload_MigrateAsyncPreservesSchemaRowsAndHistory(
        string initialState
    )
    {
        var sampleCount = RuntimeSampleCount();
        var samples = new List<RuntimeSample>(sampleCount);

        var results = await MeasureRuntimeSamplesAsync(initialState, samples);

        Assert.Equal(sampleCount, results.Count);
        // WHY: Reject unbatched transport without fixing packing details or dumping full schema records on failure.
        Assert.All(results.Select(static result => result.ExecutedCommands), commandCount =>
            Assert.InRange(commandCount, 1, (RuntimeOperationCount * 17) + 5));

        Assert.All(results, result =>
        {
            Assert.Equal(30, result.Tables);
            Assert.Equal(390, result.Columns);
            Assert.Equal(0, result.InvalidColumnContracts);
            Assert.Equal(30, result.PrimaryKeys);
            Assert.Equal(30, result.UniqueConstraints);
            Assert.Equal(30, result.ParentIndexes);
            Assert.Equal(29, result.ForeignKeys);
            Assert.Equal(2, result.HistoryRows);
            Assert.Equal(initialState == "empty" ? 0 : 30, result.Rows);
            Assert.Equal(result.Rows, result.TotalRows);
            Assert.Equal(0, result.ReplayBodies);
            Assert.Equal(result.Rows, result.ReplayRows);
            Assert.Equal(result.TotalRows, result.ReplayTotalRows);
            Assert.Equal(result.HistoryRows, result.ReplayHistoryRows);
            Assert.Equal(result.SchemaBeforeReplay, result.SchemaAfterReplay);
            Assert.True(result.ExecutedCommands > 0);
            Assert.Equal(RuntimeOperationCount, result.AppliedBodies);
        });
    }

    /// <summary>Verifies setup failure prevents the guarded body and leaves the same session reusable.</summary>
    [Fact]
    public async Task RuntimeSetupFailure_PreventsBodyAndRecoversSameSession()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var observer = new MySqlRuntimeCommandInterceptor();
        observer.InjectFirstSetup(
            "SELECT * FROM `runtime_missing_setup_probe`; SET @runtime_setup_after_error = 1;");

        await using var context = CreateRuntimeScopeProbeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        var result = await FailAndRecoverRuntimeAsync(context, observer, CancellationToken.None);

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
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var blocker = await AcquireRuntimeSetupLockAsync(connectionString);
        var observer = new MySqlRuntimeCommandInterceptor();
        using var cancellation = new CancellationTokenSource();
        observer.InjectFirstSetup(
            "SELECT `Id` FROM `runtime_setup_lock` WHERE `Id` = 1 FOR UPDATE; SET @runtime_setup_after_error = 1;",
            () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(500)));

        await using var context = CreateRuntimeScopeProbeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        var result = await FailAndRecoverRuntimeAsync(context, observer, cancellation.Token);

        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
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
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var blocker = await AcquireRuntimeSetupLockAsync(connectionString);
        var observer = new MySqlRuntimeCommandInterceptor();

        // WHY: A blocked locking query errors when cancelled; interrupted SLEEP can instead return normally on MariaDB.
        observer.InjectFirstSetup(
            "SELECT `Id` FROM `runtime_setup_lock` WHERE `Id` = 1 FOR UPDATE; SET @runtime_setup_after_error = 1;");

        await using var context = CreateRuntimeScopeProbeContext(connectionString, observer);
        context.Database.SetCommandTimeout(1);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        var result = await FailAndRecoverRuntimeAsync(context, observer, CancellationToken.None);

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
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await PrepareRuntimeCatalogAsync(connectionString, "partial");
        var table = MySqlRuntimeConvergenceMigration.TableName(violation == "duplicate" ? 0 : 1);
        var offendingSql = violation == "duplicate"
            ? $"INSERT INTO `{table}` (`Id`, `Code`, `Value01`) VALUES (2, 'preserved', 'original');"
            : $"UPDATE `{table}` SET `ParentId` = 999;";

        await ExecuteSqlAsync(connectionString, offendingSql);
        var observer = new MySqlRuntimeCommandInterceptor();
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        var result = await RejectAndRecoverRuntimeDataAsync(context, connectionString, table, violation);

        var exception = Assert.IsType<MySqlException>(result.Exception);
        Assert.Equal(1062, exception.Number);
        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, result.TargetConstraintBeforeRecovery);
        Assert.Equal(0, result.HistoryBeforeRecovery);
        Assert.Equal(violation == "duplicate" ? 2 : 1, result.RowsBeforeRecovery);
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
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var options = new DbContextOptionsBuilder<MySqlRuntimeFreshStateDbContext>()
            .UseMySql(
                connectionString,
                Fixture.ServerVersion,
                provider => provider
                    .MigrationsAssembly(typeof(MySqlRuntimeFreshStateDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__RuntimeWorkloadHistory"))
            .UseMySqlSafeMigrations<MySqlRuntimeFreshStateDbContext>()
            .Options;

        await using var context = new MySqlRuntimeFreshStateDbContext(options);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        var result = await RejectAndRecoverFreshRuntimeDataAsync(context);

        var exception = Assert.IsType<MySqlException>(result.Exception);
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

    /// <summary>Acquires a real held row lock whose interrupted wait cannot complete like the SLEEP function.</summary>
    private static async Task<RuntimeSetupLock> AcquireRuntimeSetupLockAsync(
        string connectionString
    )
    {
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `runtime_setup_lock` (`Id` int NOT NULL, PRIMARY KEY (`Id`)); "
            + "INSERT INTO `runtime_setup_lock` VALUES (1);");

        var connection = new MySqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(CancellationToken.None);
            var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT `Id` FROM `runtime_setup_lock` WHERE `Id` = 1 FOR UPDATE;";
            _ = await command.ExecuteScalarAsync(CancellationToken.None);

            return new RuntimeSetupLock(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();

            throw;
        }
    }

    /// <summary>Configures the real provider pipeline with no explicit analysis runner.</summary>
    private MySqlRuntimePerformanceDbContext CreateRuntimeContext(
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
            .UseMySqlSafeMigrations<MySqlRuntimePerformanceDbContext>()
            .AddInterceptors(observer)
            .Options;

        return new MySqlRuntimePerformanceDbContext(options);
    }

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
            .UseMySqlSafeMigrations<MySqlRuntimeScopeProbeDbContext>()
            .AddInterceptors(observer)
            .Options;

        return new MySqlRuntimeScopeProbeDbContext(options);
    }

    /// <summary>Runs isolated databases repeatedly while preserving one execution measurement per sample.</summary>
    private async Task<List<RuntimeVerification>> MeasureRuntimeSamplesAsync(
        string initialState,
        List<RuntimeSample> samples
    )
    {
        var sampleCount = RuntimeSampleCount();
        var results = new List<RuntimeVerification>(sampleCount);
        var generationAllocationBytes = await WarmRuntimeGenerationAsync();
        object? engineSettings = null;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
            await PrepareRuntimeCatalogAsync(connectionString, initialState);
            engineSettings ??= await ReadRuntimeEngineSettingsAsync(connectionString);
            var observer = new MySqlRuntimeCommandInterceptor();
            await using var context = CreateRuntimeContext(connectionString, observer);
            await context.Database.OpenConnectionAsync(CancellationToken.None);
            var started = Stopwatch.GetTimestamp();

            // WHY: The measured path is the actual application workflow; no runner or explicit preflight is invoked.
            await context.Database.MigrateAsync(CancellationToken.None);
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var applicationCommands = observer.Snapshot();
            var executedCommands = observer.CommandCount;
            var appliedBodies = observer.GuardedBodyCount;
            var before = await ReadRuntimeVerificationAsync(connectionString);
            observer.Reset();
            var replayStarted = Stopwatch.GetTimestamp();
            await context.Database.MigrateAsync(CancellationToken.None);
            var replayMilliseconds = Stopwatch.GetElapsedTime(replayStarted).TotalMilliseconds;
            var after = await ReadRuntimeVerificationAsync(connectionString);

            results.Add(before with
            {
                ReplayBodies = observer.GuardedBodyCount,
                ReplayRows = after.Rows,
                ReplayTotalRows = after.TotalRows,
                ReplayHistoryRows = after.HistoryRows,
                SchemaAfterReplay = after.SchemaBeforeReplay,
                ExecutedCommands = executedCommands,
                AppliedBodies = appliedBodies,
            });

            samples.Add(new RuntimeSample(
                sample,
                elapsedMilliseconds,
                replayMilliseconds,
                applicationCommands,
                observer.Snapshot()));
        }

        WriteRuntimeEvidence(initialState, generationAllocationBytes, engineSettings!, samples);

        return results;
    }

    /// <summary>Warms model and generator caches before measuring synchronous SQL-generation allocation.</summary>
    private async Task<long> WarmRuntimeGenerationAsync()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateRuntimeContext(connectionString, new MySqlRuntimeCommandInterceptor());
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migration = new MySqlRuntimeConvergenceMigration { ActiveProvider = context.Database.ProviderName! };
        var operations = migration.UpOperations;
        _ = generator.Generate(operations, context.Model);
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = generator.Generate(operations, context.Model);

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Creates generic legacy tables without altering default engine durability settings.</summary>
    private static async Task PrepareRuntimeCatalogAsync(
        string connectionString,
        string initialState
    )
    {
        if (initialState == "empty")
        {
            return;
        }

        for (var ordinal = 0; ordinal < MySqlRuntimeConvergenceMigration.TableCount; ordinal++)
        {
            var table = MySqlRuntimeConvergenceMigration.TableName(ordinal);
            var width = initialState == "widening" ? 20 : 80;
            var columnCount = initialState == "partial" ? 3 : 9;
            var sql = new StringBuilder($"CREATE TABLE `{table}` (`Id` int NOT NULL, ");
            sql.Append(CultureInfo.InvariantCulture, $"`Code` varchar({width}) NOT NULL, `ParentId` int NULL");
            for (var column = 1; column <= columnCount; column++)
            {
                sql.Append(CultureInfo.InvariantCulture, $", `Value{column:D2}` varchar(40) NULL");
            }

            if (initialState == "matching")
            {
                sql.Append(CultureInfo.InvariantCulture,
                    $", PRIMARY KEY (`Id`), CONSTRAINT `UQ_{table}_Code` UNIQUE (`Code`), ");

                sql.Append(CultureInfo.InvariantCulture, $"KEY `IX_{table}_ParentId` (`ParentId`)");
                if (ordinal > 0)
                {
                    var parent = MySqlRuntimeConvergenceMigration.TableName(ordinal - 1);
                    sql.Append(CultureInfo.InvariantCulture,
                        $", CONSTRAINT `FK_{table}_Parent` FOREIGN KEY (`ParentId`) ");

                    sql.Append(CultureInfo.InvariantCulture, $"REFERENCES `{parent}` (`Id`) ON DELETE RESTRICT");
                }
            }

            sql.Append("); ");
            sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO `{table}` (`Id`, `Code`, `ParentId`, `Value01`) ");
            sql.Append(CultureInfo.InvariantCulture,
                $"VALUES (1, 'preserved', {(ordinal == 0 ? "NULL" : "1")}, 'original');");

            await ExecuteSqlAsync(connectionString, sql.ToString());
        }
    }

    /// <summary>Checks catalog, history, and preserved-row totals outside the timed pipeline.</summary>
    private static async Task<RuntimeVerification> ReadRuntimeVerificationAsync(
        string connectionString
    )
    {
        const string tables = "TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%'";
        const string constraints = "CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%'";
        var schema = await ReadRuntimeSchemaAsync(connectionString);
        var rows = await ReadRuntimeRowsAsync(connectionString);

        return new RuntimeVerification(
            await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE " + tables),
            await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE " + tables),
            await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE "
                + constraints + " AND CONSTRAINT_TYPE = 'PRIMARY KEY'"),
            await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE "
                + constraints + " AND CONSTRAINT_TYPE = 'UNIQUE'"),
            await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE "
                + tables + " AND INDEX_NAME LIKE 'IX_runtime_records_%_ParentId'"),
            await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE "
                + constraints + " AND CONSTRAINT_TYPE = 'FOREIGN KEY'"),
            await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`"),
            await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE "
                + tables + " AND NOT ((COLUMN_NAME = 'Id' AND COLUMN_TYPE IN ('int','int(11)') AND IS_NULLABLE = 'NO') "
                + "OR (COLUMN_NAME = 'ParentId' AND COLUMN_TYPE IN ('int','int(11)') AND IS_NULLABLE = 'YES') "
                + "OR (COLUMN_NAME = 'Code' AND COLUMN_TYPE = 'varchar(80)' AND IS_NULLABLE = 'NO') "
                + "OR (COLUMN_NAME LIKE 'Value%' AND COLUMN_TYPE = 'varchar(40)' AND IS_NULLABLE = 'YES') "
                + "OR (COLUMN_NAME = 'Marker' AND COLUMN_TYPE = 'varchar(32)' AND IS_NULLABLE = 'YES'))"),
            rows.Preserved,
            rows.Total,
            schema,
            schema);
    }

    /// <summary>Counts all rows and exact original values through one bounded verification connection.</summary>
    private static async Task<(int Total, int Preserved)> ReadRuntimeRowsAsync(
        string connectionString
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        var total = 0;
        var preserved = 0;
        for (var ordinal = 0; ordinal < MySqlRuntimeConvergenceMigration.TableCount; ordinal++)
        {
            var table = MySqlRuntimeConvergenceMigration.TableName(ordinal);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), COALESCE(SUM(`Id` = 1 AND `Code` = 'preserved' "
                + $"AND `Value01` = 'original' AND `Marker` IS NULL), 0) FROM `{table}`;";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            if (!await reader.ReadAsync(CancellationToken.None))
            {
                throw new InvalidOperationException("The row verification returned no aggregate result.");
            }

            total += Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            preserved += Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        }

        return (total, preserved);
    }

    /// <summary>Observes the failed operation, independent cleanup, and retry through the same open session.</summary>
    private static async Task<RuntimeRecovery> FailAndRecoverRuntimeAsync(
        DbContext context,
        MySqlRuntimeCommandInterceptor observer,
        CancellationToken cancellationToken
    )
    {
        var sessionBefore = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var exception = await Record.ExceptionAsync(() => context.Database.MigrateAsync(cancellationToken));
        var bodies = observer.GuardedBodyCount;
        var tables = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%';");
        var historyBefore = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;");
        var cleanup = await ReadRuntimeCleanupProofAsync(context.Database.GetDbConnection());
        var sentinel = await ContextScalarIntAsync(context, "SELECT @runtime_setup_after_error IS NOT NULL;");
        var acquired = await ContextScalarIntAsync(
            context,
            "SELECT COALESCE(@runtime_prepared_setup_acquired = 1, FALSE);");
        var columnsBefore = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'runtime_records_probe' AND COLUMN_NAME = 'Code';");
        var rowsBefore = tables == 0
            ? 0
            : await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `runtime_records_probe` WHERE `Id` = 1;");

        context.Database.SetCommandTimeout(60);
        await context.Database.MigrateAsync(CancellationToken.None);
        var sessionAfter = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var historyAfter = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;");
        var columnsAfter = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'runtime_records_probe' AND COLUMN_NAME = 'Code';");
        var rowsAfter = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `runtime_records_probe` WHERE `Id` = 1;");

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

    /// <summary>Captures default server durability settings separately from the timed migration workflow.</summary>
    private static async Task<object> ReadRuntimeEngineSettingsAsync(
        string connectionString
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT VERSION(), @@innodb_flush_log_at_trx_commit, @@innodb_doublewrite, "
            + "@@innodb_buffer_pool_size, @@log_bin;";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            throw new InvalidOperationException("The server returned no durability settings.");
        }

        return new
        {
            actualVersion = reader.GetString(0),
            // WHY: MariaDB exposes some settings as ON strings; retain native values without coercing their meaning.
            flushLogAtTransactionCommit = reader.GetValue(1),
            doublewrite = reader.GetValue(2),
            bufferPoolBytes = reader.GetValue(3),
            binaryLogging = reader.GetValue(4),
        };
    }

    /// <summary>Observes authoritative row rejection and same-session retry after test-owned repair.</summary>
    private static async Task<RuntimeDataRecovery> RejectAndRecoverRuntimeDataAsync(
        MySqlRuntimePerformanceDbContext context,
        string connectionString,
        string table,
        string violation
    )
    {
        var sessionBefore = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var exception = await Record.ExceptionAsync(() => context.Database.MigrateAsync(CancellationToken.None));
        var name = (violation == "duplicate" ? "UQ_" + table + "_Code" : "FK_" + table + "_Parent");
        var constraintQuery = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
            + $"WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND CONSTRAINT_NAME = '{name}';";

        var targetBefore = await ContextScalarIntAsync(context, constraintQuery);
        var rowsBefore = await ContextScalarIntAsync(context, $"SELECT COUNT(*) FROM `{table}`;");
        var historyBefore = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;");
        var cleanup = await ReadRuntimeCleanupProofAsync(context.Database.GetDbConnection());
        var repair = violation == "duplicate"
            ? $"UPDATE `{table}` SET `Code` = 'repaired' WHERE `Id` = 2;"
            : $"UPDATE `{table}` SET `ParentId` = 1;";

        await ExecuteSqlAsync(connectionString, repair);
        await context.Database.MigrateAsync(CancellationToken.None);
        var sessionAfter = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var historyAfter = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;");
        var targetAfter = await ContextScalarIntAsync(context, constraintQuery);

        return new RuntimeDataRecovery(
            exception, targetBefore, historyBefore, rowsBefore, cleanup.VariablesCleared,
            sessionBefore, sessionAfter, historyAfter, targetAfter, cleanup);
    }

    /// <summary>Captures live rejection after raw DML, then retries only the remaining pending migration.</summary>
    private static async Task<RuntimeDataRecovery> RejectAndRecoverFreshRuntimeDataAsync(
        MySqlRuntimeFreshStateDbContext context
    )
    {
        const string constraintQuery = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'runtime_fresh_rows' "
            + "AND CONSTRAINT_NAME = 'UQ_runtime_fresh_rows_Code';";

        var sessionBefore = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var exception = await Record.ExceptionAsync(() => context.Database.MigrateAsync(CancellationToken.None));
        var targetBefore = await ContextScalarIntAsync(context, constraintQuery);
        var rowsBefore = await ContextScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `runtime_fresh_rows` WHERE `Code` = 'duplicate';");
        var historyBefore = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;");
        var cleanup = await ReadRuntimeCleanupProofAsync(context.Database.GetDbConnection());
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE `runtime_fresh_rows` SET `Code` = 'repaired' WHERE `Id` = 2;",
            CancellationToken.None);
        await context.Database.MigrateAsync(CancellationToken.None);
        var sessionAfter = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var historyAfter = await ContextScalarIntAsync(context, "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`;");
        var targetAfter = await ContextScalarIntAsync(context, constraintQuery);

        return new RuntimeDataRecovery(
            exception, targetBefore, historyBefore, rowsBefore, cleanup.VariablesCleared,
            sessionBefore, sessionAfter, historyAfter, targetAfter, cleanup);
    }

    /// <summary>Persists generic aggregate counters, timings, and versions in ignored artifacts.</summary>
    private void WriteRuntimeEvidence(
        string initialState,
        long generationAllocationBytes,
        object engineSettings,
        IReadOnlyList<RuntimeSample> samples
    )
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json")))
        {
            root = root.Parent;
        }

        var directory = Path.Combine(
            root?.FullName ?? throw new InvalidOperationException("The repository root was not found."),
            "artifacts",
            "performance",
            "runtime");

        Directory.CreateDirectory(directory);
        var phase = RuntimeMeasurementLabel();
        var payload = new
        {
            schemaVersion = 1,
            provider = Fixture.IsMariaDb ? "mariadb" : "mysql",
            serverVersion = Fixture.ServerVersion.ToString(),
            runtime = Environment.Version.ToString(),
            phase,
            initialState,
            tableCount = MySqlRuntimeConvergenceMigration.TableCount,
            columnsPerInitialTable = 12,
            explicitPreflight = false,
            measuredPath = "Database.MigrateAsync",
            generationAllocationBytes,
            engineSettings,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processOperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            samples,
        };

        var fileName = $"{(Fixture.IsMariaDb ? "mariadb" : "mysql")}-{phase}-{initialState}.json";
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(payload, s_runtimeSerializerOptions));
    }

    /// <summary>Reads the entire ordered physical column contract without a server-side concatenation limit.</summary>
    private static async Task<string> ReadRuntimeSchemaAsync(
        string connectionString
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONCAT(TABLE_NAME, ':', COLUMN_NAME, ':', ORDINAL_POSITION, ':', "
            + "COLUMN_TYPE, ':', IS_NULLABLE, "
            + "':', COALESCE(COLLATION_NAME, '')) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%' "
            + "UNION ALL SELECT CONCAT(TABLE_NAME, ':index:', INDEX_NAME, ':', NON_UNIQUE, ':', SEQ_IN_INDEX, "
            + "':', COLUMN_NAME, ':', COALESCE(SUB_PART, '')) FROM INFORMATION_SCHEMA.STATISTICS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%' "
            + "UNION ALL SELECT CONCAT(TABLE_NAME, ':key:', CONSTRAINT_NAME, ':', COLUMN_NAME, ':', ORDINAL_POSITION, "
            + "':', COALESCE(REFERENCED_TABLE_NAME, ''), ':', COALESCE(REFERENCED_COLUMN_NAME, '')) "
            + "FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%' "
            + "UNION ALL SELECT CONCAT(TABLE_NAME, ':fk:', CONSTRAINT_NAME, ':', REFERENCED_TABLE_NAME, "
            + "':', UPDATE_RULE, ':', DELETE_RULE) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
            + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%' ORDER BY 1;";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        var schema = new StringBuilder();
        while (await reader.ReadAsync(CancellationToken.None))
        {
            // WHY: Read every ordinal individually; server GROUP_CONCAT limits must not hide replay drift.
            schema.AppendLine(reader.GetString(0));
        }

        return schema.ToString();
    }

    /// <summary>Selects repeat measurements only for deliberate baseline/optimized experiments.</summary>
    private static int RuntimeSampleCount() => RuntimeMeasurementLabel() == "current" ? 1 : MeasuredRuntimeSampleCount;

    /// <summary>Validates the artifact label without creating a product runtime configuration knob.</summary>
    private static string RuntimeMeasurementLabel()
    {
        var label = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_RUNTIME_MEASUREMENT_LABEL") ?? "current";
        if (label is not ("baseline" or "optimized" or "current"))
        {
            throw new InvalidOperationException("The runtime measurement label is not a supported evidence label.");
        }

        return label;
    }

    /// <summary>Carries one isolated application's timings and bounded transport counters.</summary>
    private sealed record RuntimeSample(
        int Ordinal,
        double ApplyMilliseconds,
        double ReplayMilliseconds,
        object ApplicationCommands,
        object ReplayCommands
    );

    /// <summary>Carries authoritative catalog, data-preservation, replay, and history checks.</summary>
    private sealed record RuntimeVerification(
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
        string SchemaBeforeReplay,
        string SchemaAfterReplay
    )
    {
        /// <summary>Gets the guarded bodies executed during history-only replay.</summary>
        public long ReplayBodies { get; init; }

        /// <summary>Gets exact original rows remaining after replay.</summary>
        public int ReplayRows { get; init; }

        /// <summary>Gets all rows remaining after replay.</summary>
        public int ReplayTotalRows { get; init; }

        /// <summary>Gets migration history rows after replay.</summary>
        public int ReplayHistoryRows { get; init; }

        /// <summary>Gets actual commands executed during initial application.</summary>
        public long ExecutedCommands { get; init; }

        /// <summary>Gets guarded bodies executed during initial application.</summary>
        public long AppliedBodies { get; init; }
    }

    /// <summary>Carries acquired-resource failure and same-session recovery observations.</summary>
    private sealed record RuntimeRecovery(
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
    private sealed record RuntimeDataRecovery(
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
    private sealed class RuntimeSetupLock(
        MySqlConnection connection,
        MySqlTransaction transaction
    ) : IAsyncDisposable
    {
        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.DisposeAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
