namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task StructuredIntegerCast_ConvergesRoundTripsAndDetectsSemanticDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var definition = StructuredExpressionTable(multiplier: 2);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("structured-integer-cast-preflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `structured_expression_columns` (`id`) VALUES (7);");

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("structured-integer-cast-postflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(
            14,
            await ScalarIntAsync(
                connectionString,
                "SELECT `doubled_id` FROM `structured_expression_columns` WHERE `id` = 7;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `structured_expression_columns` "
                + "WHERE `id` = 7 AND `created_at` IS NOT NULL;"));

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.ConvergeTable(StructuredExpressionTable(multiplier: 3));
        var driftReport = await runner.AnalyzeAsync(
            context,
            drift.Operations,
            new SafeMigrationRunOptions("structured-integer-cast-drift"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, driftReport.Status);
        Assert.Contains(
            driftReport.Assessments,
            assessment => assessment.ObjectName == "doubled_id"
                && assessment.ObservedState == SafeMigrationObservedState.Different);
    }

    [Fact]
    public async Task UnsupportedStructuredCastTarget_IsReportedWithoutCreatingTable()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("unsupported-structured-cast"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Contains(
            report.Assessments,
            static assessment => assessment.ObservedState == SafeMigrationObservedState.Unsupported
                && assessment.Code == "structured_cast_type");
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'unsupported_structured_cast';"));
    }

    [Fact]
    public async Task NonNullableGeneratedColumn_ConvergesOnMySqlAndFailsClosedOnMariaDb()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("nonnullable-generated-column"),
                CancellationToken.None);

        if (Fixture.IsMariaDb)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Contains(
                report.Assessments,
                static assessment => assessment.ObservedState == SafeMigrationObservedState.Unsupported
                    && assessment.Code == "generated_column_nullability");
            Assert.Equal(
                0,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                    + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'nonnullable_generated_column';"));

            return;
        }

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .VerifyAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("nonnullable-generated-column-postflight"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    [Fact]
    public async Task CommonCastTargets_RenderTypedNullsAcceptedByTheServer()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var renderer = new MySqlSafeMigrationSqlExpressionRenderer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());
        var storeTypes = new[]
        {
            "int",
            "bigint unsigned",
            "decimal(18,4)",
            "float",
            "double",
            "varchar(32)",
            "longtext",
            "binary(16)",
            "date",
            "datetime(6)",
            "time(6)",
        };

        foreach (var storeType in storeTypes)
        {
            var expression = renderer.Render(SafeMigrationSql.Literal(null, storeType));

            await ExecuteSqlAsync(connectionString, $"SELECT {expression};");
        }
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
