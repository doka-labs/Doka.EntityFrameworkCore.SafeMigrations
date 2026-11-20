namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Provides isolated SQLite coverage for safe migration integration.
/// </summary>
public sealed class SqliteSafeMigrationIntegrationTests : SqliteIntegrationTestBase
{
    /// <summary>
    /// Data preparation SQL defers preflight and allows guarded unique index.
    /// </summary>
    [Fact]
    public async Task DataPreparationSql_DefersPreflightAndAllowsGuardedUniqueIndex()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE mixed_records (id INTEGER NOT NULL PRIMARY KEY, code TEXT NOT
            NULL); INSERT INTO mixed_records VALUES (1,
            'duplicate'), (2, 'duplicate');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("UPDATE mixed_records SET code = code || id WHERE code = 'duplicate';");
        builder.CreateIndexIfNotExists("ux_mixed_records_code", "mixed_records", ["code"], unique: true);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-opaque-sql"),
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var indexCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_index_list('mixed_records') "
            + "WHERE name = 'ux_mixed_records_code';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.Equal(1, indexCount);
    }

    /// <summary>
    /// Data regression SQL defers preflight but runtime guard rejects unique index.
    /// </summary>
    [Fact]
    public async Task DataRegressionSql_DefersPreflightButRuntimeGuardRejectsUniqueIndex()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE mixed_records (id INTEGER NOT NULL PRIMARY KEY, code TEXT NOT
            NULL); INSERT INTO mixed_records VALUES (1,
            'first'), (2, 'second');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("UPDATE mixed_records SET code = 'duplicate';");
        builder.CreateIndexIfNotExists("ux_mixed_records_code", "mixed_records", ["code"], unique: true);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-opaque-sql-reject"),
            cancellationToken: CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            builder.Operations,
            cancellationToken: CancellationToken.None));

        var indexCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_index_list('mixed_records') "
            + "WHERE name = 'ux_mixed_records_code';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        var rejection = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("data_blocked", rejection.Message, StringComparison.Ordinal);
        Assert.Equal(0, indexCount);
    }

    /// <summary>
    /// Granular table column and index lifecycle applies replays and verifies.
    /// </summary>
    [Fact]
    public async Task GranularTableColumnAndIndexLifecycle_AppliesReplaysAndVerifies()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "module_state",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_module_state", value => value.Id),
            mode: SafeMigrationTableMode.ConvergenceContainer,
            policy: SafeMigrationPolicy.ExistenceOnly);

        builder.AddColumnIfNotExists<string>(
            "display_name",
            "module_state",
            type: "TEXT",
            nullable: true);

        builder.CreateIndexIfNotExists(
            "ix_module_state_display_name",
            "module_state",
            ["display_name"]);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-lifecycle"),
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-lifecycle"),
            cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('module_state') WHERE name = 'display_name';",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_index_list('module_state') "
            + "WHERE name = 'ix_module_state_display_name';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);
    }

    /// <summary>
    /// Existing definition drift is rejected without mutation.
    /// </summary>
    [Fact]
    public async Task ExistingDefinitionDrift_IsRejectedWithoutMutation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE drift_state (id INTEGER NOT NULL, payload TEXT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>(
            "payload",
            "drift_state",
            type: "INTEGER",
            nullable: false);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-drift"),
            cancellationToken: CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            builder.Operations,
            cancellationToken: CancellationToken.None));

        var retainedColumnCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('drift_state') "
            + "WHERE name = 'payload' AND type = 'TEXT' AND \"notnull\" = 0;",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, retainedColumnCount);
    }

    /// <summary>
    /// Required column on populated table is data blocked before DDL.
    /// </summary>
    [Fact]
    public async Task RequiredColumnOnPopulatedTable_IsDataBlockedBeforeDdl()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE populated_state (id INTEGER NOT NULL); INSERT INTO populated_state VALUES (1);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>(
            "required_value",
            "populated_state",
            type: "TEXT",
            nullable: false);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-data-blocked"),
                cancellationToken: CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            builder.Operations,
            cancellationToken: CancellationToken.None));

        var addedColumnCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('populated_state') WHERE name = 'required_value';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, assessment.Action);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(0, addedColumnCount);
    }

    /// <summary>
    /// Foreign database qualifier is rejected before catalog mutation.
    /// </summary>
    [Fact]
    public async Task ForeignDatabaseQualifier_IsRejectedBeforeCatalogMutation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "qualified_state",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
            },
            schema: "attached");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-qualifier"),
                cancellationToken: CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            builder.Operations,
            cancellationToken: CancellationToken.None));

        var addedTableCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'qualified_state';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("database_qualifier_mismatch", assessment.AnalysisCode);
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(0, addedTableCount);
    }

    /// <summary>
    /// Safe SQL script generation fails before returning partial output.
    /// </summary>
    [Fact]
    public async Task SafeSqlScriptGeneration_FailsBeforeReturningPartialOutput()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "script_state",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
            });

        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var exception = Assert.Throws<NotSupportedException>(() => generator.Generate(
            builder.Operations,
            context.Model,
            MigrationsSqlGenerationOptions.Script));

        // Assert
        Assert.Contains("cannot express", exception.Message, StringComparison.Ordinal);
    }
}
