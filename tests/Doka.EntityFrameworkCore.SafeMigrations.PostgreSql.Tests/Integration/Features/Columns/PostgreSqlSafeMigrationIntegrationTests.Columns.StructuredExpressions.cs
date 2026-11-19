namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ColumnsStructuredExpressions PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlColumnsStructuredExpressionsIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlColumnsStructuredExpressionsIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Structured Alias Cast Converges And Round Trips Canonical Catalog Type.</summary>
    [Fact]
    public async Task StructuredAliasCast_ConvergesAndRoundTripsCanonicalCatalogType()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("structured-alias-cast-preflight"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("structured-alias-cast-postflight"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(builder.Operations.Count, preflight.Assessments.Count);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(builder.Operations.Count, postflight.Assessments.Count);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>Structured Cast Types Render Typed Nulls Accepted By The Server.</summary>
    /// <param name="storeType">The independently discovered native typed-NULL store type.</param>
    [Theory]
    [InlineData("integer")]
    [InlineData("int4")]
    [InlineData("int4[]")]
    [InlineData("bigint")]
    [InlineData("float4")]
    [InlineData("float8")]
    [InlineData("float")]
    [InlineData("float(1)")]
    [InlineData("float(24)")]
    [InlineData("float(25)")]
    [InlineData("float(53)")]
    [InlineData("float(24)[]")]
    [InlineData("bool")]
    [InlineData("varchar(32)")]
    [InlineData("char(8)")]
    [InlineData("varbit(16)")]
    [InlineData("numeric(18,4)")]
    [InlineData("double precision")]
    [InlineData("text")]
    [InlineData("character varying(32)")]
    [InlineData("bytea")]
    [InlineData("date")]
    [InlineData("timestamp(6)")]
    [InlineData("timestamptz(6)")]
    [InlineData("timestamp(6) without time zone")]
    [InlineData("time(6)")]
    [InlineData("timetz(6)")]
    [InlineData("time(6) without time zone")]
    [InlineData("uuid")]
    public async Task StructuredCastTypes_RenderTypedNullsAcceptedByTheServer(
        string storeType
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var renderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        // Act
        var expression = renderer.Render(SafeMigrationSql.Literal(null, storeType));
        var failure = await Record.ExceptionAsync(() =>
            ExecuteSqlAsync(connectionString, $"SELECT {expression};", cancellationToken: CancellationToken.None));

        // Assert
        Assert.Null(failure);
    }
}
