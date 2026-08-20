namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ObservableColumnFacetDrift_IsRejectedOneFieldAtATime()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE column_facets (a integer NOT NULL, b integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var canonical = new[]
        {
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: "C",
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "fixed_value",
                typeof(string),
                true,
                "character(4)",
                maxLength: 4,
                isFixedLength: true),
            new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "numeric(10,2)",
                precision: 10,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "generated_value",
                typeof(int),
                true,
                "integer",
                computedColumnSql: "a + b",
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
        Assert.All(canonicalReport.Assessments, assessment =>
            Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));

        var variants = new[]
        {
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(81)",
                maxLength: 81,
                collation: "C",
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                false,
                "character varying(80)",
                maxLength: 80,
                collation: "C",
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: "POSIX",
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: "C",
                comment: "different",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: "C",
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("different")),
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: "C",
                comment: "canonical"),
            new ExpectedColumnDefinition(
                "fixed_value",
                typeof(string),
                true,
                "character varying(4)",
                maxLength: 4,
                isFixedLength: false),
            new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "numeric(11,2)",
                precision: 11,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "numeric(10,3)",
                precision: 10,
                scale: 3,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "generated_value",
                typeof(int),
                true,
                "integer",
                computedColumnSql: "a - b",
                isStored: true),
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
