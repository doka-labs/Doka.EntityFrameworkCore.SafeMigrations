namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task StructuredLiteralFilterMatrix_ConvergesThroughPostgreSqlCatalogNormalization()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE typed_filters (status text NULL, amount numeric NULL);");
        await using var context = CreateContext(connectionString);
        var definitions = new[]
        {
            new ExpectedIndexDefinition(
                "ix_typed_status",
                "typed_filters",
                [new ExpectedIndexKeyDefinition(column: "status")],
                structuredFilter:
                SqlBinary(
                    SqlColumn("status"),
                    SafeMigrationSqlBinaryOperator.Equal,
                    SafeMigrationSql.Literal("active", "text"))),
            new ExpectedIndexDefinition(
                "ix_typed_amount",
                "typed_filters",
                [new ExpectedIndexKeyDefinition(column: "amount")],
                structuredFilter:
                SqlBinary(
                    SqlColumn("amount"),
                    SafeMigrationSqlBinaryOperator.GreaterThan,
                    SafeMigrationSql.Cast(SafeMigrationSql.Literal(0), "numeric"))),
            new ExpectedIndexDefinition(
                "ix_typed_in",
                "typed_filters",
                [new ExpectedIndexKeyDefinition(column: "status")],
                structuredFilter:
                SafeMigrationSql.In(
                    SqlColumn("status"),
                    [SafeMigrationSql.Literal("active", "text"), SafeMigrationSql.Literal("pending", "text"),])),
            new ExpectedIndexDefinition(
                "ix_typed_not_in",
                "typed_filters",
                [new ExpectedIndexKeyDefinition(column: "status")],
                structuredFilter: SafeMigrationSql.In(
                    SqlColumn("status"),
                    [SafeMigrationSql.Literal("deleted", "text"), SafeMigrationSql.Literal("archived", "text"),],
                    negated: true)),
            new ExpectedIndexDefinition(
                "ix_typed_between",
                "typed_filters",
                [new ExpectedIndexKeyDefinition(column: "amount")],
                structuredFilter: SafeMigrationSql.Between(
                    SqlColumn("amount"),
                    SafeMigrationSql.Cast(SafeMigrationSql.Literal(1), "numeric"),
                    SafeMigrationSql.Cast(SafeMigrationSql.Literal(5), "numeric"))),
        };
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        foreach (var definition in definitions)
        {
            builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("typed-filter-matrix"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(
            report.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_typed_status",
                "typed_filters",
                [new ExpectedIndexKeyDefinition(column: "status")],
                structuredFilter: SqlBinary(
                    SqlColumn("status"),
                    SafeMigrationSqlBinaryOperator.Equal,
                    SafeMigrationSql.Literal("pending", "text"))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var driftReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, drift.Operations, new SafeMigrationRunOptions("typed-filter-drift"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, driftReport.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(driftReport.Assessments)
                .ObservedState);
    }

    [Fact]
    public async Task OrderedIndexMatrix_ConvergesAndPreservesExplicitNullPlacement()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE ordered_indexes (value integer NULL);");
        await using var context = CreateContext(connectionString);
        var orderings = new[]
        {
            (Name: "provider_default", Sort: SafeMigrationIndexSortOrder.ProviderDefault,
                Nulls: SafeMigrationIndexNullOrder.ProviderDefault),
            (Name: "asc_first", Sort: SafeMigrationIndexSortOrder.Ascending,
                Nulls: SafeMigrationIndexNullOrder.First),
            (Name: "asc_last", Sort: SafeMigrationIndexSortOrder.Ascending,
                Nulls: SafeMigrationIndexNullOrder.Last),
            (Name: "desc_first", Sort: SafeMigrationIndexSortOrder.Descending,
                Nulls: SafeMigrationIndexNullOrder.First),
            (Name: "desc_last", Sort: SafeMigrationIndexSortOrder.Descending,
                Nulls: SafeMigrationIndexNullOrder.Last),
        };
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        foreach (var (name, sort, nulls) in orderings)
        {
            builder.EnsureIndex(
                new ExpectedIndexDefinition(
                    $"ix_ordered_{name}",
                    "ordered_indexes",
                    [
                        new ExpectedIndexKeyDefinition(
                            column: "value",
                            sortOrder: sort,
                            nullOrder: nulls)
                    ],
                    method: "btree"),
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("ordered-indexes"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(
            report.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));

        foreach (var ordering in orderings.Where(static value =>
                     value.Nulls != SafeMigrationIndexNullOrder.ProviderDefault))
        {
            var expectedProperty = ordering.Nulls == SafeMigrationIndexNullOrder.First ? "nulls_first" : "nulls_last";
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT CASE WHEN pg_catalog.pg_index_column_has_property(i.indexrelid, 1, "
                    + $"'{expectedProperty}') IS TRUE THEN 1 ELSE 0 END "
                    + "FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid "
                    + $"WHERE c.relname = 'ix_ordered_{ordering.Name}';"));
        }
    }

    [Fact]
    public async Task ExplicitOrderingOnNonOrderableAccessMethod_IsUnsupportedBeforeTargetDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE unordered_index (value integer NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_unordered_hash",
                "unordered_index",
                [new ExpectedIndexKeyDefinition(column: "value", sortOrder: SafeMigrationIndexSortOrder.Descending)],
                method: "hash"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unordered-index"));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class WHERE relname = 'ix_unordered_hash';"));
    }

    [Fact]
    public async Task AdvancedIndexFacets_ConvergeAndDetectDirectionDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE advanced_indexes (value text NULL, payload text NULL);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedIndexDefinition(
            "ix_advanced_value",
            "advanced_indexes",
            [
                new ExpectedIndexKeyDefinition(
                    structuredExpression: SqlFunction("lower", "value"),
                    sortOrder: SafeMigrationIndexSortOrder.Descending)
            ],
            unique: true,
            structuredFilter: SafeMigrationSql.IsNotNull(SqlColumn("value")),
            includedColumns: ["payload"],
            method: "btree",
            nullsDistinct: false);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_advanced_collation",
                "advanced_indexes",
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        collation: new SafeMigrationCollationIdentifier("C"),
                        operatorClass: "text_pattern_ops"),
                ],
                method: "btree"),
            SafeMigrationPolicy.ThrowIfDifferent);

        if (Fixture.ServerVersion.Major < 15)
        {
            var unsupported = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

            Assert.Equal(SafeMigrationReportStatus.Blocked, unsupported.Status);
            Assert.Contains(
                unsupported.Assessments,
                assessment => assessment.ObservedState == SafeMigrationObservedState.Unsupported);
            var exception =
                await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context, builder.Operations));

            Assert.Equal("P1002", exception.SqlState);
            Assert.Equal("doka_sm_unsupported", exception.MessageText);
            return;
        }

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.EnsureIndex(
            new ExpectedIndexDefinition(
                definition.Name,
                definition.Table,
                [new ExpectedIndexKeyDefinition(structuredExpression: SqlFunction("lower", "value"))],
                unique: true,
                includedColumns: definition.IncludedColumns,
                method: definition.Method,
                nullsDistinct: definition.NullsDistinct,
                structuredFilter: definition.StructuredFilter),
            SafeMigrationPolicy.ThrowIfDifferent);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, drift.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);
    }
}
