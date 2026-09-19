namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
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
        await using var connection = await OpenConnectionAsync();
        if (!foreignKeysEnabled)
        {
            await ExecuteSqlAsync(connection, "PRAGMA foreign_keys = OFF;");
        }

        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE drop_parent (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE drop_child (Id INTEGER NOT NULL PRIMARY KEY, ParentId INTEGER NULL, "
            + $"CONSTRAINT fk_drop_child_parent FOREIGN KEY (ParentId) REFERENCES drop_parent (Id) ON DELETE {deleteAction}); "
            + "INSERT INTO drop_parent (Id) VALUES (1); "
            + "INSERT INTO drop_child (Id, ParentId) VALUES (11, 1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("drop_parent");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-drop-incoming-foreign-key"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_drop_foreign_key_dependency", assessment.AnalysisCode);
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM drop_parent;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM drop_child WHERE Id = 11 AND ParentId = 1;"));
    }

    [Fact]
    public async Task OrderedDependentAndPrincipalDrops_ConvergeWithoutForeignKeySideEffects()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE ordered_drop_parent (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE ordered_drop_child (Id INTEGER NOT NULL PRIMARY KEY, ParentId INTEGER NULL, "
            + "CONSTRAINT fk_ordered_drop_child_parent FOREIGN KEY (ParentId) "
            + "REFERENCES ordered_drop_parent (Id) ON DELETE CASCADE); "
            + "INSERT INTO ordered_drop_parent (Id) VALUES (1); "
            + "INSERT INTO ordered_drop_child (Id, ParentId) VALUES (11, 1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("ordered_drop_child");
        builder.DropTableIfExists("ordered_drop_parent");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-ordered-dependent-drop"));

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'table' AND name IN ('ordered_drop_parent', 'ordered_drop_child');"));
    }

    [Fact]
    public async Task ProjectedChildTable_BlocksLaterPrincipalDrop()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE projected_parent (Id INTEGER NOT NULL PRIMARY KEY);");
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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-projected-child-drop"));

        var assessment = Assert.Single(
            report.Assessments,
            static value => value.OperationKind == SafeMigrationOperationKind.DropTable);

        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_drop_foreign_key_dependency", assessment.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'table' AND name = 'projected_parent';"));
    }

    [Fact]
    public async Task ProjectedForeignKeyAndRename_BlockRenamedPrincipalDrop()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE collation_parents (Id INTEGER NOT NULL, Code TEXT COLLATE NOCASE NOT NULL, "
            + "CONSTRAINT pk_collation_parents PRIMARY KEY (Id), "
            + "CONSTRAINT uq_collation_parents_code UNIQUE (Code)); "
            + "CREATE TABLE collation_children (Id INTEGER NOT NULL, ParentCode TEXT NOT NULL, "
            + "CONSTRAINT pk_collation_children PRIMARY KEY (Id)); "
            + "CREATE INDEX ix_collation_children_parent_code ON collation_children (ParentCode);");
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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-projected-foreign-key-rename"));

        var assessment = Assert.Single(
            report.Assessments,
            static value => value.OperationKind == SafeMigrationOperationKind.DropTable);

        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_drop_foreign_key_dependency", assessment.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'table' AND name = 'collation_parents';"));
    }

    [Fact]
    public async Task ProjectedDependentDrop_AllowsLaterPrincipalDrop()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE projected_drop_parent (Id INTEGER NOT NULL PRIMARY KEY);");
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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-projected-dependent-drop"));

        var principalDrop = Assert.Single(
            report.Assessments,
            static value => value.OperationKind == SafeMigrationOperationKind.DropTable
                && value.ObjectName == "projected_drop_parent");

        Assert.Equal(SafeMigrationAction.Apply, principalDrop.Action);
    }

    [Fact]
    public async Task SelfReferencingTableDrop_DoesNotReportAnExternalDependency()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE self_drop_tree (Id INTEGER NOT NULL PRIMARY KEY, ParentId INTEGER NULL, "
            + "CONSTRAINT fk_self_drop_tree_parent FOREIGN KEY (ParentId) "
            + "REFERENCES self_drop_tree (Id) ON DELETE CASCADE); "
            + "INSERT INTO self_drop_tree (Id, ParentId) VALUES (1, NULL), (2, 1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("self_drop_tree");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-self-referencing-drop"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'table' AND name = 'self_drop_tree';"));
    }

    [Fact]
    public async Task RenameWithLegacyAlterTableEnabled_IsRejectedBeforeReferenceDrift()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE legacy_rename_source (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE VIEW legacy_rename_view AS SELECT Id FROM legacy_rename_source; "
            + "PRAGMA legacy_alter_table = ON;");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("legacy_rename_source", "legacy_rename_target");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-legacy-alter-table"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("legacy_alter_table", assessment.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'table' AND name = 'legacy_rename_source';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'view' AND name = 'legacy_rename_view' "
                + "AND sql LIKE '%legacy_rename_source%';"));
    }

    [Fact]
    public async Task CrossTableSqlitePrefixedTrigger_BlocksReferencedTableRebuild()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE structural_blogs (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE structural_posts (Id INTEGER NOT NULL, Title TEXT NOT NULL, BlogId INTEGER NULL, "
            + "CONSTRAINT pk_structural_posts PRIMARY KEY (Id), "
            + "CONSTRAINT fk_structural_posts_blog FOREIGN KEY (BlogId) REFERENCES structural_blogs (Id)); "
            + "CREATE TABLE structural_post_source (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TRIGGER SqliteAudit_StructuralPosts AFTER INSERT ON structural_post_source "
            + "BEGIN INSERT INTO structural_posts (Id, Title, BlogId) VALUES (NEW.Id, 'trigger', NULL); END;");
        await using var context = new SqliteStructuralSequenceTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_structural_posts_blog", "structural_posts");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-cross-table-trigger"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_trigger", assessment.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'SqliteAudit_StructuralPosts';"));
    }

    [Fact]
    public async Task NonAsciiCaseDistinctIdentifiers_RemainIndependentPhysicalObjects()
    {
        const string uppercase = "\u00C9";
        const string lowercase = "\u00E9";
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            $"CREATE TABLE \"{uppercase}\" (Id INTEGER NOT NULL); "
            + $"CREATE TABLE \"{lowercase}\" (Id INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists(uppercase);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-non-ascii-identifiers"));

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                $"SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'table' AND name = '{uppercase}';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                $"SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'table' AND name = '{lowercase}';"));
    }

    [Fact]
    public async Task SqlitePrefixedUserTable_RemainsVisibleToLifecycleAnalysis()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE SqliteCache (Id INTEGER NOT NULL PRIMARY KEY);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("SqliteCache");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-user-prefix"));

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'SqliteCache';"));
    }

    [Theory]
    [InlineData("WITHOUT\nROWID")]
    [InlineData("WITHOUT /*gap*/ ROWID")]
    [InlineData("WITHOUT --gap\nROWID")]
    public async Task TriviaSeparatedWithoutRowIdOption_BlocksTableRebuild(
        string tableOption
    )
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE newline_without_rowid (Id INTEGER NOT NULL PRIMARY KEY, Value TEXT NULL) "
            + $"{tableOption};");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_newline_without_rowid", "newline_without_rowid", "Id > 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-newline-without-rowid"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("without_rowid_table_rebuild", assessment.AnalysisCode);
    }

    [Fact]
    public async Task HighByteUnquotedIdentifierView_BlocksReferencedTableRebuild()
    {
        const string table = "\u20AC";
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            $"CREATE TABLE {table} (Id INTEGER NOT NULL, CONSTRAINT pk_high_byte_identifier PRIMARY KEY (Id)); "
            + $"CREATE VIEW high_byte_identifier_view AS SELECT Id FROM {table};");
        await using var context = new SqliteHighByteIdentifierRebuildTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_high_byte_identifier_id", table, "Id > 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-high-byte-identifier"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_view", assessment.AnalysisCode);
    }

    [Fact]
    public async Task IndexWithoutExplicitCollation_UsesColumnCollationForMatching()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE inherited_collation (Id INTEGER NOT NULL PRIMARY KEY, Code TEXT COLLATE NOCASE NOT NULL); "
            + "CREATE INDEX ix_inherited_collation_code ON inherited_collation (Code);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_inherited_collation_code", "inherited_collation", ["Code"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        var matching = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-inherited-index-collation"));

        await ExecuteSqlAsync(
            connection,
            "DROP INDEX ix_inherited_collation_code; "
            + "CREATE INDEX ix_inherited_collation_code ON inherited_collation (Code COLLATE RTRIM);");
        var different = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-different-index-collation"));

        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(matching.Assessments).Action);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(different.Assessments).ObservedState);
    }

    [Theory]
    [InlineData(0, SafeMigrationReportStatus.Ready)]
    [InlineData(1, SafeMigrationReportStatus.Blocked)]
    public async Task RequiredColumnBackfillDefault_IsOnlyNormalizedForExactClrDefault(
        int backfillDefault,
        SafeMigrationReportStatus expectedStatus
    )
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE backfill_default_entities (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_backfill_default_entities PRIMARY KEY (Id)); "
            + "INSERT INTO backfill_default_entities (Id) VALUES (1);");
        await using var context = new SqliteBackfillDefaultRebuildTestContext(connection);
        var addColumn = new MigrationBuilder(context.Database.ProviderName!);
        addColumn.AddColumn<int>(
            "Priority",
            "backfill_default_entities",
            type: "INTEGER",
            nullable: false,
            defaultValue: backfillDefault);
        await ExecuteOperationsAsync(context, addColumn.Operations);
        var addCheck = new MigrationBuilder(context.Database.ProviderName!);
        addCheck.AddCheckConstraintIfNotExists(
            "ck_backfill_default_entities_priority",
            "backfill_default_entities",
            "Priority >= 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                addCheck.Operations,
                new SafeMigrationRunOptions("sqlite-ef-backfill-default"));

        Assert.True(
            report.Status == expectedStatus,
            $"Expected status '{expectedStatus}', actual '{report.Status}': "
            + string.Join(
                ", ",
                report.Assessments.Select(static value =>
                    $"{value.ObservedState}/{value.AnalysisCode}")));
        if (expectedStatus == SafeMigrationReportStatus.Ready)
        {
            await ExecuteOperationsAsync(context, addCheck.Operations);

            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connection,
                    "SELECT COUNT(*) FROM backfill_default_entities WHERE Id = 1 AND Priority = 0;"));
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_xinfo('backfill_default_entities') "
                    + "WHERE name = 'Priority' AND dflt_value IS NULL;"));
        }
        else
        {
            var assessment = Assert.Single(report.Assessments);
            Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
            Assert.Equal("table_rebuild_column_drift", assessment.AnalysisCode);
        }
    }

    [Fact]
    public async Task ConvertedRequiredColumnBackfill_UsesTheEfProviderDefault()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE converted_backfill_entities (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_converted_backfill_entities PRIMARY KEY (Id)); "
            + "INSERT INTO converted_backfill_entities (Id) VALUES (1);");
        await using var context = new SqliteConvertedBackfillDefaultRebuildTestContext(connection);
        var addColumn = new MigrationBuilder(context.Database.ProviderName!);
        addColumn.AddColumn<string>(
            "State",
            "converted_backfill_entities",
            type: "TEXT",
            nullable: false,
            defaultValue: string.Empty);
        await ExecuteOperationsAsync(context, addColumn.Operations);
        var addCheck = new MigrationBuilder(context.Database.ProviderName!);
        addCheck.AddCheckConstraintIfNotExists(
            "ck_converted_backfill_entities_state",
            "converted_backfill_entities",
            "length(\"State\") >= 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                addCheck.Operations,
                new SafeMigrationRunOptions("sqlite-converted-backfill-default"));

        await ExecuteOperationsAsync(context, addCheck.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM converted_backfill_entities "
                + "WHERE Id = 1 AND State = '';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('converted_backfill_entities') "
                + "WHERE name = 'State' AND dflt_value IS NULL;"));
    }

    [Theory]
    [InlineData(null, SafeMigrationReportStatus.Ready)]
    [InlineData(42, SafeMigrationReportStatus.Blocked)]
    public async Task MissingForeignKeyPrincipal_OnlyBlocksNonNullDependentKeys(
        int? parentId,
        SafeMigrationReportStatus expectedStatus
    )
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "PRAGMA foreign_keys = OFF; "
            + "CREATE TABLE missing_principal_children (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_missing_principal_children PRIMARY KEY (Id), "
            + "CONSTRAINT fk_missing_principal_children_parent FOREIGN KEY (ParentId) "
            + "REFERENCES missing_principals (Id)); "
            + "CREATE INDEX ix_missing_principal_children_parent_id "
            + "ON missing_principal_children (ParentId); "
            + $"INSERT INTO missing_principal_children (Id, ParentId) VALUES (1, "
            + (parentId.HasValue ? parentId.Value.ToString(CultureInfo.InvariantCulture) : "NULL")
            + "); PRAGMA foreign_keys = ON;");
        await using var context = new SqliteMissingPrincipalForeignKeyTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_missing_principal_children_id",
            "missing_principal_children",
            "Id > 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-missing-foreign-key-principal"));

        Assert.True(
            report.Status == expectedStatus,
            $"Expected status '{expectedStatus}', actual '{report.Status}': "
            + string.Join(
                ", ",
                report.Assessments.Select(static value =>
                    $"{value.ObservedState}/{value.AnalysisCode}")));
        if (expectedStatus == SafeMigrationReportStatus.Ready)
        {
            await ExecuteOperationsAsync(context, builder.Operations);

            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connection,
                    "SELECT COUNT(*) FROM missing_principal_children "
                    + "WHERE Id = 1 AND ParentId IS NULL;"));
        }
        else
        {
            var assessment = Assert.Single(report.Assessments);
            Assert.Equal("table_rebuild_foreign_key_violation", assessment.AnalysisCode);
            Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        }
    }

    [Fact]
    public async Task CheckLikeTextInDefaultLiteral_DoesNotCreateAPhantomConstraint()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE check_literal_entities (Id INTEGER NOT NULL, "
            + "Note TEXT NULL DEFAULT 'mycheck(', "
            + "CONSTRAINT pk_check_literal_entities PRIMARY KEY (Id)); "
            + "INSERT INTO check_literal_entities (Id) VALUES (1);");
        await using var context = new SqliteCheckLiteralRebuildTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_check_literal_entities_id",
            "check_literal_entities",
            "Id > 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-check-like-default-literal"));

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM check_literal_entities "
                + "WHERE Id = 1 AND Note = 'mycheck(';"));
    }

    [Fact]
    public async Task ForeignKeyProbe_UsesParentColumnCollation()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE collation_parents (Id INTEGER NOT NULL, Code TEXT COLLATE NOCASE NOT NULL, "
            + "CONSTRAINT pk_collation_parents PRIMARY KEY (Id), "
            + "CONSTRAINT uq_collation_parents_code UNIQUE (Code)); "
            + "CREATE TABLE collation_children (Id INTEGER NOT NULL, ParentCode TEXT NOT NULL, "
            + "CONSTRAINT pk_collation_children PRIMARY KEY (Id)); "
            + "CREATE INDEX ix_collation_children_parent_code ON collation_children (ParentCode); "
            + "INSERT INTO collation_parents (Id, Code) VALUES (1, 'parent'); "
            + "INSERT INTO collation_children (Id, ParentCode) VALUES (11, 'PARENT');");
        await using var context = new SqliteForeignKeyCollationTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "fk_collation_children_parent",
            "collation_children",
            ["ParentCode"],
            "collation_parents",
            ["Code"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-parent-collation"));

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(
            0,
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task RebuildWithRetainedPreExistingOrphan_IsDataBlockedDuringPreflight()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "PRAGMA foreign_keys = OFF; "
            + "CREATE TABLE collation_parents (Id INTEGER NOT NULL, Code TEXT COLLATE NOCASE NOT NULL, "
            + "CONSTRAINT pk_collation_parents PRIMARY KEY (Id), "
            + "CONSTRAINT uq_collation_parents_code UNIQUE (Code)); "
            + "CREATE TABLE collation_children (Id INTEGER NOT NULL, ParentCode TEXT NOT NULL, "
            + "CONSTRAINT pk_collation_children PRIMARY KEY (Id), "
            + "CONSTRAINT fk_collation_children_parent FOREIGN KEY (ParentCode) "
            + "REFERENCES collation_parents (Code)); "
            + "CREATE INDEX ix_collation_children_parent_code ON collation_children (ParentCode); "
            + "INSERT INTO collation_children (Id, ParentCode) VALUES (11, 'missing'); "
            + "PRAGMA foreign_keys = ON;");
        await using var context = new SqliteForeignKeyCollationTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_collation_parents_id", "collation_parents", "Id > 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-retained-orphan"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal("table_rebuild_foreign_key_violation", assessment.AnalysisCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'ck_collation_parents_id';"));
    }

    [Fact]
    public async Task SafeRebuildAndFollowingProviderColumns_ExecuteAsOneOrderedStructuralSegment()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE structural_blogs (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE structural_posts (Id INTEGER NOT NULL, Title TEXT NOT NULL, BlogId INTEGER NULL, "
            + "CONSTRAINT pk_structural_posts PRIMARY KEY (Id), "
            + "CONSTRAINT fk_structural_posts_blog FOREIGN KEY (BlogId) REFERENCES structural_blogs (Id)); "
            + "INSERT INTO structural_blogs (Id) VALUES (1); "
            + "INSERT INTO structural_posts (Id, Title, BlogId) VALUES (11, 'preserve', 1);");
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

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-ordered-structural-segment"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-ordered-structural-segment"));

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM structural_posts "
                + "WHERE Id = 11 AND Title = 'preserve' AND AuthorId IS NULL;"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('structural_posts') WHERE name = 'BlogId';"));
    }

    [Fact]
    public async Task LaterStructuralOperationsAcrossDataBarrier_DoNotAuthorizeEarlierRebuild()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE structural_blogs (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE structural_posts (Id INTEGER NOT NULL, Title TEXT NOT NULL, BlogId INTEGER NULL, "
            + "CONSTRAINT pk_structural_posts PRIMARY KEY (Id), "
            + "CONSTRAINT fk_structural_posts_blog FOREIGN KEY (BlogId) REFERENCES structural_blogs (Id));");
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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-future-operation-barrier"));

        var assessment = report.Assessments[0];
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.True(
            assessment.AnalysisCode is "table_rebuild_unmanaged_column"
                or "table_rebuild_unmodeled_target_column",
            $"Unexpected analysis code '{assessment.AnalysisCode}'.");
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_foreign_key_list('structural_posts');"));
    }

    [Theory]
    [InlineData("column", "table_rebuild_column_drift")]
    [InlineData("index", "table_rebuild_index_drift")]
    public async Task RebuildWithUnrelatedPhysicalShapeDrift_IsRejectedWithoutNormalization(
        string drift,
        string expectedCode
    )
    {
        await using var connection = await OpenConnectionAsync();
        var codeStoreType = drift == "column" ? "INTEGER" : "TEXT";
        var indexKind = drift == "index" ? "UNIQUE " : string.Empty;
        await ExecuteSqlAsync(
            connection,
            $"CREATE TABLE rebuild_shape_entities (Id INTEGER NOT NULL, Code {codeStoreType} NOT NULL, "
            + "CONSTRAINT pk_rebuild_shape_entities PRIMARY KEY (Id)); "
            + $"CREATE {indexKind}INDEX ix_rebuild_shape_entities_code ON rebuild_shape_entities (Code); "
            + "INSERT INTO rebuild_shape_entities (Id, Code) VALUES (1, 'preserve');");
        await using var context = new SqliteRebuildShapeTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_rebuild_shape_entities_id",
            "rebuild_shape_entities",
            "Id > 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-rebuild-shape-drift"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(expectedCode, assessment.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM rebuild_shape_entities WHERE Id = 1;"));
    }

    [Fact]
    public async Task RebuildWithUnmodeledConstraintOption_IsRejectedWithoutMutation()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE rebuild_entities (Id INTEGER NOT NULL, Code TEXT NOT NULL, "
            + "CONSTRAINT pk_rebuild_entities PRIMARY KEY (Id) ON CONFLICT REPLACE, "
            + "CONSTRAINT uq_rebuild_entities_code UNIQUE (Code)); "
            + "INSERT INTO rebuild_entities (Id, Code) VALUES (1, 'existing');");
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
                new SafeMigrationRunOptions("sqlite-provider-constraint-option"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_provider_constraint_option", assessment.AnalysisCode);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM rebuild_entities WHERE Id = 1 AND Code = 'existing';"));
    }

    [Fact]
    public async Task ProviderAddColumnAndSafeCheckOnExistingColumn_ConvergeOnPopulatedTable()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE add_column_check_entities (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_add_column_check_entities PRIMARY KEY (Id)); "
            + "INSERT INTO add_column_check_entities (Id) VALUES (1);");
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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-add-column-check"));

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM add_column_check_entities "
                + "WHERE Id = 1 AND Description IS NULL;"));
    }

    [Fact]
    public async Task SafeCheckOnColumnAddedInSameSegment_IsPrerequisiteMissingWithoutMutation()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE add_column_referenced_check_entities (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_add_column_referenced_check_entities PRIMARY KEY (Id)); "
            + "INSERT INTO add_column_referenced_check_entities (Id) VALUES (1);");
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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-add-column-referenced-check"));

        var assessment = Assert.Single(
            report.Assessments,
            static value => value.OperationKind == SafeMigrationOperationKind.EnsureCheckConstraint);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, assessment.ObservedState);
        Assert.Equal("classified_prerequisite_missing", assessment.AnalysisCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('add_column_referenced_check_entities') "
                + "WHERE name = 'Description';"));
    }

    [Fact]
    public async Task PrimaryKeyDropWithDifferentPhysicalName_IsRejectedWithoutMutation()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE primary_key_drop_entities (Id INTEGER NOT NULL, Value TEXT NOT NULL, "
            + "CONSTRAINT actual_primary_key PRIMARY KEY (Id)); "
            + "INSERT INTO primary_key_drop_entities (Id, Value) VALUES (1, 'preserve');");
        await using var context = new SqlitePrimaryKeyDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropPrimaryKeyIfExists("expected_primary_key", "primary_key_drop_entities");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-primary-key-name"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM primary_key_drop_entities WHERE Id = 1 AND Value = 'preserve';"));
    }

    [Fact]
    public async Task NamedPrimaryKeyDropDoesNotClaimAnonymousPrimaryKey()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE primary_key_drop_entities (Id INTEGER NOT NULL PRIMARY KEY, Value TEXT NOT NULL); "
            + "INSERT INTO primary_key_drop_entities (Id, Value) VALUES (1, 'preserve');");
        await using var context = new SqlitePrimaryKeyDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropPrimaryKeyIfExists("expected_primary_key", "primary_key_drop_entities");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-anonymous-primary-key"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM primary_key_drop_entities WHERE Id = 1 AND Value = 'preserve';"));
    }

    [Fact]
    public async Task EscapedIdentifierViewDependency_BlocksRebuild()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE \"order\"\"items\" (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_order_items PRIMARY KEY (Id)); "
            + "CREATE VIEW order_item_view AS SELECT Id FROM main.\"order\"\"items\";");
        await using var context = new SqliteEscapedIdentifierRebuildTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_order_items_id", "order\"items", "Id > 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-escaped-view-dependency"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("table_rebuild_view", assessment.AnalysisCode);
    }

    [Fact]
    public async Task CaseVariantIndexDropAndEnsure_UseOneProjectedPhysicalIdentity()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE case_identity_entities (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "CREATE INDEX IX_Case_Identity_Code ON case_identity_entities (Code);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_Case_Identity_Code", "case_identity_entities");
        builder.CreateIndexIfNotExists(
            "ix_case_identity_code",
            "case_identity_entities",
            ["Code"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-case-identity"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-case-identity"));

        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'index' AND name = 'ix_case_identity_code';"));
    }

    [Fact]
    public async Task EnsureTableWithAttachedPrincipalQualifier_IsRejectedBeforeDdl()
    {
        await using var connection = await OpenConnectionAsync();
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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-nested-qualifier"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("database_qualifier_mismatch", assessment.AnalysisCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema WHERE name = 'qualified_child';"));
    }

    [Fact]
    public async Task AttachedDatabaseDrop_IsClassifiedAsQualifierMismatch()
    {
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropSchemaIfExists("attached");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-drop-attached"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("database_qualifier_mismatch", assessment.AnalysisCode);
    }
}
