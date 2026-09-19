namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsurePrimaryKey, "primary_key_invalid_values")]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint, "unique_constraint_duplicate_values")]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint, "check_constraint_violated")]
    public async Task ConstraintWithInvalidExistingRows_IsDataBlockedBeforeRebuild(
        SafeMigrationOperationKind operationKind,
        string expectedAnalysisCode
    )
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE constraint_data (id INTEGER NULL, code TEXT NULL, quantity INTEGER NOT NULL); "
            + "INSERT INTO constraint_data (id, code, quantity) VALUES "
            + "(NULL, 'duplicate', -1), (1, 'duplicate', 1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        switch (operationKind)
        {
            case SafeMigrationOperationKind.EnsurePrimaryKey:
                builder.AddPrimaryKeyIfNotExists("pk_constraint_data", "constraint_data", ["id"]);
                break;
            case SafeMigrationOperationKind.EnsureUniqueConstraint:
                builder.AddUniqueConstraintIfNotExists("uq_constraint_data_code", "constraint_data", ["code"]);
                break;
            case SafeMigrationOperationKind.EnsureCheckConstraint:
                builder.AddCheckConstraintIfNotExists(
                    "ck_constraint_data_quantity",
                    "constraint_data",
                    "quantity >= 0");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operationKind));
        }

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-constraint-data-block"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(expectedAnalysisCode, Assert.Single(report.Assessments).AnalysisCode);
        Assert.Equal(2, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_data;"));
    }

    [Fact]
    public async Task ForeignKeyAndSupportingIndex_ApplyReplayAndPreserveRows()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); "
            + "INSERT INTO constraint_parents (Id) VALUES (1); "
            + "INSERT INTO constraint_children (Id, ParentId) VALUES (11, 1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists(
            "ix_constraint_children_parent_id",
            "constraint_children",
            ["ParentId"]);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent",
            "constraint_children",
            ["ParentId"],
            "constraint_parents",
            ["Id"],
            onDelete: ReferentialAction.Cascade);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-foreign-key-apply"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-foreign-key-replay"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children') "
                + "WHERE \"table\" = 'constraint_parents' AND \"from\" = 'ParentId' "
                + "AND \"to\" = 'Id' AND on_delete = 'CASCADE';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM constraint_children WHERE Id = 11 AND ParentId = 1;"));
    }

    [Fact]
    public async Task ForeignKeyWithOrphanedRows_IsDataBlockedBeforeTableRebuild()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); "
            + "INSERT INTO constraint_children (Id, ParentId) VALUES (11, 999);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent",
            "constraint_children",
            ["ParentId"],
            "constraint_parents",
            ["Id"],
            onDelete: ReferentialAction.Cascade);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-foreign-key-orphan"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal("foreign_key_orphans", assessment.AnalysisCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children');"));
    }

    [Fact]
    public async Task RebuildWithUnmanagedForeignKeyActionDrift_IsRejectedWithoutMutation()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, ParentId INTEGER NULL, Legacy TEXT NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id), "
            + "CONSTRAINT fk_constraint_children_parent FOREIGN KEY (ParentId) "
            + "REFERENCES constraint_parents (Id) ON DELETE NO ACTION); "
            + "CREATE INDEX ix_constraint_children_parent_id ON constraint_children (ParentId); "
            + "INSERT INTO constraint_parents (Id) VALUES (1); "
            + "INSERT INTO constraint_children (Id, ParentId, Legacy) VALUES (11, 1, 'preserve');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("Legacy", "constraint_children");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Contains("table_rebuild_unmanaged_foreign_key", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children') WHERE on_delete = 'NO ACTION';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM constraint_children "
                + "WHERE Id = 11 AND ParentId = 1 AND Legacy = 'preserve';"));
    }

    [Fact]
    public async Task UniqueCheckAndForeignKeyDrops_RebuildOnceAndReplayAsNoOps()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE drop_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_drop_parents PRIMARY KEY (Id)); "
            + "CREATE TABLE drop_entities (Id INTEGER NOT NULL, Code TEXT NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_drop_entities PRIMARY KEY (Id), "
            + "CONSTRAINT uq_drop_entities_code UNIQUE (Code), "
            + "CONSTRAINT ck_drop_entities_code CHECK (length(Code) > 0), "
            + "CONSTRAINT fk_drop_entities_parent FOREIGN KEY (ParentId) REFERENCES drop_parents (Id)); "
            + "INSERT INTO drop_parents (Id) VALUES (1); "
            + "INSERT INTO drop_entities (Id, Code, ParentId) VALUES (11, 'existing', 1);");
        await using var context = new SqliteConstraintDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropUniqueConstraintIfExists("uq_drop_entities_code", "drop_entities");
        builder.DropCheckConstraintIfExists("ck_drop_entities_code", "drop_entities");
        builder.DropForeignKeyIfExists("fk_drop_entities_parent", "drop_entities");
        var runner = context.GetService<ISafeMigrationRunner>();

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-constraint-drops-replay"));

        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_index_list('drop_entities') WHERE origin = 'u';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('drop_entities');"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM drop_entities WHERE Id = 11 AND Code = 'existing' AND ParentId = 1;"));
    }

    [Fact]
    public async Task ColumnCheckDrop_UsesTheLastConstraintNameDuringPreflight()
    {
        await using var connection = await OpenConnectionAsync();
        await CreateColumnCheckDropTableAsync(connection);
        await using var context = new SqliteConstraintDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropCheckConstraintIfExists("ck_drop_entities_code", "drop_entities");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-column-check-name"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
    }

    [Fact]
    public async Task ColumnCheckDrop_RemovesTheLatestNamedCheckWithoutLosingRows()
    {
        await using var connection = await OpenConnectionAsync();
        await CreateColumnCheckDropTableAsync(connection);
        await using var context = new SqliteConstraintDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropCheckConstraintIfExists("ck_drop_entities_code", "drop_entities");

        await ExecuteOperationsAsync(context, builder.Operations);

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var table = Assert.IsType<SqliteTableSnapshot>(snapshot.Tables["drop_entities"]);

        Assert.Empty(table.Checks);
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM drop_entities;"));
    }

    [Fact]
    public async Task PrimaryKeyDrop_RebuildsToKeylessTargetWithoutLosingRows()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE primary_key_drop_entities (Id INTEGER NOT NULL, Value TEXT NOT NULL, "
            + "CONSTRAINT pk_primary_key_drop_entities PRIMARY KEY (Id)); "
            + "INSERT INTO primary_key_drop_entities (Id, Value) VALUES (1, 'existing');");
        await using var context = new SqlitePrimaryKeyDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropPrimaryKeyIfExists("pk_primary_key_drop_entities", "primary_key_drop_entities");

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-primary-key-drop-replay"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('primary_key_drop_entities') WHERE pk > 0;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM primary_key_drop_entities WHERE Id = 1 AND Value = 'existing';"));
    }

    [Fact]
    public async Task ReferencedTableRebuild_PreservesEveryIncomingForeignKeyActionAndRestoresEnforcement()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id), "
            + "CONSTRAINT fk_constraint_children_parent FOREIGN KEY (ParentId) "
            + "REFERENCES constraint_parents (Id) ON DELETE CASCADE); "
            + "CREATE TABLE set_null_children (Id INTEGER PRIMARY KEY, ParentId INTEGER NULL "
            + "REFERENCES constraint_parents (Id) ON DELETE SET NULL); "
            + "CREATE TABLE set_default_children (Id INTEGER PRIMARY KEY, ParentId INTEGER DEFAULT 1 "
            + "REFERENCES constraint_parents (Id) ON DELETE SET DEFAULT); "
            + "CREATE TABLE restrict_children (Id INTEGER PRIMARY KEY, ParentId INTEGER NULL "
            + "REFERENCES constraint_parents (Id) ON DELETE RESTRICT); "
            + "INSERT INTO constraint_parents (Id) VALUES (1); "
            + "INSERT INTO constraint_children (Id, ParentId) VALUES (11, 1); "
            + "INSERT INTO set_null_children (Id, ParentId) VALUES (12, 1); "
            + "INSERT INTO set_default_children (Id, ParentId) VALUES (13, 1); "
            + "INSERT INTO restrict_children (Id, ParentId) VALUES (14, 1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_constraint_parents_positive",
            "constraint_parents",
            "Id > 0");

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_parents WHERE Id = 1;"));
        Assert.Equal(
            4,
            await ScalarIntAsync(
                connection,
                "SELECT (SELECT COUNT(*) FROM constraint_children WHERE ParentId = 1) "
                + "+ (SELECT COUNT(*) FROM set_null_children WHERE ParentId = 1) "
                + "+ (SELECT COUNT(*) FROM set_default_children WHERE ParentId = 1) "
                + "+ (SELECT COUNT(*) FROM restrict_children WHERE ParentId = 1);"));
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task SelfReferencingTableRebuild_PreservesHierarchyRows()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE self_reference_entities (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_self_reference_entities PRIMARY KEY (Id), "
            + "CONSTRAINT fk_self_reference_entities_parent FOREIGN KEY (ParentId) "
            + "REFERENCES self_reference_entities (Id) ON DELETE CASCADE); "
            + "CREATE INDEX ix_self_reference_entities_parent_id "
            + "ON self_reference_entities (ParentId); "
            + "INSERT INTO self_reference_entities (Id, ParentId) VALUES (1, NULL), (2, 1), (3, 2);");
        await using var context = new SqliteSelfReferenceRebuildTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_self_reference_entities_positive",
            "self_reference_entities",
            "Id > 0");

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(3, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM self_reference_entities;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM self_reference_entities WHERE Id = 3 AND ParentId = 2;"));
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task RebuildWithForeignKeyEnforcementDisabled_PreservesOriginalConnectionMode()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "INSERT INTO constraint_parents (Id) VALUES (1); "
            + "PRAGMA foreign_keys = OFF;");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_constraint_parents_positive",
            "constraint_parents",
            "Id > 0");

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(0, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_parents WHERE Id = 1;"));
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task RebuildWithForeignKeysDisabled_DoesNotValidatePreExistingOrphans()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE unrelated_parents (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE unrelated_children (Id INTEGER NOT NULL PRIMARY KEY, ParentId INTEGER NULL, "
            + "FOREIGN KEY (ParentId) REFERENCES unrelated_parents (Id)); "
            + "PRAGMA foreign_keys = OFF; "
            + "INSERT INTO unrelated_children (Id, ParentId) VALUES (1, 999); "
            + "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "INSERT INTO constraint_parents (Id) VALUES (1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_constraint_parents_positive",
            "constraint_parents",
            "Id > 0");

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(0, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM unrelated_children;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_parents;"));
    }

    [Fact]
    public async Task RebuildWithForeignKeysEnabled_ValidatesOnlyAffectedRelationshipClosure()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE unrelated_parents (Id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE unrelated_children (Id INTEGER NOT NULL PRIMARY KEY, ParentId INTEGER NULL, "
            + "FOREIGN KEY (ParentId) REFERENCES unrelated_parents (Id)); "
            + "PRAGMA foreign_keys = OFF; "
            + "INSERT INTO unrelated_children (Id, ParentId) VALUES (1, 999); "
            + "PRAGMA foreign_keys = ON; "
            + "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "INSERT INTO constraint_parents (Id) VALUES (1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_constraint_parents_positive",
            "constraint_parents",
            "Id > 0");

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM unrelated_children;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' "
                + "AND name = 'constraint_parents' AND sql LIKE '%ck_constraint_parents_positive%';"));
    }

    [Fact]
    public async Task RebuildWithExistingForeignKeyViolation_RollsBackAndRestoresEnforcement()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, ParentId INTEGER NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id), "
            + "CONSTRAINT fk_constraint_children_parent FOREIGN KEY (ParentId) "
            + "REFERENCES constraint_parents (Id) ON DELETE CASCADE); "
            + "PRAGMA foreign_keys = OFF; "
            + "INSERT INTO constraint_children (Id, ParentId) VALUES (11, 999); "
            + "PRAGMA foreign_keys = ON;");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_constraint_parents_positive",
            "constraint_parents",
            "Id > 0");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        Assert.Contains("foreign-key validation failed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarIntAsync(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_children;"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' "
                + "AND name = 'constraint_parents' AND sql LIKE '%ck_constraint_parents_positive%';"));
    }

    private static Task CreateColumnCheckDropTableAsync(
        DbConnection connection
    ) => ExecuteSqlAsync(
        connection,
        "CREATE TABLE drop_entities (Id INTEGER NOT NULL, "
        + "Code TEXT CONSTRAINT nn_drop_entities_code NOT NULL "
        + "CONSTRAINT ck_drop_entities_code CHECK (length(Code) > 0), "
        + "ParentId INTEGER NULL, CONSTRAINT pk_drop_entities PRIMARY KEY (Id)); "
        + "INSERT INTO drop_entities (Id, Code, ParentId) VALUES (11, 'existing', NULL);");
}
