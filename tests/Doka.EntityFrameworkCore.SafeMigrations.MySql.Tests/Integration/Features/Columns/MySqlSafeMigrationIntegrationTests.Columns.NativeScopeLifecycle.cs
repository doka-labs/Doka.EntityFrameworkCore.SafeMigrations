namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>
    /// A rejected decision cannot restore an unacquired NULL or stale native SQL-mode snapshot.
    /// </summary>
    /// <param name="leaveStaleSnapshot">Whether a prior successful native comment scope saves a different mode.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCommentScopeRejectedBeforeAcquisitionPreservesCurrentSession(bool leaveStaleSnapshot)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, NativeScopeLifecycleFixtureSql);
        var observer = new NativeScopeLifecycleObserver();
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await NativeScopeLifecycleSessionSqlAsync(context.Database.GetDbConnection(),
            "SET SESSION sql_mode = 'STRICT_ALL_TABLES'; SET @__doka_previous_sql_mode = NULL;");
        if (leaveStaleSnapshot)
        {
            // WHY: A real earlier native scope, not a fabricated variable assignment,
            // supplies the stale snapshot that the rejected next operation must not own.
            await ExecuteOperationsAsync(context, [NativeScopeLifecycleOperation(rejectModeDomain: false)],
                CancellationToken.None);
        }

        await NativeScopeLifecycleSessionSqlAsync(context.Database.GetDbConnection(),
            "SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION';");
        var before = await ReadNativeScopeLifecycleSessionAsync(context.Database.GetDbConnection());
        var operation = NativeScopeLifecycleOperation(rejectModeDomain: true);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var failedConnectionState = context.Database.GetDbConnection().State;
        var after = await ReadNativeScopeLifecycleSessionAsync(context.Database.GetDbConnection());
        var targetColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'native_scope_items' AND COLUMN_NAME = 'rejected_value';");
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `native_scope_items` WHERE `key` = 1;");

        // Assert
        Assert.Contains("doka_sm_unsupported", AssertOwnedGuardFailure(exception).Message,
            StringComparison.Ordinal);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(ConnectionState.Open, failedConnectionState);
        Assert.Equal(before.ConnectionId, after.ConnectionId);
        Assert.Equal(before.SqlMode, after.SqlMode);
        Assert.Equal(before.SavedSqlMode, after.SavedSqlMode);
        Assert.Equal(leaveStaleSnapshot ? "STRICT_ALL_TABLES" : null, before.SavedSqlMode);
        Assert.Equal(leaveStaleSnapshot ? 1 : 0, observer.CompletedCaptures);
        Assert.Equal(leaveStaleSnapshot ? 1 : 0, observer.CompletedMutations);
        Assert.Equal(leaveStaleSnapshot ? 1 : 0, observer.StartedBodies);
        Assert.Equal(leaveStaleSnapshot ? 2 : 1, observer.CompletedRestores);
        Assert.True(after.AcquisitionFlagCleared);
        Assert.True(after.DecisionVariablesCleared);
        Assert.Equal(0, targetColumns);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>
    /// Errors and caller cancellation preserve the same session before acquisition and restore it after acquisition.
    /// </summary>
    /// <param name="faultPoint">The actual native setup boundary interrupted by the probe.</param>
    /// <param name="failureMode">Whether the probe raises a native error or cancels the caller token.</param>
    [Theory]
    [InlineData("before-capture", "error")]
    [InlineData("before-capture", "cancellation")]
    [InlineData("after-acquisition", "error")]
    [InlineData("after-acquisition", "cancellation")]
    public async Task NativeCommentScopeFaultRestoresOnlyAcquiredSession(string faultPoint, string failureMode)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, NativeScopeLifecycleFixtureSql);
        using var cancellation = new CancellationTokenSource();
        var observer = new NativeScopeLifecycleObserver(faultPoint, failureMode, cancellation);
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await NativeScopeLifecycleSessionSqlAsync(context.Database.GetDbConnection(),
            "SET SESSION sql_mode = 'STRICT_ALL_TABLES'; SET @__doka_previous_sql_mode = NULL;");
        var before = await ReadNativeScopeLifecycleSessionAsync(context.Database.GetDbConnection());
        var operation = NativeScopeLifecycleOperation(rejectModeDomain: false);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], cancellation.Token));

        var failedConnectionState = context.Database.GetDbConnection().State;
        var after = await ReadNativeScopeLifecycleSessionAsync(context.Database.GetDbConnection());
        var targetColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'native_scope_items' AND COLUMN_NAME = 'prior_value';");
        var preservedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `native_scope_items` WHERE `key` = 1;");

        // Assert
        if (failureMode == "cancellation")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
            Assert.True(cancellation.IsCancellationRequested);
        }
        else
        {
            Assert.Equal(1146, Assert.IsType<MySqlException>(exception).Number);
        }

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.True(observer.FaultInjected);
        Assert.True(observer.FaultCheckpointObserved);
        Assert.Equal(faultPoint == "after-acquisition" ? 1 : 0, observer.CompletedCaptures);
        Assert.Equal(faultPoint == "after-acquisition" ? 1 : 0, observer.CompletedMutations);
        Assert.Equal(faultPoint == "after-acquisition" ? before.SqlMode : null, observer.AcquiredSqlMode);
        Assert.Equal(0, observer.StartedBodies);
        Assert.Equal(1, observer.CompletedRestores);
        Assert.Equal(ConnectionState.Open, failedConnectionState);
        Assert.Equal(before.ConnectionId, after.ConnectionId);
        Assert.Equal(before.SqlMode, after.SqlMode);
        Assert.True(after.AcquisitionFlagCleared);
        Assert.True(after.DecisionVariablesCleared);
        Assert.Equal(0, targetColumns);
        Assert.Equal(1, preservedRows);
    }

    /// <summary>
    /// A genuine native restoration failure remains fatal and quarantines the original pooled session.
    /// </summary>
    [Fact]
    public async Task NativeCommentScopeRestoreFailureQuarantinesSession()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var pooledConnectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            Pooling = true,
        }.ConnectionString;

        await ExecuteSqlAsync(pooledConnectionString, NativeScopeLifecycleFixtureSql);
        var observer = new NativeScopeLifecycleObserver(failRestore: true);
        await using var context = CreateRuntimeContext(pooledConnectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await NativeScopeLifecycleSessionSqlAsync(context.Database.GetDbConnection(),
            "SET SESSION sql_mode = 'STRICT_ALL_TABLES'; SET @__doka_previous_sql_mode = NULL;");
        var before = await ReadNativeScopeLifecycleSessionAsync(context.Database.GetDbConnection());
        var operation = NativeScopeLifecycleOperation(rejectModeDomain: false);

        // Act
        var exception = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        // WHY: Inspect closure before checking out any replacement. Reopening the
        // failed context connection would hide a lost-session cleanup regression.
        var failedConnectionState = context.Database.GetDbConnection().State;
        await using var replacement = new MySqlConnection(pooledConnectionString);
        await replacement.OpenAsync(CancellationToken.None);
        var replacementSession = await ReadNativeScopeLifecycleSessionAsync(replacement);
        var targetColumns = await ScalarIntAsync(pooledConnectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'native_scope_items' AND COLUMN_NAME = 'prior_value' "
            + "AND COLUMN_COMMENT = " + AtomicRequiredAddLiteral(NativeScopeLifecycleComment) + ";");
        var preservedRows = await ScalarIntAsync(pooledConnectionString,
            "SELECT COUNT(*) FROM `native_scope_items` WHERE `key` = 1;");

        // Assert
        var cleanupException = Assert.IsType<InvalidOperationException>(exception, exactMatch: false);

        Assert.Equal("Doka.EntityFrameworkCore.MySql.MySqlMigrationSessionCleanupException",
            cleanupException.GetType().FullName);
        Assert.Equal(1231, Assert.IsType<MySqlException>(cleanupException.InnerException).Number);
        Assert.NotNull(before);
        Assert.NotNull(replacementSession);
        Assert.True(observer.RestoreFailureInjected);
        Assert.Equal(1, observer.CompletedCaptures);
        Assert.Equal(1, observer.CompletedMutations);
        Assert.Equal(before.SqlMode, observer.AcquiredSqlMode);
        Assert.Equal(1, observer.StartedBodies);
        Assert.Equal(0, observer.CompletedRestores);
        Assert.NotNull(observer.FinalCleanupSession);
        Assert.True(observer.FinalCleanupSession.AcquisitionFlagCleared);
        Assert.True(observer.FinalCleanupSession.DecisionVariablesCleared);
        Assert.Equal(ConnectionState.Closed, failedConnectionState);
        Assert.NotEqual(before.ConnectionId, replacementSession.ConnectionId);
        Assert.Equal(1, targetColumns);
        Assert.Equal(1, preservedRows);
    }

    private const string NativeScopeLifecycleComment = "native\\scope";
    private const string NativeScopeLifecycleFixtureSql =
        "CREATE TABLE `native_scope_items` (`key` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
        + "INSERT INTO `native_scope_items` (`key`) VALUES (1);";

    /// <summary>Creates an ordinary native comment scope or an unsupported native ENUM mode-domain decision.</summary>
    /// <param name="rejectModeDomain">Whether the native comment scope changes the authored ENUM member domain.</param>
    /// <returns>The immutable operation whose native baseline needs backslash-safe comment handling.</returns>
    private static SafeMigrationOperation NativeScopeLifecycleOperation(bool rejectModeDomain)
    {
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        if (rejectModeDomain)
        {
            // WHY: Native comment setup would change this member's decoding.
            // Its mode guard must reject before acquiring any provider snapshot.
            builder.AddColumnIfNotExists<string>(name: "rejected_value", table: "native_scope_items",
                type: "enum('first\\nsecond','other')", nullable: true, comment: NativeScopeLifecycleComment);
        }
        else
        {
            builder.AddColumnIfNotExists<string>(name: "prior_value", table: "native_scope_items",
                type: "varchar(20)", maxLength: 20, nullable: true, comment: NativeScopeLifecycleComment);
        }

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }

    /// <summary>Executes test-owned session setup directly without reentering the EF interceptor.</summary>
    /// <param name="connection">The already open migration connection.</param>
    /// <param name="sql">The closed fixture or native fault statement.</param>
    private static async Task NativeScopeLifecycleSessionSqlAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Captures native state without opening or replacing a closed migration connection.</summary>
    /// <param name="connection">The connection whose existing session must be observed.</param>
    /// <returns>The original open-session evidence, or NULL when cleanup has already closed it.</returns>
    private static async Task<NativeScopeLifecycleSession?> ReadNativeScopeLifecycleSessionAsync(
        DbConnection connection
    )
    {
        if (connection.State != ConnectionState.Open)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@SESSION.sql_mode, CONNECTION_ID(), @__doka_previous_sql_mode, "
            + "@doka_sm_provider_setup_entered IS NULL, @doka_sm_state IS NULL AND @doka_sm_action IS NULL;";

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            throw new InvalidOperationException("The native lifecycle proof returned no row.");
        }

        return new NativeScopeLifecycleSession(reader.GetString(0),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) == 1,
            Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture) == 1);
    }

    /// <summary>Retains only the bounded same-session facts needed to prove scope ownership and restoration.</summary>
    /// <param name="SqlMode">The observed session mode.</param>
    /// <param name="ConnectionId">The native physical session identity.</param>
    /// <param name="SavedSqlMode">The provider's potentially unowned earlier snapshot.</param>
    /// <param name="AcquisitionFlagCleared">Whether final cleanup released the owned acquisition flag.</param>
    /// <param name="DecisionVariablesCleared">Whether final cleanup released the owned decision state.</param>
    private sealed record NativeScopeLifecycleSession(
        string SqlMode,
        long ConnectionId,
        string? SavedSqlMode,
        bool AcquisitionFlagCleared,
        bool DecisionVariablesCleared
    );

    /// <summary>Interrupts the exact native acquisition boundaries without sleeps or product-only test hooks.</summary>
    /// <param name="faultPoint">The before-capture or after-acquisition boundary to interrupt, when requested.</param>
    /// <param name="failureMode">The native error or cancellation raised at the chosen boundary.</param>
    /// <param name="cancellation">The caller's token source for deterministic cancellation probes.</param>
    /// <param name="failRestore">Whether the actual native restoration must fail instead of setup.</param>
    private sealed class NativeScopeLifecycleObserver(
        string? faultPoint = null,
        string? failureMode = null,
        CancellationTokenSource? cancellation = null,
        bool failRestore = false
    ) : DbCommandInterceptor
    {
        private const string CapturePrefix = "/*! SET @__doka_previous_sql_mode = @@SESSION.sql_mode";
        private const string MutationPrefix = "/*! SET SESSION sql_mode = IF("
            + "FIND_IN_SET('NO_BACKSLASH_ESCAPES'";
        private const string RestorePrefix = "/*! SET SESSION sql_mode = IF("
            + "COALESCE(@doka_sm_provider_setup_entered = 1";

        /// <summary>Gets the number of actual completed native snapshot statements.</summary>
        public int CompletedCaptures { get; private set; }

        /// <summary>Gets the number of actual completed native mode mutations.</summary>
        public int CompletedMutations { get; private set; }

        /// <summary>Gets the number of actual completed conditional native restores.</summary>
        public int CompletedRestores { get; private set; }

        /// <summary>Gets the number of guarded bodies dispatched after setup.</summary>
        public int StartedBodies { get; private set; }

        /// <summary>Gets whether the chosen native boundary was actually interrupted.</summary>
        public bool FaultInjected { get; private set; }

        /// <summary>Gets whether native state proves the intended before/after acquisition boundary.</summary>
        public bool FaultCheckpointObserved { get; private set; }

        /// <summary>Gets the real snapshot value read only after its native acquisition completed.</summary>
        public string? AcquiredSqlMode { get; private set; }

        /// <summary>Gets whether a real conditional restore command received the native failure probe.</summary>
        public bool RestoreFailureInjected { get; private set; }

        /// <summary>Gets independent guard cleanup evidence captured before quarantine closes the session.</summary>
        public NativeScopeLifecycleSession? FinalCleanupSession { get; private set; }

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            var text = command.CommandText.TrimStart();
            var connection = command.Connection
                ?? throw new InvalidOperationException("The native lifecycle command lost its connection.");

            if (text.StartsWith(CapturePrefix, StringComparison.Ordinal)
                && faultPoint == "before-capture" && !FaultInjected)
            {
                await using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "SELECT COALESCE(@doka_sm_provider_setup_entered = 0, FALSE);";
                FaultCheckpointObserved = Convert.ToInt32(
                    await checkpoint.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture) == 1;

                await InjectFaultAsync(connection);
            }

            if (text.Contains("SET @doka_sm_post_ok", StringComparison.Ordinal))
            {
                StartedBodies++;
            }

            if (failRestore && !RestoreFailureInjected && text.StartsWith(RestorePrefix, StringComparison.Ordinal))
            {
                // WHY: Fail the actual restoration assignment, not an unrelated
                // statement after successful restore; quarantine must remain mandatory.
                command.CommandText = "/*! SET SESSION sql_mode = 'doka_invalid_native_scope_mode' */;";
                RestoreFailureInjected = true;
            }

            return result;
        }

        /// <inheritdoc />
        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            var text = command.CommandText.TrimStart();
            var connection = command.Connection
                ?? throw new InvalidOperationException("The native lifecycle command lost its connection.");

            if (text.StartsWith(CapturePrefix, StringComparison.Ordinal))
            {
                CompletedCaptures++;
                var acquired = await ReadNativeScopeLifecycleSessionAsync(connection);
                AcquiredSqlMode = acquired?.SavedSqlMode;
            }

            if (text.StartsWith(MutationPrefix, StringComparison.Ordinal))
            {
                CompletedMutations++;
                if (faultPoint == "after-acquisition" && !FaultInjected)
                {
                    await using var checkpoint = connection.CreateCommand();
                    checkpoint.CommandText = "SELECT COALESCE(@doka_sm_provider_setup_entered = 1 "
                        + "AND FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0, FALSE);";
                    FaultCheckpointObserved = Convert.ToInt32(
                        await checkpoint.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture) == 1;

                    // WHY: The real snapshot and real mode mutation have both
                    // completed before interruption; cleanup must restore despite cancellation.
                    await InjectFaultAsync(connection);
                }
            }

            if (text.StartsWith(RestorePrefix, StringComparison.Ordinal))
            {
                CompletedRestores++;
            }

            if (text.Contains("SET @doka_sm_state = NULL, @doka_sm_action = NULL", StringComparison.Ordinal))
            {
                FinalCleanupSession = await ReadNativeScopeLifecycleSessionAsync(connection);
            }

            return result;
        }

        /// <summary>Raises one native error or deterministic caller cancellation at the observed boundary.</summary>
        /// <param name="connection">The original already open native session.</param>
        private async Task InjectFaultAsync(DbConnection connection)
        {
            FaultInjected = true;
            if (failureMode == "cancellation")
            {
                var source = cancellation
                    ?? throw new InvalidOperationException("The cancellation probe has no caller token source.");

                source.Cancel();
                source.Token.ThrowIfCancellationRequested();
            }

            await NativeScopeLifecycleSessionSqlAsync(connection,
                "SELECT * FROM `native_scope_missing_fault_probe`;");
        }
    }
}
