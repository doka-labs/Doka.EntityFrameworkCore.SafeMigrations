namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ObservableIndexFacetDrift_IsRejectedOneFieldAtATime()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `index_facets` ("
            + "`id` int NOT NULL, `alternate_id` int NOT NULL, `value` varchar(80) NULL);");
        await using var context = CreateContext(connectionString);
        var canonical = new ExpectedIndexDefinition(
            "ix_index_facets",
            "index_facets",
            [
                new ExpectedIndexKeyDefinition(column: "value", descending: true, prefixLength: 12),
                new ExpectedIndexKeyDefinition(column: "id"),
            ],
            method: "BTREE");
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.EnsureIndex(canonical, SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteOperationsAsync(context, create.Operations);

        var variants = new[]
        {
            new ExpectedIndexDefinition(canonical.Name, canonical.Table, canonical.Keys, unique: true, method: "BTREE"),
            new ExpectedIndexDefinition(canonical.Name, canonical.Table, canonical.Keys, method: "HASH"),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [new ExpectedIndexKeyDefinition(column: "value", descending: true, prefixLength: 12)],
                method: "BTREE"),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [
                    new ExpectedIndexKeyDefinition(column: "id"),
                    new ExpectedIndexKeyDefinition(column: "value", descending: true, prefixLength: 12),
                ],
                method: "BTREE"),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [
                    new ExpectedIndexKeyDefinition(column: "alternate_id", descending: true, prefixLength: 12),
                    new ExpectedIndexKeyDefinition(column: "id"),
                ],
                method: "BTREE"),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [
                    new ExpectedIndexKeyDefinition(column: "value", prefixLength: 12),
                    new ExpectedIndexKeyDefinition(column: "id"),
                ],
                method: "BTREE"),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [
                    new ExpectedIndexKeyDefinition(column: "value", descending: true, prefixLength: 13),
                    new ExpectedIndexKeyDefinition(column: "id"),
                ],
                method: "BTREE"),
        };

        foreach (var variant in variants)
        {
            var drift = new MigrationBuilder(context.Database.ProviderName!);
            drift.EnsureIndex(variant, SafeMigrationPolicy.ThrowIfDifferent);

            var report = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, drift.Operations, new SafeMigrationRunOptions("index-facet-drift"));

            var assessment = Assert.Single(report.Assessments);

            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        }
    }

    [Fact]
    public async Task UnsupportedIndexFacetMatrix_FailsClosedBeforeTargetDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `unsupported_index_facets` (`value` varchar(80) NULL);");
        await using var context = CreateContext(connectionString);
        var definitions = new[]
        {
            new ExpectedIndexDefinition(
                "ix_unsupported_filter",
                "unsupported_index_facets",
                [new ExpectedIndexKeyDefinition(column: "value")],
                filter: "value IS NOT NULL"),
            new ExpectedIndexDefinition(
                "ix_unsupported_include",
                "unsupported_index_facets",
                [new ExpectedIndexKeyDefinition(column: "value")],
                includedColumns: ["included_value"]),
            new ExpectedIndexDefinition(
                "ix_unsupported_nulls",
                "unsupported_index_facets",
                [new ExpectedIndexKeyDefinition(column: "value")],
                unique: true,
                nullsDistinct: false),
            new ExpectedIndexDefinition(
                "ix_unsupported_collation",
                "unsupported_index_facets",
                [new ExpectedIndexKeyDefinition(column: "value", collation: "utf8mb4_bin")]),
            new ExpectedIndexDefinition(
                "ix_unsupported_operator",
                "unsupported_index_facets",
                [new ExpectedIndexKeyDefinition(column: "value", operatorClass: "text_pattern_ops")]),
        };

        foreach (var definition in definitions)
        {
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);

            var report = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unsupported-index-facets"));

            var assessment = Assert.Single(report.Assessments);

            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
            Assert.Equal(
                0,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                    + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'unsupported_index_facets' "
                    + $"AND INDEX_NAME = '{definition.Name}';"));
        }
    }

    [Fact]
    public async Task FunctionalIndexExpressionDrift_IsRejectedWhenTheEngineSupportsIt()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `functional_drift` (`value` varchar(80) NULL);");
        await using var context = CreateContext(connectionString);
        var canonical = new ExpectedIndexDefinition(
            "ix_functional_drift",
            "functional_drift",
            [new ExpectedIndexKeyDefinition(expression: "lower(value)")]);
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.EnsureIndex(canonical, SafeMigrationPolicy.ThrowIfDifferent);

        if (Fixture.IsMariaDb)
        {
            var unsupported = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, create.Operations, new SafeMigrationRunOptions("functional-drift"));

            Assert.Equal(
                SafeMigrationObservedState.Unsupported,
                Assert.Single(unsupported.Assessments)
                    .ObservedState);
            return;
        }

        await ExecuteOperationsAsync(context, create.Operations);

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.EnsureIndex(
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [new ExpectedIndexKeyDefinition(expression: "upper(value)")]),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, drift.Operations, new SafeMigrationRunOptions("functional-drift"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);
    }
}
