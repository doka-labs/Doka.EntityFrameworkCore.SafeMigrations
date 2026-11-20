namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises review regressions with independently owned SQLite state.</summary>
public sealed class SqliteSafeMigrationReviewRegressionsIntegrationTests : SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>
    /// Raw SQL removing dependent table defers stale incoming foreign key evidence.
    /// </summary>
    [Fact]
    public async Task RawSqlRemovingDependentTable_DefersStaleIncomingForeignKeyEvidence()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE raw_drop_parent (Id INTEGER NOT NULL PRIMARY KEY); CREATE TABLE
            raw_drop_child (Id INTEGER NOT NULL PRIMARY KEY,
            ParentId INTEGER NULL, FOREIGN KEY (ParentId) REFERENCES raw_drop_parent
            (Id));
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("DROP TABLE raw_drop_child;");
        builder.DropTableIfExists("raw_drop_parent");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-raw-dependent-drop"),
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var remainingTables = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' "
            + "AND name IN ('raw_drop_parent', 'raw_drop_child');",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.Equal(0, remainingTables);
    }

    /// <summary>
    /// Raw SQL after safe rename defers stale incoming foreign key evidence.
    /// </summary>
    [Fact]
    public async Task RawSqlAfterSafeRename_DefersStaleIncomingForeignKeyEvidence()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE renamed_drop_parent (Id INTEGER NOT NULL PRIMARY KEY); CREATE
            TABLE renamed_drop_child (Id INTEGER NOT NULL PRIMARY KEY,
            ParentId INTEGER NULL, FOREIGN KEY (ParentId) REFERENCES renamed_drop_parent
            (Id));
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("renamed_drop_parent", "renamed_drop_target");
        _ = builder.Sql("DROP TABLE renamed_drop_child;");
        builder.DropTableIfExists("renamed_drop_target");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-rename-raw-dependent-drop"),
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var remainingTables = await ScalarIntAsync(
            connection,
            """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'table' AND name IN ('renamed_drop_parent', 'renamed_drop_target',
            'renamed_drop_child');
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[2].Action);
        Assert.Equal(1, preflight.Assessments[2].DeferredOrigin?.OperationOrdinal);
        Assert.Equal(0, remainingTables);
    }

    /// <summary>
    /// Raw SQL does not defer unsupported schema drop.
    /// </summary>
    [Fact]
    public async Task RawSql_DoesNotDeferUnsupportedSchemaDrop()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.DropSchemaIfExists("main");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-raw-unsupported-schema-drop"),
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[1].Action);
        Assert.Equal("schema_operations", preflight.Assessments[1].AnalysisCode);
        Assert.Null(preflight.Assessments[1].DeferredOrigin);
    }

    /// <summary>
    /// Raw SQL does not defer unsupported index contract.
    /// </summary>
    [Fact]
    public async Task RawSql_DoesNotDeferUnsupportedIndexContract()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.CreateIndexIfNotExists(
            "ix_unapproved",
            "missing_table",
            ["code"],
            nullOrders: [SafeMigrationIndexNullOrder.First]);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-raw-unsupported-index-option"),
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[1].Action);
        Assert.Equal("index_key_provider_option", preflight.Assessments[1].AnalysisCode);
        Assert.Null(preflight.Assessments[1].DeferredOrigin);
    }

    /// <summary>
    /// Raw SQL does not let virtual table mask unsupported index contract.
    /// </summary>
    [Fact]
    public async Task RawSql_DoesNotLetVirtualTableMaskUnsupportedIndexContract()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE VIRTUAL TABLE search_documents USING fts5(content);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.CreateIndexIfNotExists(
            "ix_unapproved",
            "search_documents",
            ["content"],
            nullOrders: [SafeMigrationIndexNullOrder.First]);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-raw-virtual-unsupported-index"),
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[1].Action);
        Assert.Equal("index_key_provider_option", preflight.Assessments[1].AnalysisCode);
        Assert.Null(preflight.Assessments[1].DeferredOrigin);
    }

    /// <summary>
    /// Raw SQL defers virtual table boundary when index contract is supported.
    /// </summary>
    [Fact]
    public async Task RawSql_DefersVirtualTableBoundaryWhenIndexContractIsSupported()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE VIRTUAL TABLE search_documents USING fts5(content);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.CreateIndexIfNotExists("ix_search_documents_content", "search_documents", ["content"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-raw-virtual-supported-index"),
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.Equal("projected_structure_state_unknown", preflight.Assessments[1].AnalysisCode);
    }

    /// <summary>
    /// Raw SQL does not defer unsupported check expression.
    /// </summary>
    [Fact]
    public async Task RawSql_DoesNotDeferUnsupportedCheckExpression()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE check_target (Id INTEGER NOT NULL PRIMARY KEY);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_check_target_id",
                "check_target",
                SafeMigrationSql.ProviderFragment("foreign_provider", "Id > 0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-raw-unsupported-check-expression"),
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, preflight.Assessments[1].Action);
        Assert.Equal("check_expression", preflight.Assessments[1].AnalysisCode);
        Assert.Null(preflight.Assessments[1].DeferredOrigin);
    }

    /// <summary>
    /// Raw SQL defers catalog dependent stored generated column rejection.
    /// </summary>
    [Fact]
    public async Task RawSql_DefersCatalogDependentStoredGeneratedColumnRejection()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE generated_values (a INTEGER NOT NULL, b INTEGER NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.Sql("SELECT 1;");
        builder.EnsureColumn(
            "generated_values",
            new ExpectedColumnDefinition(
                "sum_value",
                typeof(int),
                isNullable: false,
                storeType: "INTEGER",
                computedExpression: SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("a"),
                    SafeMigrationSqlBinaryOperator.Add,
                    SafeMigrationSql.Identifier("b")),
                isStored: true),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-raw-stored-generated-column"),
            cancellationToken: CancellationToken.None);

        // WHY: Raw SQL could rebuild the table with the target column before
        // the guarded ensure, so this live-only rejection is not invariant.

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.Equal("projected_structure_state_unknown", preflight.Assessments[1].AnalysisCode);
    }

    /// <summary>
    /// Drop table with incoming foreign key is rejected without dependent mutation.
    /// </summary>
    /// <param name="deleteAction">The delete action defining this isolated fixture case.</param>
    /// <param name="foreignKeysEnabled">The foreign keys enabled defining this isolated fixture case.</param>
    [Theory]
    [InlineData("CASCADE", true)]
    [InlineData("SET NULL", true)]
    [InlineData("RESTRICT", true)]
    [InlineData("CASCADE", false)]
    public async Task DropTableWithIncomingForeignKey_IsRejectedWithoutDependentMutation(
        string deleteAction,
        bool foreignKeysEnabled
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        if (!foreignKeysEnabled)
        {
            await ExecuteSqlAsync(connection, "PRAGMA foreign_keys = OFF;", cancellationToken: CancellationToken.None);
        }

        await ExecuteSqlAsync(
            connection,
            $"""
            CREATE TABLE drop_parent (Id INTEGER NOT NULL PRIMARY KEY); CREATE TABLE
            drop_child (Id INTEGER NOT NULL PRIMARY KEY,
            ParentId INTEGER NULL, CONSTRAINT fk_drop_child_parent FOREIGN KEY (ParentId)
            REFERENCES drop_parent (Id) ON DELETE {deleteAction}); INSERT
            INTO drop_parent (Id) VALUES (1); INSERT INTO drop_child (Id,
            ParentId) VALUES (11, 1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("drop_parent");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-drop-incoming-foreign-key"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM drop_parent;",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM drop_child WHERE Id = 11 AND ParentId = 1;",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_drop_foreign_key_dependency", assessment.AnalysisCode);
        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);
    }

    /// <summary>
    /// Ordered dependent and principal drops converge without foreign key side effects.
    /// </summary>
    [Fact]
    public async Task OrderedDependentAndPrincipalDrops_ConvergeWithoutForeignKeySideEffects()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE ordered_drop_parent (Id INTEGER NOT NULL PRIMARY KEY); CREATE
            TABLE ordered_drop_child (Id INTEGER NOT NULL PRIMARY KEY,
            ParentId INTEGER NULL, CONSTRAINT fk_ordered_drop_child_parent FOREIGN KEY
            (ParentId) REFERENCES ordered_drop_parent (Id) ON DELETE CASCADE); INSERT
            INTO ordered_drop_parent (Id) VALUES (1); INSERT INTO ordered_drop_child
            (Id,
            ParentId) VALUES (11, 1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("ordered_drop_child");
        builder.DropTableIfExists("ordered_drop_parent");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-ordered-dependent-drop"),
                cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema "
            + "WHERE type = 'table' AND name IN ('ordered_drop_parent', 'ordered_drop_child');",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(
            0,
            observedScalar1);
    }

    /// <summary>
    /// Projected child table blocks later principal drop.
    /// </summary>
    [Fact]
    public async Task ProjectedChildTable_BlocksLaterPrincipalDrop()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE projected_parent (Id INTEGER NOT NULL PRIMARY KEY);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "projected_child",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                ParentId = table.Column<int>(type: "INTEGER", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_projected_child", value => value.Id);
                table.ForeignKey(
                    "fk_projected_child_parent",
                    value => value.ParentId,
                    "projected_parent",
                    "Id");
            });

        builder.DropTableIfExists("projected_parent");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-projected-child-drop"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema "
            + "WHERE type = 'table' AND name = 'projected_parent';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(
            report.Assessments,
            static value => value.OperationKind == SafeMigrationOperationKind.DropTable);

        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_drop_foreign_key_dependency", assessment.AnalysisCode);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Projected foreign key and rename block renamed principal drop.
    /// </summary>
    [Fact]
    public async Task ProjectedForeignKeyAndRename_BlockRenamedPrincipalDrop()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE collation_parents (Id INTEGER NOT NULL, Code TEXT COLLATE NOCASE NOT
            NULL,
            CONSTRAINT pk_collation_parents PRIMARY KEY (Id), CONSTRAINT uq_collation_parents_code
            UNIQUE (Code)); CREATE TABLE collation_children (Id INTEGER NOT NULL,
            ParentCode TEXT NOT NULL, CONSTRAINT pk_collation_children PRIMARY KEY (Id));
            CREATE INDEX ix_collation_children_parent_code ON collation_children (ParentCode);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteForeignKeyCollationTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "fk_collation_children_parent",
            "collation_children",
            ["ParentCode"],
            "collation_parents",
            ["Code"]);

        builder.RenameTableIfExists("collation_parents", "renamed_collation_parents");
        builder.DropTableIfExists("renamed_collation_parents");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-projected-foreign-key-rename"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema "
            + "WHERE type = 'table' AND name = 'collation_parents';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(
            report.Assessments,
            static value => value.OperationKind == SafeMigrationOperationKind.DropTable);

        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_drop_foreign_key_dependency", assessment.AnalysisCode);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Projected dependent drop allows later principal drop.
    /// </summary>
    [Fact]
    public async Task ProjectedDependentDrop_AllowsLaterPrincipalDrop()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE projected_drop_parent (Id INTEGER NOT NULL PRIMARY KEY);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "projected_drop_child",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                ParentId = table.Column<int>(type: "INTEGER", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_projected_drop_child", value => value.Id);
                table.ForeignKey(
                    "fk_projected_drop_child_parent",
                    value => value.ParentId,
                    "projected_drop_parent",
                    "Id");
            });

        builder.DropTableIfExists("projected_drop_child");
        builder.DropTableIfExists("projected_drop_parent");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-projected-dependent-drop"),
                cancellationToken: CancellationToken.None);

        // Assert
        var principalDrop = Assert.Single(
            report.Assessments,
            static value => value is
            {
                OperationKind: SafeMigrationOperationKind.DropTable,
                ObjectName: "projected_drop_parent",
            });

        Assert.Equal(SafeMigrationAction.Apply, principalDrop.Action);
    }

    /// <summary>
    /// Self referencing table drop does not report an external dependency.
    /// </summary>
    [Fact]
    public async Task SelfReferencingTableDrop_DoesNotReportAnExternalDependency()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE self_drop_tree (Id INTEGER NOT NULL PRIMARY KEY, ParentId INTEGER
            NULL,
            CONSTRAINT fk_self_drop_tree_parent FOREIGN KEY (ParentId) REFERENCES self_drop_tree
            (Id) ON DELETE CASCADE); INSERT INTO self_drop_tree (Id,
            ParentId) VALUES (1, NULL), (2, 1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("self_drop_tree");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-self-referencing-drop"),
                cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema "
            + "WHERE type = 'table' AND name = 'self_drop_tree';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
        Assert.Equal(
            0,
            observedScalar1);
    }

    /// <summary>
    /// Rename with legacy alter table enabled is rejected before reference drift.
    /// </summary>
    [Fact]
    public async Task RenameWithLegacyAlterTableEnabled_IsRejectedBeforeReferenceDrift()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE legacy_rename_source (Id INTEGER NOT NULL PRIMARY KEY); CREATE
            VIEW legacy_rename_view AS SELECT Id
            FROM legacy_rename_source; PRAGMA legacy_alter_table = ON;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("legacy_rename_source", "legacy_rename_target");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-legacy-alter-table"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema "
            + "WHERE type = 'table' AND name = 'legacy_rename_source';",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            """
            SELECT COUNT(*) FROM main.sqlite_schema
            WHERE type = 'view' AND name = 'legacy_rename_view'
            AND sql LIKE '%legacy_rename_source%';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("legacy_alter_table", assessment.AnalysisCode);
        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);
    }

    /// <summary>
    /// Cross table SQLite prefixed trigger blocks referenced table rebuild.
    /// </summary>
    [Fact]
    public async Task CrossTableSqlitePrefixedTrigger_BlocksReferencedTableRebuild()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE structural_blogs (Id INTEGER NOT NULL PRIMARY KEY); CREATE
            TABLE structural_posts (Id INTEGER NOT NULL,
            Title TEXT NOT NULL, BlogId INTEGER NULL, CONSTRAINT pk_structural_posts
            PRIMARY KEY (Id),
            CONSTRAINT fk_structural_posts_blog FOREIGN KEY (BlogId) REFERENCES structural_blogs
            (Id)); CREATE TABLE structural_post_source (Id INTEGER NOT NULL PRIMARY
            KEY); CREATE TRIGGER SqliteAudit_StructuralPosts AFTER INSERT ON structural_post_source
            BEGIN INSERT INTO structural_posts (Id,
            Title, BlogId) VALUES (NEW.Id, 'trigger', NULL); END;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteStructuralSequenceTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_structural_posts_blog", "structural_posts");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-cross-table-trigger"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'SqliteAudit_StructuralPosts';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_trigger", assessment.AnalysisCode);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Non ascii case distinct identifiers remain independent physical objects.
    /// </summary>
    [Fact]
    public async Task NonAsciiCaseDistinctIdentifiers_RemainIndependentPhysicalObjects()
    {
        // Arrange
        const string uppercase = "\u00C9";
        const string lowercase = "\u00E9";
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            $"""
            CREATE TABLE "{uppercase}" (Id INTEGER NOT NULL);
            CREATE TABLE "{lowercase}" (Id INTEGER NOT NULL);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists(uppercase);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-non-ascii-identifiers"),
                cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            $"SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'table' AND name = '{uppercase}';",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            $"SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'table' AND name = '{lowercase}';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply,
            Assert.Single(report.Assessments)
                .Action);

        Assert.Equal(
            0,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);
    }

    /// <summary>
    /// SQLite prefixed user table remains visible to lifecycle analysis.
    /// </summary>
    [Fact]
    public async Task SqlitePrefixedUserTable_RemainsVisibleToLifecycleAnalysis()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE SqliteCache (Id INTEGER NOT NULL PRIMARY KEY);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("SqliteCache");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-user-prefix"),
                cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'SqliteCache';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply,
            Assert.Single(report.Assessments)
                .Action);

        Assert.Equal(
            0,
            observedScalar1);
    }

    /// <summary>
    /// Trivia separated without row id option blocks table rebuild.
    /// </summary>
    /// <param name="tableOption">The table option defining this isolated fixture case.</param>
    [Theory]
    [InlineData("WITHOUT\nROWID")]
    [InlineData("WITHOUT /*gap*/ ROWID")]
    [InlineData("WITHOUT --gap\nROWID")]
    public async Task TriviaSeparatedWithoutRowIdOption_BlocksTableRebuild(
        string tableOption
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE newline_without_rowid (Id INTEGER NOT NULL PRIMARY KEY, Value TEXT NULL) "
            + $"{tableOption};",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_newline_without_rowid", "newline_without_rowid", "Id > 0");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-newline-without-rowid"),
                cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("without_rowid_table_rebuild", assessment.AnalysisCode);
    }

    /// <summary>
    /// High byte unquoted identifier view blocks referenced table rebuild.
    /// </summary>
    [Fact]
    public async Task HighByteUnquotedIdentifierView_BlocksReferencedTableRebuild()
    {
        // Arrange
        const string table = "\u20AC";
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            $"""
            CREATE TABLE {table} (Id INTEGER NOT NULL, CONSTRAINT
            pk_high_byte_identifier PRIMARY KEY (Id)); CREATE VIEW high_byte_identifier_view
            AS SELECT Id
            FROM {table};
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteHighByteIdentifierRebuildTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_high_byte_identifier_id", table, "Id > 0");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-high-byte-identifier"),
                cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_view", assessment.AnalysisCode);
    }

    /// <summary>
    /// Index without explicit collation uses column collation for matching.
    /// </summary>
    [Fact]
    public async Task IndexWithoutExplicitCollation_UsesColumnCollationForMatching()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE inherited_collation (Id INTEGER NOT NULL PRIMARY KEY, Code TEXT COLLATE NOCASE NOT NULL);
            CREATE INDEX ix_inherited_collation_code ON inherited_collation (Code);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_inherited_collation_code", "inherited_collation", ["Code"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var matching = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-inherited-index-collation"),
            cancellationToken: CancellationToken.None);

        await ExecuteSqlAsync(
            connection,
            """
            DROP INDEX ix_inherited_collation_code;
            CREATE INDEX ix_inherited_collation_code ON inherited_collation (Code COLLATE RTRIM);
            """,
            cancellationToken: CancellationToken.None);

        var different = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-different-index-collation"),
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp,
            Assert.Single(matching.Assessments)
                .Action);

        Assert.Equal(SafeMigrationObservedState.Different,
            Assert.Single(different.Assessments)
                .ObservedState);
    }

    /// <summary>
    /// Required column backfill default is only normalized for exact CLR default.
    /// </summary>
    /// <param name="backfillDefault">The backfill default defining this isolated fixture case.</param>
    /// <param name="expectedStatus">The exact expected status.</param>
    [Theory]
    [InlineData(0, SafeMigrationReportStatus.Ready)]
    [InlineData(1, SafeMigrationReportStatus.Blocked)]
    public async Task RequiredColumnBackfillDefault_IsOnlyNormalizedForExactClrDefault(
        int backfillDefault,
        SafeMigrationReportStatus expectedStatus
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE backfill_default_entities (
                Id INTEGER NOT NULL,
                CONSTRAINT pk_backfill_default_entities PRIMARY KEY (Id)
            );
            INSERT INTO backfill_default_entities (Id) VALUES (1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteBackfillDefaultRebuildTestContext(connection);
        var addColumn = new MigrationBuilder(context.Database.ProviderName!);
        addColumn.AddColumn<int>(
            "Priority",
            "backfill_default_entities",
            type: "INTEGER",
            nullable: false,
            defaultValue: backfillDefault);

        var addCheck = new MigrationBuilder(context.Database.ProviderName!);
        addCheck.AddCheckConstraintIfNotExists(
            "ck_backfill_default_entities_priority",
            "backfill_default_entities",
            "Priority >= 0");

        var retainedRowCount = 0;
        var normalizedDefaultCount = 0;

        // Act
        await ExecuteOperationsAsync(context, addColumn.Operations, cancellationToken: CancellationToken.None);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                addCheck.Operations,
                new SafeMigrationRunOptions("sqlite-ef-backfill-default"),
                cancellationToken: CancellationToken.None);

        if (expectedStatus == SafeMigrationReportStatus.Ready)
        {
            await ExecuteOperationsAsync(context, addCheck.Operations, cancellationToken: CancellationToken.None);

            retainedRowCount = await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM backfill_default_entities WHERE Id = 1 AND Priority = 0;",
                cancellationToken: CancellationToken.None);

            normalizedDefaultCount = await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('backfill_default_entities') "
                + "WHERE name = 'Priority' AND dflt_value IS NULL;",
                cancellationToken: CancellationToken.None);
        }

        // Assert
        Assert.True(
            report.Status == expectedStatus,
            $"Expected status '{expectedStatus}', actual '{report.Status}': "
            + string.Join(
                ", ",
                report.Assessments.Select(static value =>
                    $"{value.ObservedState}/{value.AnalysisCode}")));

        if (expectedStatus == SafeMigrationReportStatus.Ready)
        {
            Assert.Equal(1, retainedRowCount);
            Assert.Equal(1, normalizedDefaultCount);
        }
        else
        {
            var assessment = Assert.Single(report.Assessments);
            Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
            Assert.Equal("table_rebuild_column_drift", assessment.AnalysisCode);
        }
    }

    /// <summary>
    /// Converted required column backfill uses the EF provider default.
    /// </summary>
    [Fact]
    public async Task ConvertedRequiredColumnBackfill_UsesTheEfProviderDefault()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE converted_backfill_entities (
                Id INTEGER NOT NULL,
                CONSTRAINT pk_converted_backfill_entities PRIMARY KEY (Id)
            );
            INSERT INTO converted_backfill_entities (Id) VALUES (1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteConvertedBackfillDefaultRebuildTestContext(connection);
        var addColumn = new MigrationBuilder(context.Database.ProviderName!);
        addColumn.AddColumn<string>(
            "State",
            "converted_backfill_entities",
            type: "TEXT",
            nullable: false,
            defaultValue: string.Empty);

        var addCheck = new MigrationBuilder(context.Database.ProviderName!);
        addCheck.AddCheckConstraintIfNotExists(
            "ck_converted_backfill_entities_state",
            "converted_backfill_entities",
            "length(\"State\") >= 0");

        // Act
        await ExecuteOperationsAsync(context, addColumn.Operations, cancellationToken: CancellationToken.None);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                addCheck.Operations,
                new SafeMigrationRunOptions("sqlite-converted-backfill-default"),
                cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, addCheck.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM converted_backfill_entities "
            + "WHERE Id = 1 AND State = '';",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('converted_backfill_entities') "
            + "WHERE name = 'State' AND dflt_value IS NULL;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);
    }

    /// <summary>
    /// Missing foreign key principal only blocks non null dependent keys.
    /// </summary>
    /// <param name="parentId">The parent id defining this isolated fixture case.</param>
    /// <param name="expectedStatus">The exact expected status.</param>
    [Theory]
    [InlineData(null, SafeMigrationReportStatus.Ready)]
    [InlineData(42, SafeMigrationReportStatus.Blocked)]
    public async Task MissingForeignKeyPrincipal_OnlyBlocksNonNullDependentKeys(
        int? parentId,
        SafeMigrationReportStatus expectedStatus
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            $"""
            PRAGMA foreign_keys = OFF; CREATE TABLE missing_principal_children (Id INTEGER
            NOT NULL,
            ParentId INTEGER NULL, CONSTRAINT pk_missing_principal_children PRIMARY
            KEY (Id),
            CONSTRAINT fk_missing_principal_children_parent FOREIGN KEY (ParentId) REFERENCES
            missing_principals (Id)); CREATE INDEX ix_missing_principal_children_parent_id
            ON missing_principal_children (ParentId); INSERT INTO missing_principal_children
            (Id,
            ParentId) VALUES (1, {(parentId.HasValue
                ? parentId.Value.ToString(CultureInfo.InvariantCulture)
                : "NULL")}); PRAGMA foreign_keys =
            ON;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteMissingPrincipalForeignKeyTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_missing_principal_children_id",
            "missing_principal_children",
            "Id > 0");

        var retainedRowCount = 0;

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-missing-foreign-key-principal"),
                cancellationToken: CancellationToken.None);

        if (expectedStatus == SafeMigrationReportStatus.Ready)
        {
            await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

            retainedRowCount = await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM missing_principal_children "
                + "WHERE Id = 1 AND ParentId IS NULL;",
                cancellationToken: CancellationToken.None);
        }

        // Assert
        Assert.True(
            report.Status == expectedStatus,
            $"Expected status '{expectedStatus}', actual '{report.Status}': "
            + string.Join(
                ", ",
                report.Assessments.Select(static value =>
                    $"{value.ObservedState}/{value.AnalysisCode}")));

        if (expectedStatus == SafeMigrationReportStatus.Ready)
        {
            Assert.Equal(1, retainedRowCount);
        }
        else
        {
            var assessment = Assert.Single(report.Assessments);
            Assert.Equal("table_rebuild_foreign_key_violation", assessment.AnalysisCode);
            Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        }
    }

    /// <summary>
    /// Check like text in default literal does not create a phantom constraint.
    /// </summary>
    [Fact]
    public async Task CheckLikeTextInDefaultLiteral_DoesNotCreateAPhantomConstraint()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE check_literal_entities (Id INTEGER NOT NULL, Note TEXT NULL
            DEFAULT 'mycheck(',
            CONSTRAINT pk_check_literal_entities PRIMARY KEY (Id)); INSERT INTO check_literal_entities
            (Id) VALUES (1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteCheckLiteralRebuildTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_check_literal_entities_id",
            "check_literal_entities",
            "Id > 0");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-check-like-default-literal"),
                cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM check_literal_entities "
            + "WHERE Id = 1 AND Note = 'mycheck(';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Foreign key probe uses parent column collation.
    /// </summary>
    [Fact]
    public async Task ForeignKeyProbe_UsesParentColumnCollation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE collation_parents (Id INTEGER NOT NULL, Code TEXT COLLATE NOCASE NOT
            NULL,
            CONSTRAINT pk_collation_parents PRIMARY KEY (Id), CONSTRAINT uq_collation_parents_code
            UNIQUE (Code)); CREATE TABLE collation_children (Id INTEGER NOT NULL,
            ParentCode TEXT NOT NULL, CONSTRAINT pk_collation_children PRIMARY KEY (Id));
            CREATE INDEX ix_collation_children_parent_code ON collation_children (ParentCode);
            INSERT INTO collation_parents (Id,
            Code) VALUES (1, 'parent'); INSERT INTO collation_children (Id, ParentCode)
            VALUES (11,
            'PARENT');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteForeignKeyCollationTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "fk_collation_children_parent",
            "collation_children",
            ["ParentCode"],
            "collation_parents",
            ["Code"]);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-parent-collation"),
                cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_check;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(
            0,
            observedScalar1);
    }

    /// <summary>
    /// Rebuild with retained pre existing orphan is data blocked during preflight.
    /// </summary>
    [Fact]
    public async Task RebuildWithRetainedPreExistingOrphan_IsDataBlockedDuringPreflight()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            PRAGMA foreign_keys = OFF; CREATE TABLE collation_parents (Id INTEGER NOT
            NULL,
            Code TEXT COLLATE NOCASE NOT NULL, CONSTRAINT pk_collation_parents PRIMARY
            KEY (Id),
            CONSTRAINT uq_collation_parents_code UNIQUE (Code)); CREATE TABLE collation_children
            (Id INTEGER NOT NULL,
            ParentCode TEXT NOT NULL, CONSTRAINT pk_collation_children PRIMARY KEY (Id),
            CONSTRAINT fk_collation_children_parent FOREIGN KEY (ParentCode) REFERENCES
            collation_parents (Code)); CREATE INDEX ix_collation_children_parent_code
            ON collation_children (ParentCode); INSERT INTO collation_children (Id,
            ParentCode) VALUES (11, 'missing'); PRAGMA foreign_keys = ON;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteForeignKeyCollationTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_collation_parents_id", "collation_parents", "Id > 0");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-retained-orphan"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'ck_collation_parents_id';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal("table_rebuild_foreign_key_violation", assessment.AnalysisCode);
        Assert.Equal(
            0,
            observedScalar1);
    }

    /// <summary>
    /// Safe rebuild and following provider columns execute as one ordered structural segment.
    /// </summary>
    [Fact]
    public async Task SafeRebuildAndFollowingProviderColumns_ExecuteAsOneOrderedStructuralSegment()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE structural_blogs (Id INTEGER NOT NULL PRIMARY KEY); CREATE
            TABLE structural_posts (Id INTEGER NOT NULL,
            Title TEXT NOT NULL, BlogId INTEGER NULL, CONSTRAINT pk_structural_posts
            PRIMARY KEY (Id),
            CONSTRAINT fk_structural_posts_blog FOREIGN KEY (BlogId) REFERENCES structural_blogs
            (Id)); INSERT INTO structural_blogs (Id) VALUES (1); INSERT INTO structural_posts
            (Id,
            Title, BlogId) VALUES (11, 'preserve', 1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteStructuralSequenceTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_structural_posts_blog", "structural_posts");
        builder.DropColumn("BlogId", "structural_posts");
        builder.AddColumn<int>(
            "AuthorId",
            "structural_posts",
            type: "INTEGER",
            nullable: true);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-ordered-structural-segment"),
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);
        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-ordered-structural-segment"),
            cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM structural_posts "
            + "WHERE Id = 11 AND Title = 'preserve' AND AuthorId IS NULL;",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('structural_posts') WHERE name = 'BlogId';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            0,
            observedScalar2);
    }

    /// <summary>
    /// Later structural operations across data barrier do not authorize earlier rebuild.
    /// </summary>
    [Fact]
    public async Task LaterStructuralOperationsAcrossDataBarrier_DoNotAuthorizeEarlierRebuild()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE structural_blogs (Id INTEGER NOT NULL PRIMARY KEY); CREATE
            TABLE structural_posts (Id INTEGER NOT NULL,
            Title TEXT NOT NULL, BlogId INTEGER NULL, CONSTRAINT pk_structural_posts
            PRIMARY KEY (Id),
            CONSTRAINT fk_structural_posts_blog FOREIGN KEY (BlogId) REFERENCES structural_blogs
            (Id));
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteStructuralSequenceTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_structural_posts_blog", "structural_posts");
        builder.InsertData(
            "structural_posts",
            ["Id", "Title", "BlogId"],
            [11, "barrier", 1]);

        builder.DropColumn("BlogId", "structural_posts");
        builder.AddColumn<int>(
            "AuthorId",
            "structural_posts",
            type: "INTEGER",
            nullable: true);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-future-operation-barrier"),
                cancellationToken: CancellationToken.None);

        var assessment = report.Assessments[0];
        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('structural_posts');",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.True(
            assessment.AnalysisCode is "table_rebuild_unmanaged_column"
                or "table_rebuild_unmodeled_target_column",
            $"Unexpected analysis code '{assessment.AnalysisCode}'.");

        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Rebuild with unrelated physical shape drift is rejected without normalization.
    /// </summary>
    /// <param name="drift">The drift defining this isolated fixture case.</param>
    /// <param name="expectedCode">The exact expected code.</param>
    [Theory]
    [InlineData("column", "table_rebuild_column_drift")]
    [InlineData("index", "table_rebuild_index_drift")]
    public async Task RebuildWithUnrelatedPhysicalShapeDrift_IsRejectedWithoutNormalization(
        string drift,
        string expectedCode
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        var codeStoreType = drift == "column"
            ? "INTEGER"
            : "TEXT";

        var indexKind = drift == "index"
            ? "UNIQUE "
            : string.Empty;

        await ExecuteSqlAsync(
            connection,
            $"""
            CREATE TABLE rebuild_shape_entities (Id INTEGER NOT NULL, Code {codeStoreType}
            NOT NULL,
            CONSTRAINT pk_rebuild_shape_entities PRIMARY KEY (Id)); CREATE {indexKind}INDEX
            ix_rebuild_shape_entities_code ON rebuild_shape_entities (Code); INSERT
            INTO rebuild_shape_entities (Id,
            Code) VALUES (1, 'preserve');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteRebuildShapeTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_rebuild_shape_entities_id",
            "rebuild_shape_entities",
            "Id > 0");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-rebuild-shape-drift"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM rebuild_shape_entities WHERE Id = 1;",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(expectedCode, assessment.AnalysisCode);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Rebuild with unmodeled constraint option is rejected without mutation.
    /// </summary>
    [Fact]
    public async Task RebuildWithUnmodeledConstraintOption_IsRejectedWithoutMutation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE rebuild_entities (Id INTEGER NOT NULL, Code TEXT NOT NULL,
            CONSTRAINT pk_rebuild_entities PRIMARY KEY (Id) ON CONFLICT REPLACE, CONSTRAINT
            uq_rebuild_entities_code UNIQUE (Code)); INSERT INTO rebuild_entities (Id,
            Code) VALUES (1, 'existing');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_rebuild_entities_code",
            "rebuild_entities",
            "length(\"Code\") > 0");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-provider-constraint-option"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM rebuild_entities WHERE Id = 1 AND Code = 'existing';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_provider_constraint_option", assessment.AnalysisCode);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Provider add column and safe check on existing column converge on populated table.
    /// </summary>
    [Fact]
    public async Task ProviderAddColumnAndSafeCheckOnExistingColumn_ConvergeOnPopulatedTable()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE add_column_check_entities (
                Id INTEGER NOT NULL,
                CONSTRAINT pk_add_column_check_entities PRIMARY KEY (Id)
            );
            INSERT INTO add_column_check_entities (Id) VALUES (1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteAddColumnCheckTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumn<string>(
            "Description",
            "add_column_check_entities",
            type: "TEXT",
            nullable: true);

        builder.AddCheckConstraintIfNotExists(
            "ck_add_column_check_entities_id",
            "add_column_check_entities",
            "Id > 0");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-add-column-check"),
                cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM add_column_check_entities "
            + "WHERE Id = 1 AND Description IS NULL;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Safe check on column added in same segment is prerequisite missing without mutation.
    /// </summary>
    [Fact]
    public async Task SafeCheckOnColumnAddedInSameSegment_IsPrerequisiteMissingWithoutMutation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE add_column_referenced_check_entities (Id INTEGER NOT NULL,
            CONSTRAINT pk_add_column_referenced_check_entities PRIMARY KEY (Id)); INSERT
            INTO add_column_referenced_check_entities (Id) VALUES (1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteAddColumnReferencedCheckTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumn<string>(
            "Description",
            "add_column_referenced_check_entities",
            type: "TEXT",
            nullable: false,
            defaultValue: "pending");

        builder.AddCheckConstraintIfNotExists(
            "ck_add_column_referenced_check_entities_description",
            "add_column_referenced_check_entities",
            "length(\"Description\") > 0");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-add-column-referenced-check"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('add_column_referenced_check_entities') "
            + "WHERE name = 'Description';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(
            report.Assessments,
            static value => value.OperationKind == SafeMigrationOperationKind.EnsureCheckConstraint);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, assessment.ObservedState);
        Assert.Equal("classified_prerequisite_missing", assessment.AnalysisCode);
        Assert.Equal(
            0,
            observedScalar1);
    }

    /// <summary>
    /// Primary key drop with different physical name is rejected without mutation.
    /// </summary>
    [Fact]
    public async Task PrimaryKeyDropWithDifferentPhysicalName_IsRejectedWithoutMutation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE primary_key_drop_entities (Id INTEGER NOT NULL, Value TEXT
            NOT NULL,
            CONSTRAINT actual_primary_key PRIMARY KEY (Id)); INSERT INTO primary_key_drop_entities
            (Id,
            Value) VALUES (1, 'preserve');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqlitePrimaryKeyDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropPrimaryKeyIfExists("expected_primary_key", "primary_key_drop_entities");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-primary-key-name"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM primary_key_drop_entities WHERE Id = 1 AND Value = 'preserve';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Named primary key drop does not claim anonymous primary key.
    /// </summary>
    [Fact]
    public async Task NamedPrimaryKeyDropDoesNotClaimAnonymousPrimaryKey()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE primary_key_drop_entities (Id INTEGER NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL); INSERT INTO primary_key_drop_entities (Id, Value)
            VALUES (1,
            'preserve');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqlitePrimaryKeyDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropPrimaryKeyIfExists("expected_primary_key", "primary_key_drop_entities");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-anonymous-primary-key"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM primary_key_drop_entities WHERE Id = 1 AND Value = 'preserve';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Escaped identifier view dependency blocks rebuild.
    /// </summary>
    [Fact]
    public async Task EscapedIdentifierViewDependency_BlocksRebuild()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE "order""items" (Id INTEGER NOT NULL, CONSTRAINT pk_order_items
            PRIMARY KEY (Id)); CREATE VIEW order_item_view AS SELECT Id
            FROM main."order""items";
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteEscapedIdentifierRebuildTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_order_items_id", "order\"items", "Id > 0");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-escaped-view-dependency"),
                cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_view", assessment.AnalysisCode);
    }

    /// <summary>
    /// Case variant index drop and ensure use one projected physical identity.
    /// </summary>
    [Fact]
    public async Task CaseVariantIndexDropAndEnsure_UseOneProjectedPhysicalIdentity()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE case_identity_entities (Id INTEGER NOT NULL, Code TEXT NULL);
            CREATE INDEX IX_Case_Identity_Code ON case_identity_entities (Code);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_Case_Identity_Code", "case_identity_entities");
        builder.CreateIndexIfNotExists(
            "ix_case_identity_code",
            "case_identity_entities",
            ["Code"]);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-case-identity"),
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);
        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-case-identity"),
            cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema "
            + "WHERE type = 'index' AND name = 'ix_case_identity_code';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Ensure table with attached principal qualifier is rejected before DDL.
    /// </summary>
    [Fact]
    public async Task EnsureTableWithAttachedPrincipalQualifier_IsRejectedBeforeDdl()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var foreignKey = new ExpectedForeignKeyDefinition(
            "fk_qualified_child_parent",
            "qualified_child",
            ["ParentId"],
            "qualified_parent",
            ["Id"],
            principalSchema: "attached");

        builder.EnsureTable(
            new ExpectedTableDefinition(
                "qualified_child",
                [
                    new ExpectedColumnDefinition("Id", typeof(int), isNullable: false, storeType: "INTEGER"),
                    new ExpectedColumnDefinition("ParentId", typeof(int), isNullable: true, storeType: "INTEGER"),
                ],
                foreignKeys: [foreignKey]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-nested-qualifier"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'qualified_child';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("database_qualifier_mismatch", assessment.AnalysisCode);
        Assert.Equal(
            0,
            observedScalar1);
    }

    /// <summary>
    /// Attached database drop is classified as qualifier mismatch.
    /// </summary>
    [Fact]
    public async Task AttachedDatabaseDrop_IsClassifiedAsQualifierMismatch()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropSchemaIfExists("attached");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-drop-attached"),
                cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("database_qualifier_mismatch", assessment.AnalysisCode);
    }
}
