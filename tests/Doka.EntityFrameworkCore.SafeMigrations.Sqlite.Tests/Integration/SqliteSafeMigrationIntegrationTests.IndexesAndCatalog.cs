namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests : SqliteIntegrationTestBase
{
    [Fact]
    public async Task Catalog_RecoversNamedConstraintsGeneratedColumnsAndIndexFacets()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE catalog_shapes (Id INTEGER NOT NULL, Code TEXT NOT NULL, "
            + "Generated INTEGER GENERATED ALWAYS AS (Id + 1) VIRTUAL, "
            + "CONSTRAINT pk_catalog_shapes PRIMARY KEY (Id), "
            + "CONSTRAINT uq_catalog_shapes_code UNIQUE (Code), "
            + "CONSTRAINT ck_catalog_shapes_code CHECK (length(Code) > 0)); "
            + "CREATE INDEX ix_catalog_shapes_search ON catalog_shapes "
            + "(Code COLLATE NOCASE ASC, Id DESC) WHERE Code IS NOT NULL;");
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        var table = snapshot.Tables["catalog_shapes"];
        var unique = Assert.Single(table.UniqueConstraints);
        var check = Assert.Single(table.Checks);
        var index = Assert.Single(table.Indexes, value => value.Origin == "c");
        Assert.Equal("uq_catalog_shapes_code", unique.Name);
        Assert.Equal("ck_catalog_shapes_code", check.Name);
        Assert.Equal("Id + 1", table.Columns["Generated"].GeneratedSql);
        Assert.False(table.Columns["Generated"].IsStored);
        Assert.Equal("Code IS NOT NULL", index.Filter);
        Assert.Equal("NOCASE", index.Keys[0].Collation);
        Assert.True(index.Keys[1].Descending);
    }

    [Fact]
    public async Task Catalog_IgnoresStructuralKeywordsInsideLiteralsAndComments()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE catalog_parser_shapes (Id INTEGER NOT NULL, "
            + "Value TEXT DEFAULT 'CONSTRAINT phantom PRIMARY KEY AS (phantom)', "
            + "Generated INTEGER GENERATED ALWAYS AS /* expression */ (Id + 1) VIRTUAL, "
            + "CONSTRAINT pk_catalog_parser_shapes PRIMARY KEY (Id)); "
            + "CREATE INDEX ix_catalog_parser_expression ON catalog_parser_shapes "
            + "(replace(Value, 'where', 'replacement'));");
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        var table = snapshot.Tables["catalog_parser_shapes"];
        var index = Assert.Single(table.Indexes, value => value.Origin == "c");
        Assert.Equal("pk_catalog_parser_shapes", table.PrimaryKeyName);
        Assert.Null(table.Columns["Value"].GeneratedSql);
        Assert.Equal("Id + 1", table.Columns["Generated"].GeneratedSql);
        Assert.Null(index.Filter);
    }

    [Fact]
    public async Task Catalog_UsesTheLatestColumnConstraintNameForFollowingChecks()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE catalog_column_checks (Id INTEGER NOT NULL PRIMARY KEY, "
            + "Status TEXT CONSTRAINT nn_status NOT NULL "
            + "CONSTRAINT ck_status CHECK (Status <> '') CHECK (length(Status) <= 50));");
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        var checks = snapshot.Tables["catalog_column_checks"].Checks;
        Assert.Collection(
            checks,
            value => Assert.Equal("ck_status", value.Name),
            value => Assert.Equal("ck_status", value.Name));
    }

    [Fact]
    public async Task UnexpectedObjectInventory_RemovesSemanticAliasesAndRetainsOnlyPhysicalDrift()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE inventory_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT physical_parent_pk PRIMARY KEY (Id)); "
            + "CREATE TABLE inventory_items (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "Code TEXT NOT NULL, Legacy TEXT NULL, "
            + "CONSTRAINT physical_item_pk PRIMARY KEY (Id), "
            + "CONSTRAINT physical_item_uq UNIQUE (Code), "
            + "CONSTRAINT physical_item_ck CHECK (length(Code) > 0), "
            + "CONSTRAINT physical_item_fk FOREIGN KEY (ParentId) REFERENCES inventory_parents (Id)); "
            + "CREATE INDEX physical_item_parent_idx ON inventory_items (ParentId); "
            + "CREATE INDEX legacy_item_idx ON inventory_items (Legacy);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "inventory_parents",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
            },
            constraints: table => table.PrimaryKey("expected_parent_pk", value => value.Id),
            mode: SafeMigrationTableMode.ConvergenceContainer);
        builder.CreateTableIfNotExists(
            "inventory_items",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                ParentId = table.Column<int>(type: "INTEGER", nullable: true),
                Code = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("expected_item_pk", value => value.Id),
            mode: SafeMigrationTableMode.ConvergenceContainer);
        builder.AddUniqueConstraintIfNotExists("expected_item_uq", "inventory_items", ["Code"]);
        builder.AddCheckConstraintIfNotExists("expected_item_ck", "inventory_items", "length(Code) > 0");
        builder.AddForeignKeyIfNotExists(
            "expected_item_fk",
            "inventory_items",
            ["ParentId"],
            "inventory_parents",
            ["Id"]);
        builder.CreateIndexIfNotExists(
            "expected_item_parent_idx",
            "inventory_items",
            ["ParentId"]);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        var inventory = await analyzer.FindUnexpectedObjectsAsync(
            context,
            builder.Operations,
            CancellationToken.None);

        Assert.Collection(
            inventory.OrderBy(value => value.ObjectKind).ThenBy(value => value.Name, StringComparer.Ordinal),
            value =>
            {
                Assert.Equal(SafeMigrationDatabaseObjectKind.Column, value.ObjectKind);
                Assert.Equal("Legacy", value.Name);
            },
            value =>
            {
                Assert.Equal(SafeMigrationDatabaseObjectKind.Index, value.ObjectKind);
                Assert.Equal("legacy_item_idx", value.Name);
            });
    }

    [Fact]
    public async Task CompositePartialIndex_PreservesOrderCollationFilterAndDropReplay()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE indexed_items (id INTEGER NOT NULL, code TEXT NULL, sequence INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_indexed_items_search",
                "indexed_items",
                [
                    new ExpectedIndexKeyDefinition(
                        column: "code",
                        sortOrder: SafeMigrationIndexSortOrder.Ascending,
                        collation: new SafeMigrationCollationIdentifier("NOCASE")),
                    new ExpectedIndexKeyDefinition(
                        column: "sequence",
                        sortOrder: SafeMigrationIndexSortOrder.Descending),
                ],
                structuredFilter: SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("code"))),
            SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteOperationsAsync(context, create.Operations);

        var matching = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                create.Operations,
                new SafeMigrationRunOptions("sqlite-index-definition"));

        var drop = new MigrationBuilder(context.Database.ProviderName!);
        drop.DropIndexIfExists("ix_indexed_items_search", "indexed_items");
        await ExecuteOperationsAsync(context, drop.Operations);
        await ExecuteOperationsAsync(context, drop.Operations);

        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(matching.Assessments).Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'index' AND name = 'ix_indexed_items_search';"));
    }

    [Fact]
    public async Task UniqueIndexWithDuplicateRows_IsDataBlockedBeforeDdl()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE duplicate_index_values (id INTEGER NOT NULL, code TEXT NULL); "
            + "INSERT INTO duplicate_index_values (id, code) VALUES (1, 'duplicate'), (2, 'duplicate');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ux_duplicate_index_values_code",
            "duplicate_index_values",
            ["code"],
            unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-unique-index-duplicate"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal("unique_index_duplicate_values", assessment.AnalysisCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'index' AND name = 'ux_duplicate_index_values_code';"));
    }

    [Fact]
    public async Task UniqueIndexDuplicateProbe_UsesRequestedKeyCollation()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE duplicate_collation_values (id INTEGER NOT NULL, code TEXT NULL); "
            + "INSERT INTO duplicate_collation_values (id, code) VALUES (1, 'Active'), (2, 'ACTIVE');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ux_duplicate_collation_values_code",
                "duplicate_collation_values",
                [
                    new ExpectedIndexKeyDefinition(
                        column: "code",
                        collation: new SafeMigrationCollationIdentifier("NOCASE")),
                ],
                unique: true),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-unique-index-collation"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal("unique_index_duplicate_values", assessment.AnalysisCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'index' AND name = 'ux_duplicate_collation_values_code';"));
    }

    [Fact]
    public async Task UniqueExpressionIndexWithSemanticDuplicates_IsDataBlockedBeforeDdl()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE duplicate_expression_values (id INTEGER NOT NULL, code TEXT NULL); "
            + "INSERT INTO duplicate_expression_values (id, code) VALUES (1, 'Active'), (2, 'ACTIVE');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ux_duplicate_expression_values_code",
                "duplicate_expression_values",
                [
                    new ExpectedIndexKeyDefinition(
                        structuredExpression: SafeMigrationSql.Function(
                            "lower",
                            SafeMigrationSql.Identifier("code"))),
                ],
                unique: true),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-unique-expression-index-duplicate"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal("unique_index_duplicate_values", assessment.AnalysisCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'index' AND name = 'ux_duplicate_expression_values_code';"));
    }

    [Fact]
    public async Task UniqueExpressionIndexWithDistinctValues_AppliesAndReplays()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE distinct_expression_values (id INTEGER NOT NULL, code TEXT NULL); "
            + "INSERT INTO distinct_expression_values (id, code) VALUES (1, 'Active'), (2, 'Inactive');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ux_distinct_expression_values_code",
                "distinct_expression_values",
                [
                    new ExpectedIndexKeyDefinition(
                        structuredExpression: SafeMigrationSql.Function(
                            "lower",
                            SafeMigrationSql.Identifier("code"))),
                ],
                unique: true),
            SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'index' AND name = 'ux_distinct_expression_values_code';"));
    }

    [Fact]
    public async Task UnsupportedIndexProviderOptions_AreRejectedBeforeDdl()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE unsupported_indexes (id INTEGER NOT NULL, code TEXT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_unsupported_indexes_code",
                "unsupported_indexes",
                [new ExpectedIndexKeyDefinition(column: "code", prefixLength: 12)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-index-provider-option"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("index_key_provider_option", assessment.AnalysisCode);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
    }

    [Fact]
    public async Task ExpressionIndexLifecycle_AppliesMatchesAndRenamesWithoutDefinitionLoss()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE expression_items (id INTEGER NOT NULL, value TEXT NULL);");
        await using var context = CreateContext(connection);
        var create = new MigrationBuilder(context.Database.ProviderName!);
        create.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_expression_items_normalized",
                "expression_items",
                [
                    new ExpectedIndexKeyDefinition(
                        structuredExpression: SafeMigrationSql.Function(
                            "lower",
                            SafeMigrationSql.Identifier("value"))),
                ],
                structuredFilter: SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("value"))),
            SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, create.Operations);
        await ExecuteOperationsAsync(context, create.Operations);

        var rename = new MigrationBuilder(context.Database.ProviderName!);
        rename.RenameIndexIfExists(
            "ix_expression_items_normalized",
            "expression_items",
            "ix_expression_items_normalized_v2");

        await ExecuteOperationsAsync(context, rename.Operations);
        await ExecuteOperationsAsync(context, rename.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'index' "
                + "AND name = 'ix_expression_items_normalized_v2' "
                + "AND lower(sql) LIKE '%lower(%value%)%' AND lower(sql) LIKE '%where%value%is not null%';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'index' AND name = 'ix_expression_items_normalized';"));
    }

    [Fact]
    public async Task ConstraintNames_AreMatchedByDefinitionInsteadOfPragmaOrder()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE constraint_parents (id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE constraint_children ("
            + "id INTEGER NOT NULL, code_a TEXT NULL, code_b TEXT NULL, "
            + "parent_a INTEGER NULL, parent_b INTEGER NULL, "
            + "CONSTRAINT uq_constraint_code_a UNIQUE (code_a), "
            + "CONSTRAINT uq_constraint_code_b UNIQUE (code_b), "
            + "CONSTRAINT fk_constraint_parent_a FOREIGN KEY (parent_a) REFERENCES constraint_parents (id), "
            + "CONSTRAINT fk_constraint_parent_b FOREIGN KEY (parent_b) REFERENCES constraint_parents (id));");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddUniqueConstraintIfNotExists(
            "uq_constraint_code_a",
            "constraint_children",
            ["code_a"]);
        builder.AddUniqueConstraintIfNotExists(
            "uq_constraint_code_b",
            "constraint_children",
            ["code_b"]);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_parent_a",
            "constraint_children",
            ["parent_a"],
            "constraint_parents",
            ["id"]);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_parent_b",
            "constraint_children",
            ["parent_b"],
            "constraint_parents",
            ["id"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("sqlite-constraint-names"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        });
    }

    [Fact]
    public async Task RebuildWithUnmanagedArtifacts_IsRejectedFailClosed()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE guarded_items (id INTEGER NOT NULL, value TEXT NULL); "
            + "CREATE TRIGGER tr_guarded_items AFTER INSERT ON guarded_items BEGIN SELECT 1; END;");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(
            "guarded_items",
            new ExpectedColumnDefinition("value", typeof(string), isNullable: false, storeType: "TEXT"),
            new ExpectedColumnDefinition("value", typeof(string), isNullable: true, storeType: "TEXT"),
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("sqlite-rebuild-artifacts"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_trigger", assessment.AnalysisCode);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
    }

    [Fact]
    public async Task ConstraintRebuildBatch_PreflightsEveryChangeAndPreservesRows()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE rebuild_entities (Id INTEGER NOT NULL, Code TEXT NOT NULL, "
            + "CONSTRAINT pk_rebuild_entities PRIMARY KEY (Id)); "
            + "INSERT INTO rebuild_entities (Id, Code) VALUES (1, 'existing');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddUniqueConstraintIfNotExists(
            "uq_rebuild_entities_code",
            "rebuild_entities",
            ["Code"]);
        builder.AddCheckConstraintIfNotExists(
            "ck_rebuild_entities_code",
            "rebuild_entities",
            "length(\"Code\") > 0");
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-rebuild-batch"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-rebuild-batch-replay"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM rebuild_entities WHERE Id = 1 AND Code = 'existing';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_index_list('rebuild_entities') WHERE origin = 'u';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'rebuild_entities' "
                + "AND lower(sql) LIKE '%check%length(%code%) > 0%';"));
    }

    [Fact]
    public async Task RebuildWithUnmanagedColumn_IsRejectedBeforeAnyConstraintMutation()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE rebuild_entities (Id INTEGER NOT NULL, Code TEXT NOT NULL, Legacy TEXT NULL, "
            + "CONSTRAINT pk_rebuild_entities PRIMARY KEY (Id));");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_rebuild_entities_code",
            "rebuild_entities",
            "length(\"Code\") > 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-rebuild-unmanaged-column"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_unmanaged_column", assessment.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('rebuild_entities') WHERE name = 'Legacy';"));
    }

    [Fact]
    public async Task RebuildBatch_WithLaterDataBlockDoesNotApplyEarlierConstraint()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE rebuild_entities (Id INTEGER NOT NULL, Code TEXT NOT NULL, "
            + "CONSTRAINT pk_rebuild_entities PRIMARY KEY (Id)); "
            + "INSERT INTO rebuild_entities (Id, Code) VALUES (1, '');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddUniqueConstraintIfNotExists(
            "uq_rebuild_entities_code",
            "rebuild_entities",
            ["Code"]);
        builder.AddCheckConstraintIfNotExists(
            "ck_rebuild_entities_code",
            "rebuild_entities",
            "length(\"Code\") > 0");
        var runner = context.GetService<ISafeMigrationRunner>();

        var report = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-rebuild-batch-blocked"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, report.Assessments[1].ObservedState);
        Assert.Contains("check_constraint_violated", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_index_list('rebuild_entities') WHERE origin = 'u';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM rebuild_entities WHERE Id = 1 AND Code = '';"));
    }

    [Fact]
    public async Task StrictTableRebuild_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE strict_items (id INTEGER NOT NULL PRIMARY KEY, value TEXT NULL) STRICT;");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("value", "strict_items");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("sqlite-table-options"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("strict_table_rebuild", assessment.AnalysisCode);
    }

    [Fact]
    public async Task WithoutRowIdTableRebuild_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE without_rowid_items (id INTEGER NOT NULL PRIMARY KEY, value TEXT NULL) WITHOUT ROWID;");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("value", "without_rowid_items");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("sqlite-without-rowid"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("without_rowid_table_rebuild", assessment.AnalysisCode);
    }

    [Fact]
    public async Task RebuildWithDependentView_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE rebuild_entities (Id INTEGER NOT NULL, Code TEXT NOT NULL, "
            + "CONSTRAINT pk_rebuild_entities PRIMARY KEY (Id), "
            + "CONSTRAINT uq_rebuild_entities_code UNIQUE (Code)); "
            + "CREATE VIEW rebuild_entities_view AS SELECT Id, Code FROM rebuild_entities;");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_rebuild_entities_code",
            "rebuild_entities",
            "length(\"Code\") > 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("sqlite-view-rebuild"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_view", assessment.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'view' AND name = 'rebuild_entities_view';"));
    }

    [Fact]
    public async Task RebuildWithoutTargetModelTable_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE unmodeled_items (id INTEGER NOT NULL, value TEXT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("value", "unmodeled_items");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("sqlite-model-missing"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_model_missing", assessment.AnalysisCode);
    }
}
