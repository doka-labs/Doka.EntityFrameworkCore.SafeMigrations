namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ScaffoldedAutoIncrementTable_PreservesGenerationAndIsIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedAutoIncrementTable(builder, "scaffolded_identity");

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `scaffolded_identity` (`display_name`) VALUES ('first');");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("scaffolded-identity"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(
            "auto_increment",
            await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(EXTRA) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'scaffolded_identity' "
                + "AND COLUMN_NAME = 'id';"));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT `id` FROM `scaffolded_identity`;"));
    }

    [Fact]
    public async Task ScaffoldedAutoIncrementTable_RejectsExistingNonGeneratedColumn()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `scaffolded_identity_drift` ("
            + "`id` int NOT NULL, `display_name` varchar(80) NULL, PRIMARY KEY (`id`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedAutoIncrementTable(builder, "scaffolded_identity_drift");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("scaffolded-identity-drift"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            string.Empty,
            await ScalarStringAsync(
                connectionString,
                "SELECT EXTRA FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'scaffolded_identity_drift' "
                + "AND COLUMN_NAME = 'id';"));
    }

    [Fact]
    public async Task ScaffoldedOperationLevelAnnotation_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var operation = AddScaffoldedAutoIncrementTable(builder, "unsupported_scaffolded_annotation");
        operation["Test:UnsupportedOperationFacet"] = true;

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unsupported-operation-facet"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'unsupported_scaffolded_annotation';"));
    }

    [Fact]
    public async Task UnobservableClientGuidStrategy_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.CreateTableIfNotExists(
            name: "unsupported_client_guid",
            columns: table => new
            {
                id = table
                    .Column<Guid>(type: "char(36)", nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.ClientGuid),
            },
            constraints: table => table.PrimaryKey("pk_unsupported_client_guid", value => value.id));

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unsupported-client-guid"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'unsupported_client_guid';"));
    }

    private static SafeMigrationOperation AddScaffoldedAutoIncrementTable(
        MigrationBuilder builder,
        string tableName
    )
    {
        _ = builder.CreateTableIfNotExists(
            name: tableName,
            columns: table => new
            {
                id = table
                    .Column<int>(type: "int", nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.AutoIncrement),
                display_name = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true),
            },
            constraints: table => table.PrimaryKey($"pk_{tableName}", value => value.id));

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }
}
