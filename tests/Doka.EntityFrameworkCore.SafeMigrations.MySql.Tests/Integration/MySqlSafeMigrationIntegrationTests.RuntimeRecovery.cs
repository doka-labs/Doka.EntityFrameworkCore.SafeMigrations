namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    private const string RuntimeGuardVariablesClearedSql = "SELECT @doka_sm_state IS NULL "
        + "AND @doka_sm_action IS NULL AND @doka_sm_repair_ok IS NULL AND @doka_sm_prerequisite_ok IS NULL "
        + "AND @doka_sm_sql IS NULL AND @doka_sm_post_ok IS NULL AND @doka_sm_data_probe_required IS NULL "
        + "AND @doka_sm_data_blocked IS NULL AND @doka_sm_transition_eligible IS NULL;";

    /// <summary>
    /// Verifies first-error behavior and acquired-resource cleanup inside real compacted setup groups.
    /// </summary>
    /// <param name="failureMode">The error, caller cancellation, or command timeout injected after PREPARE.</param>
    [Theory]
    [InlineData("error")]
    [InlineData("cancellation")]
    [InlineData("timeout")]
    public async Task RuntimeCompactedSetupFailure_PreventsFollowingStatementsAndRecoversSameSession(
        string failureMode
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `runtime_records_probe` (`Id` int NOT NULL, PRIMARY KEY (`Id`)); "
            + "INSERT INTO `runtime_records_probe` VALUES (1);");

        await using var blocker = failureMode == "error"
            ? null
            : await AcquireRuntimeSetupLockAsync(connectionString);

        using var cancellation = new CancellationTokenSource();
        var observer = new MySqlRuntimeCommandInterceptor();
        var probe = failureMode == "error"
            ? "SELECT * FROM `runtime_missing_compacted_probe`;"
            : "SELECT `Id` FROM `runtime_setup_lock` WHERE `Id` = 1 FOR UPDATE;";

        observer.InjectFirstCompactedPrepareGroup(
            "SET @runtime_prepared_setup_acquired = 1; " + probe + " SET @runtime_setup_after_error = 1;",
            failureMode == "cancellation" ? () => cancellation.CancelAfter(TimeSpan.FromMilliseconds(500)) : null);

        var options = new DbContextOptionsBuilder<MySqlRuntimeCompactedSetupProbeDbContext>()
            .UseMySql(
                connectionString,
                Fixture.ServerVersion,
                provider => provider
                    .MigrationsAssembly(typeof(MySqlRuntimeCompactedSetupProbeDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__RuntimeWorkloadHistory"))
            .UseMySqlSafeMigrations<MySqlRuntimeCompactedSetupProbeDbContext>()
            .AddInterceptors(observer)
            .Options;

        await using var context = new MySqlRuntimeCompactedSetupProbeDbContext(options);
        context.Database.SetCommandTimeout(failureMode == "timeout" ? 1 : 60);
        await context.Database.OpenConnectionAsync(CancellationToken.None);

        var result = await FailAndRecoverRuntimeAsync(context, observer, cancellation.Token);

        if (failureMode == "cancellation")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        }
        else
        {
            var exception = Assert.IsType<MySqlException>(result.Exception);
            if (failureMode == "timeout")
            {
                Assert.Equal(MySqlErrorCode.CommandTimeoutExpired, exception.ErrorCode);
                Assert.Equal(1, observer.InjectedCommandTimeout);
            }
            else
            {
                Assert.Equal(1146, exception.Number);
            }
        }

        Assert.True(observer.CompactedPrepareGroupWasInjected);
        Assert.True(result.SetupWasInjected);
        var originalPayloadBytes = Assert.IsType<int>(observer.OriginalCompactedPrepareGroupPayloadBytes);
        // WHY: This must interrupt the real assignment-plus-prepared-control batch,
        // not just the older small-control compaction tested independently.
        Assert.True(originalPayloadBytes > 256);
        Assert.Equal(1, result.CompactedPrepareAcquired);
        Assert.Equal(0, result.SetupSentinelExecuted);
        Assert.Equal(0, result.BodiesBeforeRecovery);
        Assert.Equal(1, result.TablesBeforeRecovery);
        Assert.Equal(0, result.TargetColumnsBeforeRecovery);
        Assert.Equal(0, result.HistoryBeforeRecovery);
        Assert.Equal(1, result.CleanedSessionState);
        Assert.Equal(1146, result.Cleanup.TemporaryTableError);
        Assert.Equal(1243, result.Cleanup.PreparedStatementError);
        Assert.Equal(result.SessionBefore, result.SessionAfter);
        Assert.Equal(1, result.HistoryAfterRecovery);
        Assert.Equal(1, result.TargetColumnsAfterRecovery);
        Assert.Equal(1, result.PreservedRowsBeforeRecovery);
        Assert.Equal(1, result.PreservedRowsAfterRecovery);
    }

    /// <summary>Verifies synchronous generated commands retain ordered setup, body, and cleanup semantics.</summary>
    /// <param name="failSetup">Whether the actual setup command receives a first-error probe.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeSynchronousScope_PreservesFirstErrorCleanupAndRecovery(
        bool failSetup
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `runtime_sync_rows` (`Id` int NOT NULL, PRIMARY KEY (`Id`)); "
            + "INSERT INTO `runtime_sync_rows` VALUES (1);");

        var observer = new MySqlRuntimeCommandInterceptor();
        if (failSetup)
        {
            observer.InjectFirstSetup("SELECT * FROM `runtime_missing_sync_probe`; SET @runtime_sync_sentinel = 1;");
        }

        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>("Note", "runtime_sync_rows", type: "varchar(20)", nullable: true);

        var result = ExecuteRuntimeSynchronousRecovery(context, observer, builder.Operations);

        if (failSetup)
        {
            Assert.Equal(1146, Assert.IsType<MySqlException>(result.Exception).Number);
            Assert.Equal(0, result.BodiesBeforeRecovery);
        }
        else
        {
            Assert.Null(result.Exception);
            Assert.Equal(1, result.BodiesBeforeRecovery);
        }

        Assert.Equal(0, result.SentinelExecuted);
        Assert.Equal(1, result.Cleanup.VariablesCleared);
        Assert.Equal(1146, result.Cleanup.TemporaryTableError);
        Assert.Equal(1243, result.Cleanup.PreparedStatementError);
        Assert.Equal(result.SessionBefore, result.SessionAfter);
        Assert.Equal(1, result.PreservedRows);
        Assert.Equal(1, result.TargetColumns);
    }

    /// <summary>Exercises the fused lazy prepared setup on the synchronous provider execution path.</summary>
    /// <param name="failureMode">Whether execution succeeds, raises a server error, or times out after PREPARE.</param>
    [Theory]
    [InlineData("success")]
    [InlineData("error")]
    [InlineData("timeout")]
    public async Task RuntimeSynchronousPreparedScope_PreservesDataCleanupAndRecovery(string failureMode)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `runtime_sync_rows` (`Id` int NOT NULL, PRIMARY KEY (`Id`)); "
            + "INSERT INTO `runtime_sync_rows` VALUES (1);");

        await using var blocker = failureMode == "timeout"
            ? await AcquireRuntimeSetupLockAsync(connectionString)
            : null;

        var observer = new MySqlRuntimeCommandInterceptor();
        if (failureMode != "success")
        {
            var probe = failureMode == "error"
                ? "SELECT * FROM `runtime_missing_sync_probe`;"
                : "SELECT `Id` FROM `runtime_setup_lock` WHERE `Id` = 1 FOR UPDATE;";

            observer.InjectFirstCompactedPrepareGroup(
                probe + " SET @runtime_sync_sentinel = 1;");
        }

        await using var context = CreateRuntimeContext(connectionString, observer);
        context.Database.SetCommandTimeout(failureMode == "timeout" ? 1 : 60);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        // WHY: Only explicit repair classification needs this lazy row-safety path;
        // the default strict add would not reach the acquired-PREPARE probe.
        builder.AddColumnIfNotExists<string>(
            "Note", "runtime_sync_rows", type: "varchar(20)", nullable: false, defaultValue: "preserved",
            policy: SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = ExecuteRuntimeSynchronousRecovery(context, observer, builder.Operations);

        // Assert
        if (failureMode != "success")
        {
            var exception = Assert.IsType<MySqlException>(result.Exception);
            if (failureMode == "timeout")
            {
                Assert.Equal(MySqlErrorCode.CommandTimeoutExpired, exception.ErrorCode);
                Assert.Equal(1, observer.InjectedCommandTimeout);
            }
            else
            {
                Assert.Equal(1146, exception.Number);
            }

            Assert.True(observer.CompactedPrepareGroupWasInjected);
            Assert.True(observer.OriginalCompactedPrepareGroupPayloadBytes > 256);
            Assert.Equal(0, result.BodiesBeforeRecovery);
        }
        else
        {
            Assert.Null(result.Exception);
            Assert.Equal(1, result.BodiesBeforeRecovery);
        }

        Assert.Equal(0, result.SentinelExecuted);
        Assert.Equal(1, result.Cleanup.VariablesCleared);
        Assert.Equal(1146, result.Cleanup.TemporaryTableError);
        Assert.Equal(1243, result.Cleanup.PreparedStatementError);
        Assert.Equal(result.SessionBefore, result.SessionAfter);
        Assert.Equal(1, result.PreservedRows);
        Assert.Equal(1, result.TargetColumns);
    }

    /// <summary>Verifies later independent cleanup still runs when prepared-statement cleanup fails.</summary>
    [Fact]
    public async Task RuntimeCleanupFailure_StillClearsGuardResourcesAndEvictsSession()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var pooledConnectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
        }.ConnectionString;

        var observer = new RuntimeCleanupFailureInterceptor();
        await using var context = CreateRuntimeContext(pooledConnectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var originalSession = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "runtime_cleanup_rows",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });

        var result = await FailRuntimeCleanupAndReconnectAsync(context, builder.Operations, pooledConnectionString);

        // WHY: Doka's internal cleanup wrapper disables unsafe DDL retries and retains the native cleanup failure.
        var cleanupException = Assert.IsType<InvalidOperationException>(result.Exception, exactMatch: false);

        Assert.Equal(
            "Doka.EntityFrameworkCore.MySql.MySqlMigrationSessionCleanupException",
            cleanupException.GetType().FullName);
        Assert.Equal(1146, Assert.IsType<MySqlException>(cleanupException.InnerException).Number);
        Assert.True(observer.Injected);
        Assert.NotNull(observer.Cleanup);
        Assert.Equal(1, observer.Cleanup.VariablesCleared);
        Assert.Equal(1146, observer.Cleanup.TemporaryTableError);
        Assert.Equal(1243, observer.Cleanup.PreparedStatementError);
        Assert.Equal(ConnectionState.Closed, result.FailedConnectionState);
        Assert.NotEqual(originalSession, result.ReplacementSession);
        Assert.Equal(1, result.TargetTables);
    }

    /// <summary>Captures proof before recovery can overwrite leaked guard resources.</summary>
    private static async Task<RuntimeCleanupProof> ReadRuntimeCleanupProofAsync(
        DbConnection connection
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = RuntimeGuardVariablesClearedSql;
        var variables = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

        var temporary = await RuntimeFailureCodeAsync(connection, "SELECT 1 FROM `__doka_sm_assert` LIMIT 1;");
        var prepared = await RuntimeFailureCodeAsync(connection, "DEALLOCATE PREPARE doka_sm_statement;");

        return new RuntimeCleanupProof(variables, temporary, prepared);
    }

    /// <summary>Captures resource-absence error codes without swallowing unexpected non-provider failures.</summary>
    private static async Task<int> RuntimeFailureCodeAsync(
        DbConnection connection,
        string sql
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        try
        {
            await command.ExecuteNonQueryAsync(CancellationToken.None);

            return 0;
        }
        catch (MySqlException exception)
        {
            return exception.Number;
        }
    }

    /// <summary>Executes a real synchronous scope and inspects resources before retry.</summary>
    private static RuntimeSynchronousRecovery ExecuteRuntimeSynchronousRecovery(
        DbContext context,
        MySqlRuntimeCommandInterceptor observer,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var connection = context.Database.GetDbConnection();
        var relationalConnection = context.GetService<IRelationalConnection>();
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, context.Model);
        var sessionBefore = RuntimeScalarInt(connection, "SELECT CONNECTION_ID();");
        var exception = Record.Exception(() =>
        {
            foreach (var command in commands)
            {
                command.ExecuteNonQuery(relationalConnection);
            }
        });

        var bodies = observer.GuardedBodyCount;
        var variables = RuntimeScalarInt(connection, RuntimeGuardVariablesClearedSql);
        var temporary = RuntimeFailureCode(connection, "SELECT 1 FROM `__doka_sm_assert` LIMIT 1;");
        var prepared = RuntimeFailureCode(connection, "DEALLOCATE PREPARE doka_sm_statement;");
        var sentinel = RuntimeScalarInt(connection, "SELECT @runtime_sync_sentinel IS NOT NULL;");
        foreach (var command in commands)
        {
            command.ExecuteNonQuery(relationalConnection);
        }

        var sessionAfter = RuntimeScalarInt(connection, "SELECT CONNECTION_ID();");
        var rows = RuntimeScalarInt(connection, "SELECT COUNT(*) FROM `runtime_sync_rows` WHERE `Id` = 1;");
        var columns = RuntimeScalarInt(connection, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'runtime_sync_rows' AND COLUMN_NAME = 'Note';");

        return new RuntimeSynchronousRecovery(
            exception, bodies, new RuntimeCleanupProof(variables, temporary, prepared), sentinel,
            sessionBefore, sessionAfter, rows, columns);
    }

    /// <summary>Reads a test scalar directly from the already open synchronous migration connection.</summary>
    private static int RuntimeScalarInt(
        DbConnection connection,
        string sql
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Checks synchronous resource absence without accidentally executing a dangling prepared body.</summary>
    private static int RuntimeFailureCode(
        DbConnection connection,
        string sql
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        try
        {
            command.ExecuteNonQuery();

            return 0;
        }
        catch (MySqlException exception)
        {
            return exception.Number;
        }
    }

    /// <summary>Observes forced close and pooled-session replacement after cleanup failure.</summary>
    private static async Task<RuntimeCleanupFailure> FailRuntimeCleanupAndReconnectAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        string connectionString
    )
    {
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations));
        var state = context.Database.GetDbConnection().State;
        await using var replacement = new MySqlConnection(connectionString);
        await replacement.OpenAsync(CancellationToken.None);
        var session = RuntimeScalarInt(replacement, "SELECT CONNECTION_ID();");
        var tables = RuntimeScalarInt(replacement, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'runtime_cleanup_rows';");

        return new RuntimeCleanupFailure(exception, state, session, tables);
    }

    /// <summary>Injects an earlier cleanup failure and inspects later guard cleanup before forced close.</summary>
    private sealed class RuntimeCleanupFailureInterceptor : DbCommandInterceptor
    {
        /// <summary>Gets whether the generated prepared cleanup was actually reached.</summary>
        public bool Injected { get; private set; }

        /// <summary>Gets direct same-session resource proof captured before forced close.</summary>
        public RuntimeCleanupProof? Cleanup { get; private set; }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (!Injected && command.CommandText.StartsWith(
                    "PREPARE doka_sm_statement FROM 'DO 0'",
                    StringComparison.Ordinal))
            {
                // WHY: Fail an actual owned cleanup scope, not a custom handler that bypasses setup compaction.
                command.CommandText += " SELECT * FROM `runtime_missing_cleanup_probe`;";
                Injected = true;
            }

            return ValueTask.FromResult(result);
        }

        /// <inheritdoc />
        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            if (Injected && command.CommandText.Contains(
                    "SET @doka_sm_state = NULL, @doka_sm_action = NULL",
                    StringComparison.Ordinal))
            {
                Cleanup = await ReadRuntimeCleanupProofAsync(
                    command.Connection ?? throw new InvalidOperationException("The cleanup connection was lost."));
            }

            return result;
        }
    }

    /// <summary>Carries direct evidence that every guarded scope resource was released.</summary>
    private sealed record RuntimeCleanupProof(
        int VariablesCleared,
        int TemporaryTableError,
        int PreparedStatementError
    );

    /// <summary>Carries synchronous error, cleanup, ordered retry, and durable schema/data evidence.</summary>
    private sealed record RuntimeSynchronousRecovery(
        Exception? Exception,
        long BodiesBeforeRecovery,
        RuntimeCleanupProof Cleanup,
        int SentinelExecuted,
        int SessionBefore,
        int SessionAfter,
        int PreservedRows,
        int TargetColumns
    );

    /// <summary>Carries cleanup-failure quarantine and replacement-session evidence.</summary>
    private sealed record RuntimeCleanupFailure(
        Exception? Exception,
        ConnectionState FailedConnectionState,
        int ReplacementSession,
        int TargetTables
    );
}
