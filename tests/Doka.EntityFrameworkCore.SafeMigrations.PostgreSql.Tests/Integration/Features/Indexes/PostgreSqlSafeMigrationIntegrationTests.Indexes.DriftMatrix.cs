namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task EquivalentIndexWithDifferentName_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE index_identity (code integer NOT NULL);"
            + "CREATE INDEX ix_index_identity_legacy ON index_identity (code);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_index_identity_expected",
            "index_identity",
            ["code"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("index-identity"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal("doka_sm_different", exception.MessageText);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class "
                + "WHERE relname IN ('ix_index_identity_legacy', 'ix_index_identity_expected');"));
    }

    [Fact]
    public async Task DifferentlyNamedIndexWithDifferentShape_RemainsApplicable()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE index_nonidentity (code integer NOT NULL, alternate_code integer NOT NULL);"
            + "CREATE INDEX ix_index_nonidentity_legacy ON index_nonidentity (code);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_index_nonidentity_expected",
            "index_nonidentity",
            ["alternate_code"]);

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("index-nonidentity"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("index-nonidentity-post"));
        var assessment = Assert.Single(preflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(postflight.Assessments).Action);
    }

    [Fact]
    public async Task InvalidIndexWithExpectedName_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE invalid_index_drift (id integer NOT NULL, code integer NOT NULL);"
            + "INSERT INTO invalid_index_drift VALUES (1, 7), (2, 7);");

        _ = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteSqlAsync(
                connectionString,
                "CREATE UNIQUE INDEX CONCURRENTLY ix_invalid_index_drift ON invalid_index_drift (code);"));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_index i "
                + "JOIN pg_catalog.pg_class idx ON idx.oid = i.indexrelid "
                + "WHERE idx.relname = 'ix_invalid_index_drift' AND NOT i.indisvalid;"));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_invalid_index_drift",
            "invalid_index_drift",
            ["code"],
            unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("invalid-index-drift"));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal("doka_sm_different", exception.MessageText);
    }

    [Fact]
    public async Task DifferentlyNamedInvalidIndex_DoesNotBlockValidIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE invalid_index_identity (id integer NOT NULL, code integer NOT NULL);"
            + "INSERT INTO invalid_index_identity VALUES (1, 7), (2, 7);");

        _ = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteSqlAsync(
                connectionString,
                "CREATE UNIQUE INDEX CONCURRENTLY ix_invalid_index_legacy ON invalid_index_identity (code);"));

        await ExecuteSqlAsync(connectionString, "DELETE FROM invalid_index_identity WHERE id = 2;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_invalid_index_expected",
            "invalid_index_identity",
            ["code"],
            unique: true);

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("invalid-index-identity"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("invalid-index-post"));

        var preflightAssessment = Assert.Single(preflight.Assessments);
        var postflightAssessment = Assert.Single(postflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflightAssessment.Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, postflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, postflightAssessment.Action);
    }

    [Fact]
    public async Task PartitionedParentIndex_IsMatching_WhileAttachedChildOperationsAreRejected()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE partitioned_index_root (bucket integer NOT NULL, code integer NOT NULL) "
            + "PARTITION BY RANGE (bucket);"
            + "CREATE TABLE partitioned_index_leaf PARTITION OF partitioned_index_root "
            + "FOR VALUES FROM (0) TO (100);"
            + "CREATE INDEX ix_partitioned_index_root_code ON partitioned_index_root (code);");

        var childIndex = await ScalarStringAsync(
            connectionString,
            "SELECT child.relname FROM pg_catalog.pg_inherits inh "
            + "JOIN pg_catalog.pg_class child ON child.oid = inh.inhrelid "
            + "JOIN pg_catalog.pg_class parent ON parent.oid = inh.inhparent "
            + "WHERE parent.relname = 'ix_partitioned_index_root_code';");

        await using var context = CreateContext(connectionString);
        var parent = new MigrationBuilder(context.Database.ProviderName!);
        parent.CreateIndexIfNotExists(
            "ix_partitioned_index_root_code",
            "partitioned_index_root",
            ["code"]);

        var parentReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, parent.Operations, new SafeMigrationRunOptions("partitioned-index-parent"));

        Assert.Equal(SafeMigrationReportStatus.Ready, parentReport.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(parentReport.Assessments).ObservedState);

        var child = new MigrationBuilder(context.Database.ProviderName!);
        child.CreateIndexIfNotExists(childIndex, "partitioned_index_leaf", ["code"]);
        child.DropIndexIfExists(childIndex, "partitioned_index_leaf");
        child.RenameIndexIfExists(childIndex, "partitioned_index_leaf", "ix_partitioned_index_leaf_renamed");

        var childReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, child.Operations, new SafeMigrationRunOptions("partitioned-index-child"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, childReport.Status);
        Assert.All(
            childReport.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        foreach (var operation in child.Operations)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteOperationsAsync(context, [operation]));

            Assert.Equal("P1001", exception.SqlState);
            Assert.Equal("doka_sm_different", exception.MessageText);
        }
    }

    [Fact]
    public async Task ConstraintOwnedIndexOperations_AreRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE constraint_owned_index (code integer NULL, "
            + "CONSTRAINT ix_constraint_owned_code UNIQUE (code));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_constraint_owned_code",
            "constraint_owned_index",
            ["code"],
            unique: true);
        builder.CreateIndexIfNotExists(
            "ix_constraint_owned_expected",
            "constraint_owned_index",
            ["code"],
            unique: true);
        builder.DropIndexIfExists("ix_constraint_owned_code", "constraint_owned_index");
        builder.RenameIndexIfExists(
            "ix_constraint_owned_code",
            "constraint_owned_index",
            "ix_constraint_owned_renamed");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("constraint-owned-index"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        foreach (var operation in builder.Operations)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteOperationsAsync(context, [operation]));

            Assert.Equal("P1001", exception.SqlState);
            Assert.Equal("doka_sm_different", exception.MessageText);
        }
    }

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
