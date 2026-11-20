namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>
/// Exercises the ColumnsStructuredExpressions migration contracts in an independent class collection.
/// </summary>
public sealed class MySqlColumnsStructuredExpressionsIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlColumnsStructuredExpressionsIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>
    /// Preserves structured integer-cast semantics through rendering, catalog comparison, and execution.
    /// </summary>
    [Fact]
    public async Task StructuredIntegerCast_ConvergesRoundTripsAndDetectsSemanticDrift()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var definition = StructuredExpressionTable(multiplier: 2);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("structured-integer-cast-preflight"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `structured_expression_columns` (`id`) VALUES (7);",
            cancellationToken: CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("structured-integer-cast-postflight"),
            CancellationToken.None);

        var generatedValue = await ScalarIntAsync(
            connectionString,
            "SELECT `doubled_id` FROM `structured_expression_columns` WHERE `id` = 7;",
            cancellationToken: CancellationToken.None);

        var timestampedRowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM `structured_expression_columns` "
            + "WHERE `id` = 7 AND `created_at` IS NOT NULL;",
            cancellationToken: CancellationToken.None);

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.ConvergeTable(StructuredExpressionTable(multiplier: 3));
        var driftReport = await runner.AnalyzeAsync(
            context,
            drift.Operations,
            new SafeMigrationRunOptions("structured-integer-cast-drift"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(14, generatedValue);

        Assert.Equal(1, timestampedRowCount);

        Assert.Equal(SafeMigrationReportStatus.Blocked, driftReport.Status);
        Assert.Contains(
            driftReport.Assessments,
            assessment => assessment is
            {
                ObjectName: "doubled_id", ObservedState: SafeMigrationObservedState.Different,
            });
    }

    /// <summary>Reports an unsupported structured cast without creating its target table.</summary>
    [Fact]
    public async Task UnsupportedStructuredCastTarget_IsReportedWithoutCreatingTable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(
            new ExpectedTableDefinition(
                "unsupported_structured_cast",
                [
                    new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                    new ExpectedColumnDefinition(
                        "computed_value",
                        typeof(DateTime),
                        true,
                        "datetime(6)",
                        computedExpression: SafeMigrationSql.Cast(
                            SafeMigrationSql.Identifier("id"),
                            "timestamp(6)"),
                        isStored: true),
                ],
                primaryKey: new ExpectedPrimaryKeyDefinition(
                    "pk_unsupported_structured_cast",
                    "unsupported_structured_cast",
                    ["id"])));

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("unsupported-structured-cast"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Contains(
            report.Assessments,
            static assessment => assessment is
            {
                ObservedState: SafeMigrationObservedState.Unsupported, Code: "structured_cast_type",
            });

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'unsupported_structured_cast';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Accepts required generated values only with the qualified native nullability proof.</summary>
    [Fact]
    public async Task NonNullableGeneratedColumnConvergesWithQualifiedNativeSemantics()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(
            new ExpectedTableDefinition(
                "nonnullable_generated_column",
                [
                    new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                    new ExpectedColumnDefinition(
                        "computed_value",
                        typeof(int),
                        false,
                        "int",
                        computedExpression: SafeMigrationSql.Binary(
                            SafeMigrationSql.Identifier("id"),
                            SafeMigrationSqlBinaryOperator.Multiply,
                            SafeMigrationSql.Literal(2)),
                        isStored: true),
                ],
                primaryKey: new ExpectedPrimaryKeyDefinition(
                    "pk_nonnullable_generated_column",
                    "nonnullable_generated_column",
                    ["id"])));

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("nonnullable-generated-column"),
                CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .VerifyAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("nonnullable-generated-column-postflight"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>Requires the server to accept typed NULL expressions for every supported common CAST target.</summary>
    /// <param name="storeType">The independently exercised native CAST target.</param>
    [Theory]
    [InlineData("int")]
    [InlineData("bigint unsigned")]
    [InlineData("decimal(18,4)")]
    [InlineData("float")]
    [InlineData("double")]
    [InlineData("varchar(32)")]
    [InlineData("longtext")]
    [InlineData("binary(16)")]
    [InlineData("date")]
    [InlineData("datetime(6)")]
    [InlineData("time(6)")]
    public async Task CommonCastTargets_RenderTypedNullsAcceptedByTheServer(
        string storeType
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var renderer = new MySqlSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            var expression = renderer.Render(SafeMigrationSql.Literal(null, storeType));

            await ExecuteSqlAsync(connectionString, $"SELECT {expression};", cancellationToken: CancellationToken.None);
        });

        // Assert
        Assert.Null(failure);
    }

    private static ExpectedTableDefinition StructuredExpressionTable(
        int multiplier
    ) => new(
        "structured_expression_columns",
        [
            new ExpectedColumnDefinition("id", typeof(int), false, "int"),
            new ExpectedColumnDefinition(
                "doubled_id",
                typeof(int),
                true,
                "int",
                computedExpression: SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("id"),
                    SafeMigrationSqlBinaryOperator.Multiply,
                    SafeMigrationSql.Literal(multiplier, "int")),
                isStored: true),
            new ExpectedColumnDefinition(
                "created_at",
                typeof(DateTime),
                false,
                "datetime(6)",
                defaultValue: SafeMigrationDefaultValue.Sql(
                    SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp, precision: 6))),
        ],
        primaryKey: new ExpectedPrimaryKeyDefinition(
            "pk_structured_expression_columns",
            "structured_expression_columns",
            ["id"]));
}
