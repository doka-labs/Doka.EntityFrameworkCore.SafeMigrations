namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies cancellation after a native default proof has acquired real session-local state.</summary>
public sealed class MySqlBinaryDefaultProofRecoveryIntegrationTests : MySqlIntegrationTestBase
{
    private const string ClearedVariables = "SELECT @doka_sm_binary_default_0 IS NULL "
        + "AND @doka_sm_state IS NULL AND @doka_sm_action IS NULL AND @doka_sm_repair_ok IS NULL "
        + "AND @doka_sm_prerequisite_ok IS NULL AND @doka_sm_sql IS NULL AND @doka_sm_post_ok IS NULL "
        + "AND @doka_sm_data_probe_required IS NULL AND @doka_sm_data_blocked IS NULL "
        + "AND @doka_sm_transition_eligible IS NULL AND @doka_sm_nullability_blocked IS NULL "
        + "AND @doka_sm_column_repair_eligible IS NULL;";

    /// <summary>Shares only the native engine and retains per-test database cleanup ownership.</summary>
    /// <param name="fixture">The lazily activated assembly-owned native engine.</param>
    public MySqlBinaryDefaultProofRecoveryIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// A completed proof is cleared after cancellation before the guarded body, then safely reacquired.
    /// </summary>
    [MySqlBinaryDefaultFact]
    public async Task AcquiredBinaryDefaultProofCancellationCleansAndRecoversSameSession()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `binary_default_recovery` (`id` int NOT NULL, "
            + "`value` varbinary(4) NOT NULL DEFAULT X'A0B1'); "
            + "INSERT INTO `binary_default_recovery` VALUES (1, X'1020');");
        using var cancellation = new CancellationTokenSource();
        var observer = new AcquiredBinaryDefaultCancellationInterceptor(cancellation);
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseMySql(connectionString, Fixture.ServerVersion)
            .UseMySqlSafeMigrations<SafeMigrationDbContext>()
            .AddInterceptors(observer)
            .Options;

        await using var context = new SafeMigrationDbContext(options);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var connection = context.Database.GetDbConnection();
        var sessionBefore = await SessionScalarAsync(connection, "SELECT CONNECTION_ID();");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("binary_default_recovery",
            new ExpectedColumnDefinition("value", typeof(byte[]), false, "varbinary(4)", maxLength: 4,
                defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 0xA0, 0xB1 })),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations,
            cancellation.Token));

        var bodiesBeforeRetry = observer.GuardedBodiesStarted;
        var variablesAfterCancellation = await SessionScalarAsync(connection, ClearedVariables);
        var temporaryTableError = await ResourceFailureCodeAsync(connection,
            "SELECT 1 FROM `__doka_sm_assert` LIMIT 1;");
        var preparedStatementError = await ResourceFailureCodeAsync(connection,
            "DEALLOCATE PREPARE doka_sm_statement;");
        var sessionAfterCancellation = await SessionScalarAsync(connection, "SELECT CONNECTION_ID();");
        var preservedBeforeRetry = await SessionScalarAsync(connection,
            "SELECT COUNT(*) FROM `binary_default_recovery` WHERE `id` = 1 AND `value` = X'1020';");

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var variablesAfterRetry = await SessionScalarAsync(connection, ClearedVariables);
        var sessionAfterRetry = await SessionScalarAsync(connection, "SELECT CONNECTION_ID();");
        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO `binary_default_recovery` (`id`) VALUES (2);";
        await insert.ExecuteNonQueryAsync(CancellationToken.None);
        var defaultRows = await SessionScalarAsync(connection,
            "SELECT COUNT(*) FROM `binary_default_recovery` WHERE `id` = 2 "
            + "AND HEX(`value`) = 'A0B1';");

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.True(observer.CancellationTriggered);
        Assert.False(observer.WasCancelledBeforeProof);
        Assert.Equal(1, observer.AcquiredProofValue);
        Assert.Equal(sessionBefore, observer.AcquiredSessionId);
        Assert.Equal(0, bodiesBeforeRetry);
        Assert.Equal(1, variablesAfterCancellation);
        Assert.Equal(1146, temporaryTableError);
        Assert.Equal(1243, preparedStatementError);
        Assert.Equal(sessionBefore, sessionAfterCancellation);
        Assert.Equal(1, preservedBeforeRetry);
        Assert.Equal(sessionBefore, sessionAfterRetry);
        Assert.Equal(1, variablesAfterRetry);
        Assert.True(observer.GuardedBodiesStarted > bodiesBeforeRetry);
        Assert.Equal(1, defaultRows);
    }

    /// <summary>
    /// Reads directly from the retained physical session without invoking EF interception recursively.
    /// </summary>
    /// <param name="connection">The already open migration session.</param>
    /// <param name="sql">A test-owned scalar query containing no application or connection information.</param>
    /// <returns>The scalar value converted without dependence on provider integer-width metadata.</returns>
    private static async Task<long> SessionScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Requires the server's resource-absence error rather than inferring cleanup from a successful retry.
    /// </summary>
    /// <param name="connection">The unchanged physical migration connection.</param>
    /// <param name="sql">The direct resource-absence probe.</param>
    /// <returns>The native error code, or zero when the supposedly released resource still exists.</returns>
    private static async Task<int> ResourceFailureCodeAsync(DbConnection connection, string sql)
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

    /// <summary>Cancels only after a real completed proof has made operation-local evidence available.</summary>
    private sealed class AcquiredBinaryDefaultCancellationInterceptor(CancellationTokenSource cancellation)
        : DbCommandInterceptor
    {
        private const string ProofReset = "SET @doka_sm_binary_default_0 = FALSE;";
        private const string PreparedExecution = "EXECUTE doka_sm_statement;";

        /// <summary>Gets the observed number of guarded bodies dispatched on this retained session.</summary>
        public int GuardedBodiesStarted { get; private set; }

        /// <summary>Gets whether this interceptor cancelled the caller after proof acquisition.</summary>
        public bool CancellationTriggered { get; private set; }

        /// <summary>Gets whether the caller was already cancelled before the proof could be observed.</summary>
        public bool WasCancelledBeforeProof { get; private set; }

        /// <summary>Gets the real proof value read after the native prepared SELECT completed.</summary>
        public long? AcquiredProofValue { get; private set; }

        /// <summary>Gets the physical session that materialized the observed proof.</summary>
        public long? AcquiredSessionId { get; private set; }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SET @doka_sm_post_ok", StringComparison.Ordinal))
            {
                GuardedBodiesStarted++;
            }

            return ValueTask.FromResult(result);
        }

        /// <inheritdoc />
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (CancellationTriggered
                || !command.CommandText.Contains(ProofReset, StringComparison.Ordinal)
                || !command.CommandText.Contains(PreparedExecution, StringComparison.Ordinal)
                || command.CommandText.Contains("SET @doka_sm_post_ok", StringComparison.Ordinal))
            {
                return result;
            }

            // WHY: Cancellation after dispatch alone is not proof of acquired
            // state. Observe the completed native result first; no timer or
            // CPU-dependent delay decides whether this test reaches its target.
            WasCancelledBeforeProof = cancellation.IsCancellationRequested;
            var connection = command.Connection
                ?? throw new InvalidOperationException("The binary-default proof lost its physical session.");

            await using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT @doka_sm_binary_default_0, CONNECTION_ID();";
            await using var reader = await probe.ExecuteReaderAsync(CancellationToken.None);
            if (!await reader.ReadAsync(CancellationToken.None))
            {
                throw new InvalidOperationException("The completed binary-default proof returned no session state.");
            }

            AcquiredProofValue = reader.IsDBNull(0)
                ? null
                : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);

            AcquiredSessionId = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (AcquiredProofValue != 1 || WasCancelledBeforeProof)
            {
                throw new InvalidOperationException(
                    "Cancellation requires a freshly completed matching default proof.");
            }

            CancellationTriggered = true;
            cancellation.Cancel();

            return result;
        }
    }

    /// <summary>Skips this native MySQL-only proof contract before an unsupported engine fixture starts.</summary>
    private sealed class MySqlBinaryDefaultFactAttribute : FactAttribute
    {
        /// <summary>Uses the fixture's explicit engine selector rather than treating MariaDB as equivalent.</summary>
        public MySqlBinaryDefaultFactAttribute()
        {
            var engine = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_ENGINE")?.Trim();
            if (!string.Equals(engine, "mysql", StringComparison.OrdinalIgnoreCase))
            {
                Skip = "The operation-local native binary-default proof is a MySQL-specific runtime contract.";
            }
        }
    }
}
