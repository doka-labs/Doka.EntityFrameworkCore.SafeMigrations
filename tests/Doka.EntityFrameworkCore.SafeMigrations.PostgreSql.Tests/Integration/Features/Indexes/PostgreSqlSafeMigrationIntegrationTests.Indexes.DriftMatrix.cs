namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ObservableIndexFacetDrift_IsRejectedOneFieldAtATime()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE index_facets (value text NULL, alternate text NULL, payload text NULL, other text NULL);");
        await using var context = CreateContext(connectionString);
        var canonical = new ExpectedIndexDefinition(
            "ix_index_facets",
            "index_facets",
            [
                new ExpectedIndexKeyDefinition(
                    structuredExpression: SqlFunction("lower", "value"),
                    sortOrder: SafeMigrationIndexSortOrder.Descending)
            ],
            unique: true,
            structuredFilter: SafeMigrationSql.IsNotNull(SqlColumn("value")),
            includedColumns: ["payload"],
            method: "btree",
            nullsDistinct: true);
        var collated = new ExpectedIndexDefinition(
            "ix_index_facets_collated",
            "index_facets",
            [
                new ExpectedIndexKeyDefinition(
                    column: "value",
                    collation: new SafeMigrationCollationIdentifier("C"),
                    operatorClass: "text_pattern_ops")
            ],
            method: "btree");
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.EnsureIndex(canonical, SafeMigrationPolicy.ThrowIfDifferent);
        create.EnsureIndex(collated, SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteOperationsAsync(context, create.Operations);

        var variants = new[]
        {
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                canonical.Keys,
                unique: false,
                includedColumns: canonical.IncludedColumns,
                method: canonical.Method,
                structuredFilter: canonical.StructuredFilter),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                canonical.Keys,
                unique: true,
                includedColumns: canonical.IncludedColumns,
                method: canonical.Method,
                nullsDistinct: true,
                structuredFilter:
                SqlBinary(
                    SqlColumn("value"),
                    SafeMigrationSqlBinaryOperator.NotEqual,
                    SafeMigrationSql.Literal(string.Empty, "text"))),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                canonical.Keys,
                unique: true,
                includedColumns: ["other"],
                method: canonical.Method,
                nullsDistinct: true,
                structuredFilter: canonical.StructuredFilter),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [new ExpectedIndexKeyDefinition(structuredExpression: SqlFunction("lower", "value"))],
                unique: true,
                includedColumns: canonical.IncludedColumns,
                method: "hash",
                nullsDistinct: true,
                structuredFilter: canonical.StructuredFilter),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [
                    new ExpectedIndexKeyDefinition(
                        structuredExpression: SqlFunction("upper", "value"),
                        sortOrder: SafeMigrationIndexSortOrder.Descending)
                ],
                unique: true,
                includedColumns: canonical.IncludedColumns,
                method: canonical.Method,
                nullsDistinct: true,
                structuredFilter: canonical.StructuredFilter),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [new ExpectedIndexKeyDefinition(structuredExpression: SqlFunction("lower", "value"))],
                unique: true,
                includedColumns: canonical.IncludedColumns,
                method: canonical.Method,
                nullsDistinct: true,
                structuredFilter: canonical.StructuredFilter),
            new ExpectedIndexDefinition(
                collated.Name,
                collated.Table,
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        collation: new SafeMigrationCollationIdentifier("POSIX"),
                        operatorClass: "text_pattern_ops")
                ],
                method: "btree"),
            new ExpectedIndexDefinition(
                collated.Name,
                collated.Table,
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        collation: new SafeMigrationCollationIdentifier("C"),
                        operatorClass: "text_ops")
                ],
                method: "btree"),
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

        var nullSemantics = new MigrationBuilder(context.Database.ProviderName!);
        nullSemantics.EnsureIndex(
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                canonical.Keys,
                unique: true,
                includedColumns: canonical.IncludedColumns,
                method: canonical.Method,
                nullsDistinct: false,
                structuredFilter: canonical.StructuredFilter),
            SafeMigrationPolicy.ThrowIfDifferent);

        var nullReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, nullSemantics.Operations, new SafeMigrationRunOptions("index-null-semantics"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, nullReport.Status);
        Assert.Equal(
            Fixture.ServerVersion.Major < 15
                ? SafeMigrationObservedState.Unsupported
                : SafeMigrationObservedState.Different,
            Assert.Single(nullReport.Assessments)
                .ObservedState);
    }

    [Fact]
    public async Task PrefixLength_IsRejectedAsUnsupportedBeforeTargetDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE unsupported_prefix (value text NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_unsupported_prefix",
                "unsupported_prefix",
                [new ExpectedIndexKeyDefinition(column: "value", prefixLength: 12)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unsupported-prefix"));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class WHERE relname = 'ix_unsupported_prefix';"));
    }
}
