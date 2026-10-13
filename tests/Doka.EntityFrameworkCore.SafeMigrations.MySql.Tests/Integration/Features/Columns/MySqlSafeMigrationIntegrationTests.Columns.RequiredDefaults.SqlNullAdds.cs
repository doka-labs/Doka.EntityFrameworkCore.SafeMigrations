namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>
    /// Required SQL NULL additions never authorize implicit values for existing rows or permissive sessions.
    /// </summary>
    /// <param name="strictSession">Whether the actual retained migration session uses strict conversion.</param>
    /// <param name="populated">Whether the original owner contains a row which would require a replacement.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RequiredDefaultSqlNullAddQualifiesSessionAndExistingRows(bool strictSession, bool populated)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `sql_null_add_items` (`key` int NOT NULL PRIMARY KEY) ENGINE=InnoDB;"
            + (populated ? " INSERT INTO `sql_null_add_items` (`key`) VALUES (1);" : ""));

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context,
            "SET SESSION sql_mode = '" + (strictSession ? "STRICT_ALL_TABLES" : "") + "';");
        var operation = RequiredDefaultsSqlNullAddOperation();
        var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);
        var qualified = !Fixture.IsMariaDb && strictSession;
        var allowed = qualified && !populated;
        var expectedAction = !qualified
            ? SafeMigrationAction.RejectUnsupported
            : populated ? SafeMigrationAction.RejectDataBlocked : SafeMigrationAction.Apply;

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("required-sql-null-add"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        SafeMigrationRunReport? matching = null;
        Exception? matchingFailure = null;
        if (allowed && failure is null)
        {
            // WHY: A completed exact target has no conversion to perform.
            // The missing-column session guard must not reject its later NoOp.
            await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = '';");
            matching = await runner.AnalyzeAsync(context, [operation],
                new SafeMigrationRunOptions("required-sql-null-add-matching"), CancellationToken.None);

            matchingFailure = await Record.ExceptionAsync(() =>
                ExecuteOperationsAsync(context, [operation], CancellationToken.None));
        }

        var targetColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'sql_null_add_items' AND COLUMN_NAME = 'value';");

        var requiredColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'sql_null_add_items' AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'NO';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `sql_null_add_items`;");
        var originalKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `sql_null_add_items` WHERE `key` = 1;");

        // Assert
        Assert.Equal(allowed ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(expectedAction, Assert.Single(preflight.Assessments).Action);
        if (allowed)
        {
            Assert.Null(failure);
            Assert.NotNull(matching);
            Assert.Equal(SafeMigrationReportStatus.Ready, matching.Status);
            Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(matching.Assessments).Action);
            Assert.Null(matchingFailure);
        }
        else if (qualified)
        {
            // WHY: The structurally known NULL default cannot populate any
            // existing row. Both preflight and the fresh runtime gate reject
            // this case before target DDL rather than relying on its native error.
            Assert.Contains("doka_sm_data_blocked", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("doka_sm_unsupported", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(allowed ? 1 : 0, targetColumns);
        Assert.Equal(allowed ? 1 : 0, requiredColumns);
        Assert.Equal(populated ? 1 : 0, rows);
        Assert.Equal(populated ? 1 : 0, originalKeys);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
        var defaultValue = Assert.IsType<EnsureColumnIntent>(operation.Intent).Definition.DefaultValue;

        Assert.Equal(SafeMigrationDefaultValueKind.Sql, defaultValue.Kind);
        Assert.Null(Assert.IsType<SafeMigrationSqlLiteralExpression>(defaultValue.StructuredExpression).Value);
    }

    /// <summary>
    /// Fresh projected owners use captured SQL NULL add eligibility instead of inventing a safe Missing state.
    /// </summary>
    /// <param name="strictSession">Whether the same session used for analysis and execution is strict.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredDefaultSqlNullAddFreshProjectedOwnerUsesCapturedSession(bool strictSession)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context,
            "SET SESSION sql_mode = '" + (strictSession ? "STRICT_ALL_TABLES" : "") + "';");
        var operation = RequiredDefaultsSqlNullAddOperation();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("sql_null_add_items",
                [new ExpectedColumnDefinition("key", typeof(int), false, "int")],
                primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "sql_null_add_items", ["key"])),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.Operations.Add(operation);
        var allowed = !Fixture.IsMariaDb && strictSession;

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("required-sql-null-projected-add"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var ownerExists = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'sql_null_add_items' AND TABLE_TYPE = 'BASE TABLE';");

        var targetColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'sql_null_add_items' AND COLUMN_NAME = 'value';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `sql_null_add_items`;");

        // Assert
        Assert.Equal(allowed ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(allowed ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[1].Action);
        if (allowed)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Contains("doka_sm_unsupported", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
            if (!Fixture.IsMariaDb)
            {
                Assert.Equal("column_required_sql_add_unavailable", report.Assessments[1].AnalysisCode);
            }
        }

        Assert.Equal(1, ownerExists);
        Assert.Equal(allowed ? 1 : 0, targetColumns);
        Assert.Equal(0, rows);
    }

    /// <summary>
    /// The native strict ADD remains authoritative if an external row arrives after the runtime empty proof.
    /// </summary>
    /// <param name="useNullDefault">
    /// Whether the SQL default is NULL rather than a caller-declared value of seven.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredDefaultSqlNullAddLateWriterCannotReceiveImplicitValues(bool useNullDefault)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `sql_null_add_items` (`key` int NOT NULL PRIMARY KEY) ENGINE=InnoDB;");
        var observer = new RequiredSqlDefaultAddWriter(connectionString);
        await using var context = CreateRuntimeContext(connectionString, observer);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await AtomicRequiredAddSessionSqlAsync(context, "SET SESSION sql_mode = 'STRICT_ALL_TABLES';");
        var operation = useNullDefault
            ? RequiredDefaultsSqlNullAddOperation()
            : new SafeMigrationOperation(new EnsureColumnIntent("sql_null_add_items",
                    new ExpectedColumnDefinition("value", typeof(int), false, "int",
                        defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(7)))),
                SafeMigrationPolicy.RepairIfSafe);

        var originalFingerprint = SafeMigrationContractFingerprint.Create([operation]);
        var earlyUnsupported = useNullDefault && Fixture.IsMariaDb;

        // Act
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        var targetColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
            + "AND TABLE_NAME = 'sql_null_add_items' AND COLUMN_NAME = 'value';");
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `sql_null_add_items`;");
        var writerKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `sql_null_add_items` WHERE `key` = 2;");
        var explicitValues = targetColumns == 1
            ? await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM `sql_null_add_items` WHERE `key` = 2 AND `value` = 7;")
            : 0;

        // Assert
        Assert.Equal(!earlyUnsupported, observer.ProvenApplyCheckpointObserved);
        Assert.Equal(!earlyUnsupported, observer.WasInjected);
        Assert.Equal(earlyUnsupported ? 0 : 1, observer.CommittedRows);
        if (earlyUnsupported)
        {
            Assert.Contains("doka_sm_unsupported", Assert.IsType<MySqlException>(failure).Message,
                StringComparison.Ordinal);
        }
        else if (useNullDefault)
        {
            Assert.Equal(1048, Assert.IsType<MySqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(useNullDefault ? 0 : 1, targetColumns);
        Assert.Equal(earlyUnsupported ? 0 : 1, rows);
        Assert.Equal(earlyUnsupported ? 0 : 1, writerKeys);
        Assert.Equal(useNullDefault ? 0 : 1, explicitValues);
        Assert.Equal(originalFingerprint, SafeMigrationContractFingerprint.Create([operation]));
    }

    /// <summary>Writes exactly after the runtime missing/apply proof and before the real native required ADD.</summary>
    /// <remarks>
    /// WHY: A preflight-only race does not exercise the final proof-to-DDL
    /// interval. The independent committed writer runs at the actual body
    /// checkpoint, without sleeps, session locks or a production test hook.
    /// </remarks>
    private sealed class RequiredSqlDefaultAddWriter : DbCommandInterceptor
    {
        private readonly string _connectionString;

        /// <summary>Initializes the independent writer for one isolated anonymous owner.</summary>
        /// <param name="connectionString">The isolated database connection for the external writer.</param>
        public RequiredSqlDefaultAddWriter(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>Gets whether the real missing/apply runtime checkpoint preceded the write.</summary>
        public bool ProvenApplyCheckpointObserved { get; private set; }

        /// <summary>Gets whether the external row committed before body execution.</summary>
        public bool WasInjected { get; private set; }

        /// <summary>Gets the affected external row count, making the writer control non-vacuous.</summary>
        public int CommittedRows { get; private set; }

        /// <inheritdoc />
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            if (WasInjected
                || !command.CommandText.Contains("SET @doka_sm_post_ok", StringComparison.Ordinal)
                || !command.CommandText.Contains("EXECUTE doka_sm_statement;", StringComparison.Ordinal))
            {
                return result;
            }

            var migrationConnection = command.Connection
                ?? throw new InvalidOperationException("The required ADD body omitted its migration connection.");

            await using var checkpoint = migrationConnection.CreateCommand();
            checkpoint.CommandText = "SELECT COALESCE(@doka_sm_state = 'missing', FALSE) "
                + "AND COALESCE(@doka_sm_action = 'apply', FALSE);";
            ProvenApplyCheckpointObserved = Convert.ToBoolean(
                await checkpoint.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (!ProvenApplyCheckpointObserved)
            {
                throw new InvalidOperationException("The writer did not reach a proven missing-column apply.");
            }

            await using var writer = new MySqlConnection(_connectionString);
            await writer.OpenAsync(cancellationToken);
            await using var insert = writer.CreateCommand();
            insert.CommandText = "INSERT INTO `sql_null_add_items` (`key`) VALUES (2);";
            CommittedRows = await insert.ExecuteNonQueryAsync(cancellationToken);
            WasInjected = true;

            return result;
        }
    }

    /// <summary>Captures a SQL NULL expression without confusing it with a rejected CLR literal NULL default.</summary>
    /// <returns>The immutable required integer addition under explicit repair policy.</returns>
    private static SafeMigrationOperation RequiredDefaultsSqlNullAddOperation()
        => new(new EnsureColumnIntent("sql_null_add_items",
                new ExpectedColumnDefinition("value", typeof(int), false, "int",
                    defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(null)))),
            SafeMigrationPolicy.RepairIfSafe);
}
