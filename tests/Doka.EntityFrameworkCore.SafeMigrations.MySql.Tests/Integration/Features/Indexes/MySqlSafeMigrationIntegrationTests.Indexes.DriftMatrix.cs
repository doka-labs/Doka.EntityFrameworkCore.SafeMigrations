namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task EquivalentIndexWithDifferentName_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `index_identity` (`code` int NOT NULL, "
            + "INDEX `ix_index_identity_legacy` (`code`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_index_identity_expected",
            "index_identity",
            ["code"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("index-identity"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'index_identity' "
                + "AND INDEX_NAME IN ('ix_index_identity_legacy', 'ix_index_identity_expected');"));
    }

    [Fact]
    public async Task DifferentlyNamedIndexWithDifferentShape_RemainsApplicable()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `index_nonidentity` (`code` int NOT NULL, `alternate_code` int NOT NULL, "
            + "INDEX `ix_index_nonidentity_legacy` (`code`));");

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
    public async Task InvisibleOrIgnoredIndexWithExpectedName_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var visibility = Fixture.IsMariaDb ? "IGNORED" : "INVISIBLE";

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `index_visibility_drift` (`code` int NOT NULL);"
            + $"CREATE INDEX `ix_index_visibility_drift` ON `index_visibility_drift` (`code`) {visibility};");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_index_visibility_drift",
            "index_visibility_drift",
            ["code"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("index-visibility-drift"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DifferentlyNamedInvisibleOrIgnoredIndex_DoesNotBlockVisibleIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var visibility = Fixture.IsMariaDb ? "IGNORED" : "INVISIBLE";

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `index_visibility_identity` (`code` int NOT NULL);"
            + $"CREATE INDEX `ix_index_visibility_legacy` ON `index_visibility_identity` (`code`) {visibility};");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_index_visibility_expected",
            "index_visibility_identity",
            ["code"]);

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("index-visibility-identity"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("index-visibility-post"));

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
    public async Task OverlongUnprefixedIndex_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `overlong_index` ("
            + "`id` int NOT NULL, `property` varchar(800) CHARACTER SET utf8mb4 NOT NULL, "
            + "PRIMARY KEY (`id`)); INSERT INTO `overlong_index` VALUES (1, 'preserved');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_overlong_property", "overlong_index", ["property"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("overlong-index"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("index_prefix_required_for_key_limit", assessment.Code);
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'overlong_index' "
                + "AND INDEX_NAME = 'ix_overlong_property';"));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `overlong_index`;"));
    }

    [Fact]
    public async Task OneCharacterOverFullKeyLimit_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `one_over_index` ("
            + "`value` varchar(769) CHARACTER SET utf8mb4 NOT NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_one_over_value", "one_over_index", ["value"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("one-over-index"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("index_prefix_required_for_key_limit", assessment.Code);
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'one_over_index' "
                + "AND INDEX_NAME = 'ix_one_over_value';"));
    }

    [Fact]
    public async Task CompositePrefixesAndSingleByteFullKey_ConvergeAtTheExactLimit()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `encoded_width_indexes` ("
            + "`latin_value` varchar(800) CHARACTER SET latin1 NOT NULL, "
            + "`left_value` varchar(500) CHARACTER SET utf8mb4 NOT NULL, "
            + "`right_value` varchar(500) CHARACTER SET utf8mb4 NOT NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_encoded_latin_full",
            "encoded_width_indexes",
            ["latin_value"]);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_encoded_composite_prefix",
                "encoded_width_indexes",
                [
                    new ExpectedIndexKeyDefinition(column: "left_value", prefixLength: 384),
                    new ExpectedIndexKeyDefinition(column: "right_value", prefixLength: 384),
                ]),
            SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("encoded-width-indexes"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            3,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'encoded_width_indexes' "
                + "AND INDEX_NAME IN ('ix_encoded_latin_full', 'ix_encoded_composite_prefix');"));
    }

    [Fact]
    public async Task CompositeOverLimitAndScalarPrefix_AreRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `invalid_index_shapes` ("
            + "`left_value` varchar(500) CHARACTER SET utf8mb4 NOT NULL, "
            + "`right_value` varchar(500) CHARACTER SET utf8mb4 NOT NULL, "
            + "`numeric_value` int NOT NULL);");

        await using var context = CreateContext(connectionString);
        var definitions = new[]
        {
            new ExpectedIndexDefinition(
                "ix_invalid_composite_width",
                "invalid_index_shapes",
                [
                    new ExpectedIndexKeyDefinition(column: "left_value", prefixLength: 384),
                    new ExpectedIndexKeyDefinition(column: "right_value", prefixLength: 385),
                ]),
            new ExpectedIndexDefinition(
                "ix_invalid_scalar_prefix",
                "invalid_index_shapes",
                [new ExpectedIndexKeyDefinition(column: "numeric_value", prefixLength: 1)]),
        };

        foreach (var definition in definitions)
        {
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);

            var report = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("invalid-index-shape"));

            var exception = await Assert.ThrowsAsync<MySqlException>(() =>
                ExecuteOperationsAsync(context, builder.Operations));
            var assessment = Assert.Single(report.Assessments);

            Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
            Assert.Equal("index_prefix_required_for_key_limit", assessment.Code);
            Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'invalid_index_shapes';"));
    }

    [Fact]
    public async Task ExactLimitAndExplicitPrefixIndexes_AreAppliedAndIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `bounded_indexes` ("
            + "`id` int NOT NULL, "
            + "`exact_value` varchar(768) CHARACTER SET utf8mb4 NOT NULL, "
            + "`wide_value` varchar(800) CHARACTER SET utf8mb4 NOT NULL, "
            + "PRIMARY KEY (`id`));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_bounded_exact", "bounded_indexes", ["exact_value"]);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_bounded_prefix",
                "bounded_indexes",
                [new ExpectedIndexKeyDefinition(column: "wide_value", prefixLength: 768)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("bounded-indexes"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            768,
            await ScalarIntAsync(
                connectionString,
                "SELECT SUB_PART FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'bounded_indexes' "
                + "AND INDEX_NAME = 'ix_bounded_prefix';"));
    }

    [Fact]
    public async Task CompactRowFormat_UsesItsSmallerLimitAndAcceptsExplicitPrefix()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `compact_index_limit` ("
            + "`value` varchar(192) CHARACTER SET utf8mb4 NOT NULL) ROW_FORMAT=COMPACT;");

        await using var context = CreateContext(connectionString);
        var fullKey = new MigrationBuilder(context.Database.ProviderName!);
        fullKey.CreateIndexIfNotExists("ix_compact_full", "compact_index_limit", ["value"]);

        var blocked = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, fullKey.Operations, new SafeMigrationRunOptions("compact-full-key"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, fullKey.Operations));

        Assert.Equal(
            "index_prefix_required_for_key_limit",
            Assert.Single(blocked.Assessments)
                .Code);
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);

        var prefix = new MigrationBuilder(context.Database.ProviderName!);
        prefix.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_compact_prefix",
                "compact_index_limit",
                [new ExpectedIndexKeyDefinition(column: "value", prefixLength: 191)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, prefix.Operations);
        await ExecuteOperationsAsync(context, prefix.Operations);

        var ready = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, prefix.Operations, new SafeMigrationRunOptions("compact-prefix"));

        Assert.Equal(SafeMigrationReportStatus.Ready, ready.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(ready.Assessments).Action);
        Assert.Equal(
            191,
            await ScalarIntAsync(
                connectionString,
                "SELECT SUB_PART FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'compact_index_limit' "
                + "AND INDEX_NAME = 'ix_compact_prefix';"));
    }

    [Fact]
    public async Task PrefixBeyondColumnLength_IsRejectedBeforeDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `invalid_prefix` (`value` varchar(80) CHARACTER SET utf8mb4 NOT NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_invalid_prefix",
                "invalid_prefix",
                [new ExpectedIndexKeyDefinition(column: "value", prefixLength: 81)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("invalid-prefix"));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("index_prefix_required_for_key_limit", assessment.Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'invalid_prefix' "
                + "AND INDEX_NAME = 'ix_invalid_prefix';"));
    }

    [Fact]
    public async Task ObservableIndexFacetDrift_IsRejectedOneFieldAtATime()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `index_facets` ("
            + "`id` int NOT NULL, `alternate_id` varchar(80) NOT NULL, `value` varchar(80) NULL);");
        await using var context = CreateContext(connectionString);
        var canonical = new ExpectedIndexDefinition(
            "ix_index_facets",
            "index_facets",
            [
                new ExpectedIndexKeyDefinition(
                    column: "value",
                    sortOrder: SafeMigrationIndexSortOrder.Descending,
                    prefixLength: 12),
                new ExpectedIndexKeyDefinition(column: "id"),
            ],
            method: "BTREE");
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.EnsureIndex(canonical, SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteOperationsAsync(context, create.Operations);

        var variants = new[]
        {
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                canonical.Keys,
                unique: true,
                method: "BTREE"),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [
                    new ExpectedIndexKeyDefinition(column: "value", prefixLength: 12),
                    new ExpectedIndexKeyDefinition(column: "id"),
                ],
                method: "HASH"),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        sortOrder: SafeMigrationIndexSortOrder.Descending,
                        prefixLength: 12)
                ],
                method: "BTREE"),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [
                    new ExpectedIndexKeyDefinition(column: "id"),
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        sortOrder: SafeMigrationIndexSortOrder.Descending,
                        prefixLength: 12),
                ],
                method: "BTREE"),
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [
                    new ExpectedIndexKeyDefinition(
                        column: "alternate_id",
                        sortOrder: SafeMigrationIndexSortOrder.Descending,
                        prefixLength: 12),
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
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        sortOrder: SafeMigrationIndexSortOrder.Descending,
                        prefixLength: 13),
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
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
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
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"))
                ]),
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
    public async Task ExistingFunctionalIndex_IsMatchedAndExpressionDriftIsRejected()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `functional_drift` (`value` varchar(80) NULL);");
        await using var context = CreateContext(connectionString);
        var canonical = new ExpectedIndexDefinition(
            "ix_functional_drift",
            "functional_drift",
            [new ExpectedIndexKeyDefinition(structuredExpression: SqlFunction("lower", "value"))]);
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.EnsureIndex(canonical, SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, create.Operations, new SafeMigrationRunOptions("functional-drift"));

        if (Fixture.IsMariaDb)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
            return;
        }

        await ExecuteSqlAsync(
            connectionString,
            "CREATE INDEX `ix_functional_drift` ON `functional_drift` ((lower(`value`)));");

        var matching = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, create.Operations, new SafeMigrationRunOptions("functional-drift"));

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.EnsureIndex(
            new ExpectedIndexDefinition(
                canonical.Name,
                canonical.Table,
                [new ExpectedIndexKeyDefinition(structuredExpression: SqlFunction("upper", "value"))]),
            SafeMigrationPolicy.ThrowIfDifferent);

        var different = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, drift.Operations, new SafeMigrationRunOptions("functional-drift"));

        Assert.Equal(SafeMigrationReportStatus.Ready, matching.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(matching.Assessments).ObservedState);
        Assert.Equal(SafeMigrationReportStatus.Blocked, different.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(different.Assessments).ObservedState);
    }
}
