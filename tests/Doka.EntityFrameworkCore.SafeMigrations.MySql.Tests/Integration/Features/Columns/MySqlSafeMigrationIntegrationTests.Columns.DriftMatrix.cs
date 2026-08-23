namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ObservableColumnFacetDrift_IsRejectedOneFieldAtATime()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `column_facets` (`a` int NOT NULL, `b` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var canonical = new[]
        {
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "fixed_value",
                typeof(string),
                true,
                "char(4)",
                maxLength: 4,
                isFixedLength: true),
            new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "decimal(10,2)",
                precision: 10,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "generated_value",
                typeof(int),
                true,
                "int",
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Add, "b"),
                isStored: true),
        };

        var create = new MigrationBuilder(context.Database.ProviderName!);
        foreach (var definition in canonical)
        {
            create.EnsureColumn("column_facets", definition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteOperationsAsync(context, create.Operations);

        var canonicalReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, create.Operations, new SafeMigrationRunOptions("column-facets-canonical"));

        Assert.Equal(SafeMigrationReportStatus.Ready, canonicalReport.Status);
        Assert.All(
            canonicalReport.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));

        var variants = new[]
        {
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(81)",
                maxLength: 81,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                false,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_general_ci"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "different",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("different")),
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "canonical"),
            new ExpectedColumnDefinition(
                "fixed_value",
                typeof(string),
                true,
                "varchar(4)",
                maxLength: 4,
                isFixedLength: false),
            new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "decimal(11,2)",
                precision: 11,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "decimal(10,3)",
                precision: 10,
                scale: 3,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "generated_value",
                typeof(int),
                true,
                "int",
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Subtract, "b"),
                isStored: true),
            new ExpectedColumnDefinition(
                "generated_value",
                typeof(int),
                true,
                "int",
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Add, "b"),
                isStored: false),
        };

        foreach (var variant in variants)
        {
            var drift = new MigrationBuilder(context.Database.ProviderName!);
            drift.EnsureColumn("column_facets", variant, SafeMigrationPolicy.ThrowIfDifferent);

            var report = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, drift.Operations, new SafeMigrationRunOptions($"column-drift-{variant.Name}"));

            var assessment = Assert.Single(report.Assessments);

            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        }
    }
}
