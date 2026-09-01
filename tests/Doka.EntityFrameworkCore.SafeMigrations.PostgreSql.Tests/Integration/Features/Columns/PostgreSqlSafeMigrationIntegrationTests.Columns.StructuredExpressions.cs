namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task StructuredAliasCast_ConvergesAndRoundTripsCanonicalCatalogType()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(
            new ExpectedTableDefinition(
                "structured_alias_cast",
                [
                    new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                    new ExpectedColumnDefinition(
                        "normalized_id",
                        typeof(long),
                        true,
                        "bigint",
                        computedExpression: SafeMigrationSql.Cast(
                            SafeMigrationSql.Identifier("id"),
                            "int8"),
                        isStored: true),
                ],
                primaryKey: new ExpectedPrimaryKeyDefinition(
                    "pk_structured_alias_cast",
                    "structured_alias_cast",
                    ["id"])));

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("structured-alias-cast-preflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("structured-alias-cast-postflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    [Fact]
    public async Task StructuredCastTypes_RenderTypedNullsAcceptedByTheServer()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var storeTypes = new[]
        {
            "integer",
            "int4",
            "int4[]",
            "bigint",
            "float4",
            "float8",
            "float",
            "float(1)",
            "float(24)",
            "float(25)",
            "float(53)",
            "float(24)[]",
            "bool",
            "varchar(32)",
            "char(8)",
            "varbit(16)",
            "numeric(18,4)",
            "double precision",
            "text",
            "character varying(32)",
            "bytea",
            "date",
            "timestamp(6)",
            "timestamptz(6)",
            "timestamp(6) without time zone",
            "time(6)",
            "timetz(6)",
            "time(6) without time zone",
            "uuid",
        };

        foreach (var storeType in storeTypes)
        {
            var expression = renderer.Render(SafeMigrationSql.Literal(null, storeType));

            await ExecuteSqlAsync(connectionString, $"SELECT {expression};");
        }
    }
}
