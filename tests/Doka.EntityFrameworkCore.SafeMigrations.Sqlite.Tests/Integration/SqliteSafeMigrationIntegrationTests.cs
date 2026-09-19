namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqliteSafeMigrationIntegrationTests : SqliteIntegrationTestBase
{
    [Fact]
    public async Task GranularTableColumnAndIndexLifecycle_AppliesReplaysAndVerifies()
    {
        await using var connection = await OpenConnectionAsync();
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

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-lifecycle"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-lifecycle"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('module_state') WHERE name = 'display_name';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_index_list('module_state') "
                + "WHERE name = 'ix_module_state_display_name';"));
    }

    [Fact]
    public async Task ExistingDefinitionDrift_IsRejectedWithoutMutation()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE drift_state (id INTEGER NOT NULL, payload TEXT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>(
            "payload",
            "drift_state",
            type: "INTEGER",
            nullable: false);
        var runner = context.GetService<ISafeMigrationRunner>();

        var report = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-drift"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('drift_state') "
                + "WHERE name = 'payload' AND type = 'TEXT' AND \"notnull\" = 0;"));
    }

    [Fact]
    public async Task RequiredColumnOnPopulatedTable_IsDataBlockedBeforeDdl()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE populated_state (id INTEGER NOT NULL); INSERT INTO populated_state VALUES (1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>(
            "required_value",
            "populated_state",
            type: "TEXT",
            nullable: false);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("sqlite-data-blocked"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, assessment.Action);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('populated_state') WHERE name = 'required_value';"));
    }

    [Fact]
    public async Task ForeignDatabaseQualifier_IsRejectedBeforeCatalogMutation()
    {
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "qualified_state",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
            },
            schema: "attached");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("sqlite-qualifier"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("database_qualifier_mismatch", assessment.AnalysisCode);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'qualified_state';"));
    }

    [Fact]
    public async Task SafeSqlScriptGeneration_FailsBeforeReturningPartialOutput()
    {
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "script_state",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
            });
        var generator = context.GetService<IMigrationsSqlGenerator>();

        var exception = Assert.Throws<NotSupportedException>(() => generator.Generate(
            builder.Operations,
            context.Model,
            MigrationsSqlGenerationOptions.Script));

        Assert.Contains("cannot express", exception.Message, StringComparison.Ordinal);
    }
}
