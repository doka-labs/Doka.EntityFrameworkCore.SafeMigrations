namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Theory]
    [InlineData(NpgsqlValueGenerationStrategy.IdentityAlwaysColumn, "a")]
    [InlineData(NpgsqlValueGenerationStrategy.IdentityByDefaultColumn, "d")]
    public async Task ScaffoldedIdentityTable_PreservesGenerationAndIsIdempotent(
        NpgsqlValueGenerationStrategy strategy,
        string expectedIdentity
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedIdentityTable(builder, "scaffolded_identity", strategy);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO scaffolded_identity (display_name) VALUES ('first');");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("scaffolded-identity"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(
            expectedIdentity,
            await ScalarStringAsync(
                connectionString,
                "SELECT a.attidentity FROM pg_catalog.pg_attribute a "
                + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = current_schema() AND c.relname = 'scaffolded_identity' "
                + "AND a.attname = 'id';"));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT id FROM scaffolded_identity;"));
    }

    [Fact]
    public async Task ScaffoldedIdentityTable_RejectsExistingNonGeneratedColumn()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE scaffolded_identity_drift ("
            + "id integer NOT NULL, display_name character varying(80) NULL, "
            + "CONSTRAINT pk_scaffolded_identity_drift PRIMARY KEY (id));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedIdentityTable(builder, "scaffolded_identity_drift");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("scaffolded-identity-drift"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal(
            "none",
            await ScalarStringAsync(
                connectionString,
                "SELECT CASE WHEN a.attidentity = '' THEN 'none' ELSE a.attidentity::text END "
                + "FROM pg_catalog.pg_attribute a "
                + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = current_schema() AND c.relname = 'scaffolded_identity_drift' "
                + "AND a.attname = 'id';"));
    }

    [Fact]
    public async Task ScaffoldedOperationLevelAnnotation_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var operation = AddScaffoldedIdentityTable(builder, "unsupported_scaffolded_annotation");
        operation["Test:UnsupportedOperationFacet"] = true;

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unsupported-operation-facet"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = current_schema() AND c.relname = 'unsupported_scaffolded_annotation';"));
    }

    [Fact]
    public async Task UnmodeledSerialStrategy_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedIdentityTable(
            builder,
            "unsupported_serial_strategy",
            NpgsqlValueGenerationStrategy.SerialColumn);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unsupported-serial"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = current_schema() AND c.relname = 'unsupported_serial_strategy';"));
    }

    private static SafeMigrationOperation AddScaffoldedIdentityTable(
        MigrationBuilder builder,
        string tableName,
        NpgsqlValueGenerationStrategy strategy = NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
    )
    {
        _ = builder.CreateTableIfNotExists(
            name: tableName,
            columns: table => new
            {
                id = table
                    .Column<int>(type: "integer", nullable: false)
                    .Annotation(
                        "Npgsql:ValueGenerationStrategy",
                        strategy),
                display_name = table
                    .Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
            },
            constraints: table => table.PrimaryKey($"pk_{tableName}", value => value.id));

        return Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
    }
}
