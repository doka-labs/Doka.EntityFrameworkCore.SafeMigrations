namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises constraint matrix with independently owned SQLite state.</summary>
public sealed class SqliteSafeMigrationConstraintMatrixIntegrationTests : SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>
    /// Constraint with invalid existing rows is data blocked before rebuild.
    /// </summary>
    /// <param name="operationKind">The operation kind defining this isolated fixture case.</param>
    /// <param name="expectedAnalysisCode">The exact expected analysis code.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsurePrimaryKey, "primary_key_invalid_values")]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint, "unique_constraint_duplicate_values")]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint, "check_constraint_violated")]
    public async Task ConstraintWithInvalidExistingRows_IsDataBlockedBeforeRebuild(
        SafeMigrationOperationKind operationKind,
        string expectedAnalysisCode
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE constraint_data (id INTEGER NULL, code TEXT NULL, quantity
            INTEGER NOT NULL); INSERT INTO constraint_data (id,
            code, quantity) VALUES (NULL, 'duplicate', -1), (1, 'duplicate', 1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        // WHY: This fixture accepts only its declared constraint families; every other operation must reject.
        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
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

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-constraint-data-block"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM constraint_data;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(expectedAnalysisCode,
            Assert.Single(report.Assessments)
                .AnalysisCode);

        Assert.Equal(
            2,
            observedScalar1);
    }

    /// <summary>
    /// Foreign key and supporting index apply replay and preserve rows.
    /// </summary>
    [Fact]
    public async Task ForeignKeyAndSupportingIndex_ApplyReplayAndPreserveRows()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents
            PRIMARY KEY (Id)); CREATE TABLE constraint_children (Id INTEGER NOT NULL,
            ParentId INTEGER NULL, CONSTRAINT pk_constraint_children PRIMARY KEY (Id));
            INSERT INTO constraint_parents (Id) VALUES (1); INSERT INTO constraint_children
            (Id,
            ParentId) VALUES (11, 1);
            """,
            cancellationToken: CancellationToken.None);

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

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-foreign-key-apply"),
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-foreign-key-replay"),
            cancellationToken: CancellationToken.None);

        var foreignKeyCount = await ScalarIntAsync(
            connection,
            """
            SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children')
            WHERE "table" = 'constraint_parents'
            AND "from" = 'ParentId' AND "to" = 'Id'
            AND on_delete = 'CASCADE';
            """,
            cancellationToken: CancellationToken.None);

        var preservedRowCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM constraint_children WHERE Id = 11 AND ParentId = 1;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        AssertConstraintAssessmentSequence(preflight.Assessments,
            [
                (SafeMigrationOperationKind.EnsureIndex, "ix_constraint_children_parent_id"),
                (SafeMigrationOperationKind.EnsureForeignKey, "fk_constraint_children_parent"),
            ],
            SafeMigrationAction.Apply);

        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        AssertConstraintAssessmentSequence(replay.Assessments,
            [
                (SafeMigrationOperationKind.EnsureIndex, "ix_constraint_children_parent_id"),
                (SafeMigrationOperationKind.EnsureForeignKey, "fk_constraint_children_parent"),
            ],
            SafeMigrationAction.NoOp);

        Assert.Equal(1, foreignKeyCount);
        Assert.Equal(1, preservedRowCount);
    }

    /// <summary>
    /// Foreign key with orphaned rows is data blocked before table rebuild.
    /// </summary>
    [Fact]
    public async Task ForeignKeyWithOrphanedRows_IsDataBlockedBeforeTableRebuild()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents
            PRIMARY KEY (Id)); CREATE TABLE constraint_children (Id INTEGER NOT NULL,
            ParentId INTEGER NULL, CONSTRAINT pk_constraint_children PRIMARY KEY (Id));
            INSERT INTO constraint_children (Id,
            ParentId) VALUES (11, 999);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent",
            "constraint_children",
            ["ParentId"],
            "constraint_parents",
            ["Id"],
            onDelete: ReferentialAction.Cascade);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-foreign-key-orphan"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children');",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal("foreign_key_orphans", assessment.AnalysisCode);
        Assert.Equal(
            0,
            observedScalar1);
    }

    /// <summary>
    /// Rebuild with unmanaged foreign key action drift is rejected without mutation.
    /// </summary>
    [Fact]
    public async Task RebuildWithUnmanagedForeignKeyActionDrift_IsRejectedWithoutMutation()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents
            PRIMARY KEY (Id)); CREATE TABLE constraint_children (Id INTEGER NOT NULL,
            ParentId INTEGER NULL, Legacy TEXT NULL, CONSTRAINT pk_constraint_children
            PRIMARY KEY (Id),
            CONSTRAINT fk_constraint_children_parent FOREIGN KEY (ParentId) REFERENCES
            constraint_parents (Id) ON DELETE NO ACTION); CREATE INDEX ix_constraint_children_parent_id
            ON constraint_children (ParentId); INSERT INTO constraint_parents (Id) VALUES
            (1); INSERT INTO constraint_children (Id,
            ParentId, Legacy) VALUES (11, 1, 'preserve');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("Legacy", "constraint_children");

        // Act
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None));

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children') WHERE on_delete = 'NO ACTION';",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM constraint_children "
            + "WHERE Id = 11 AND ParentId = 1 AND Legacy = 'preserve';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Contains("table_rebuild_unmanaged_foreign_key",
            Assert.IsType<InvalidOperationException>(failure)
                .Message,
            StringComparison.Ordinal);

        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);
    }

    /// <summary>
    /// Unique check and foreign key drops rebuild once and replay as no ops.
    /// </summary>
    [Fact]
    public async Task UniqueCheckAndForeignKeyDrops_RebuildOnceAndReplayAsNoOps()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE drop_parents (Id INTEGER NOT NULL, CONSTRAINT pk_drop_parents
            PRIMARY KEY (Id)); CREATE TABLE drop_entities (Id INTEGER NOT NULL,
            Code TEXT NOT NULL, ParentId INTEGER NULL, CONSTRAINT pk_drop_entities PRIMARY
            KEY (Id),
            CONSTRAINT uq_drop_entities_code UNIQUE (Code), CONSTRAINT ck_drop_entities_code
            CHECK (length(Code) > 0),
            CONSTRAINT fk_drop_entities_parent FOREIGN KEY (ParentId) REFERENCES drop_parents
            (Id)); INSERT INTO drop_parents (Id) VALUES (1); INSERT INTO drop_entities
            (Id,
            Code, ParentId) VALUES (11, 'existing', 1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteConstraintDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropUniqueConstraintIfExists("uq_drop_entities_code", "drop_entities");
        builder.DropCheckConstraintIfExists("ck_drop_entities_code", "drop_entities");
        builder.DropForeignKeyIfExists("fk_drop_entities_parent", "drop_entities");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-constraint-drops-replay"),
            cancellationToken: CancellationToken.None);

        var uniqueConstraintCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_index_list('drop_entities') WHERE origin = 'u';",
            cancellationToken: CancellationToken.None);

        var foreignKeyCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_list('drop_entities');",
            cancellationToken: CancellationToken.None);

        var preservedRowCount = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM drop_entities WHERE Id = 11 AND Code = 'existing' AND ParentId = 1;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        AssertConstraintAssessmentSequence(replay.Assessments,
            [
                (SafeMigrationOperationKind.DropUniqueConstraint, "uq_drop_entities_code"),
                (SafeMigrationOperationKind.DropCheckConstraint, "ck_drop_entities_code"),
                (SafeMigrationOperationKind.DropForeignKey, "fk_drop_entities_parent"),
            ],
            SafeMigrationAction.NoOp);

        Assert.Equal(0, uniqueConstraintCount);
        Assert.Equal(0, foreignKeyCount);
        Assert.Equal(1, preservedRowCount);
    }

    /// <summary>Requires every declared operation, in order, rather than allowing vacuous All assertions.</summary>
    private static void AssertConstraintAssessmentSequence(
        IReadOnlyList<SafeMigrationAssessment> assessments,
        (SafeMigrationOperationKind Kind, string Name)[] expected,
        SafeMigrationAction action
    )
    {
        Assert.Equal(expected.Length, assessments.Count);

        // WHY: Expected and actual rows are paired by ordinal; enumeration alone cannot verify that identity.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < expected.Length; index++)
        {
            var assessment = assessments[index];
            Assert.Equal(index, assessment.Ordinal);
            Assert.True(assessment.IsSafeOperation);
            Assert.Equal(expected[index].Kind, assessment.OperationKind);
            Assert.Equal(expected[index].Name, assessment.ObjectName);
            Assert.Equal(action, assessment.Action);
        }
    }

    /// <summary>
    /// Column check drop uses the last constraint name during preflight.
    /// </summary>
    [Fact]
    public async Task ColumnCheckDrop_UsesTheLastConstraintNameDuringPreflight()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await CreateColumnCheckDropTableAsync(connection, cancellationToken: CancellationToken.None);
        await using var context = new SqliteConstraintDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropCheckConstraintIfExists("ck_drop_entities_code", "drop_entities");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-column-check-name"),
                cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply,
            Assert.Single(report.Assessments)
                .Action);
    }

    /// <summary>
    /// Column check drop removes the latest named check without losing rows.
    /// </summary>
    [Fact]
    public async Task ColumnCheckDrop_RemovesTheLatestNamedCheckWithoutLosingRows()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await CreateColumnCheckDropTableAsync(connection, cancellationToken: CancellationToken.None);
        await using var context = new SqliteConstraintDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropCheckConstraintIfExists("ck_drop_entities_code", "drop_entities");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM drop_entities;",
            cancellationToken: CancellationToken.None);

        // Assert
        var table = Assert.IsType<SqliteTableSnapshot>(snapshot.Tables["drop_entities"]);

        Assert.Empty(table.Checks);
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Primary key drop rebuilds to keyless target without losing rows.
    /// </summary>
    [Fact]
    public async Task PrimaryKeyDrop_RebuildsToKeylessTargetWithoutLosingRows()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE primary_key_drop_entities (Id INTEGER NOT NULL, Value TEXT
            NOT NULL,
            CONSTRAINT pk_primary_key_drop_entities PRIMARY KEY (Id)); INSERT INTO primary_key_drop_entities
            (Id,
            Value) VALUES (1, 'existing');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqlitePrimaryKeyDropTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropPrimaryKeyIfExists("pk_primary_key_drop_entities", "primary_key_drop_entities");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-primary-key-drop-replay"),
                cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('primary_key_drop_entities') WHERE pk > 0;",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM primary_key_drop_entities WHERE Id = 1 AND Value = 'existing';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        Assert.Equal(
            0,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);
    }

    /// <summary>
    /// Referenced table rebuild preserves every incoming foreign key action and restores enforcement.
    /// </summary>
    [Fact]
    public async Task ReferencedTableRebuild_PreservesEveryIncomingForeignKeyActionAndRestoresEnforcement()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents
            PRIMARY KEY (Id)); CREATE TABLE constraint_children (Id INTEGER NOT NULL,
            ParentId INTEGER NULL, CONSTRAINT pk_constraint_children PRIMARY KEY (Id),
            CONSTRAINT fk_constraint_children_parent FOREIGN KEY (ParentId) REFERENCES
            constraint_parents (Id) ON DELETE CASCADE); CREATE TABLE set_null_children
            (Id INTEGER PRIMARY KEY,
            ParentId INTEGER NULL REFERENCES constraint_parents (Id) ON DELETE SET NULL);
            CREATE TABLE set_default_children (Id INTEGER PRIMARY KEY,
            ParentId INTEGER DEFAULT 1 REFERENCES constraint_parents (Id) ON DELETE
            SET DEFAULT); CREATE TABLE restrict_children (Id INTEGER PRIMARY KEY,
            ParentId INTEGER NULL REFERENCES constraint_parents (Id) ON DELETE RESTRICT);
            INSERT INTO constraint_parents (Id) VALUES (1); INSERT INTO constraint_children
            (Id,
            ParentId) VALUES (11, 1); INSERT INTO set_null_children (Id, ParentId) VALUES
            (12,
            1); INSERT INTO set_default_children (Id, ParentId) VALUES (13, 1); INSERT
            INTO restrict_children (Id,
            ParentId) VALUES (14, 1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_constraint_parents_positive",
            "constraint_parents",
            "Id > 0");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "PRAGMA foreign_keys;",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM constraint_parents WHERE Id = 1;",
            cancellationToken: CancellationToken.None);

        var observedScalar3 = await ScalarIntAsync(
            connection,
            """
            SELECT (SELECT COUNT(*) FROM constraint_children
            WHERE ParentId = 1) + (SELECT COUNT(*)
            FROM set_null_children WHERE ParentId = 1) + (SELECT COUNT(*)
            FROM set_default_children WHERE ParentId = 1) + (SELECT COUNT(*)
            FROM restrict_children WHERE ParentId = 1);
            """,
            cancellationToken: CancellationToken.None);

        var observedScalar4 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_check;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);

        Assert.Equal(
            4,
            observedScalar3);

        Assert.Equal(
            0,
            observedScalar4);
    }

    /// <summary>
    /// Self referencing table rebuild preserves hierarchy rows.
    /// </summary>
    [Fact]
    public async Task SelfReferencingTableRebuild_PreservesHierarchyRows()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE self_reference_entities (Id INTEGER NOT NULL, ParentId INTEGER
            NULL,
            CONSTRAINT pk_self_reference_entities PRIMARY KEY (Id), CONSTRAINT fk_self_reference_entities_parent
            FOREIGN KEY (ParentId) REFERENCES self_reference_entities (Id) ON DELETE
            CASCADE); CREATE INDEX ix_self_reference_entities_parent_id ON self_reference_entities
            (ParentId); INSERT INTO self_reference_entities (Id,
            ParentId) VALUES (1, NULL), (2, 1), (3, 2);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteSelfReferenceRebuildTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_self_reference_entities_positive",
            "self_reference_entities",
            "Id > 0");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM self_reference_entities;",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM self_reference_entities WHERE Id = 3 AND ParentId = 2;",
            cancellationToken: CancellationToken.None);

        var observedScalar3 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_check;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            3,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);

        Assert.Equal(
            0,
            observedScalar3);
    }

    /// <summary>
    /// Rebuild with foreign key enforcement disabled preserves original connection mode.
    /// </summary>
    [Fact]
    public async Task RebuildWithForeignKeyEnforcementDisabled_PreservesOriginalConnectionMode()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE constraint_parents (
                Id INTEGER NOT NULL,
                CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)
            );
            INSERT INTO constraint_parents (Id) VALUES (1);
            PRAGMA foreign_keys = OFF;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_constraint_parents_positive",
            "constraint_parents",
            "Id > 0");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "PRAGMA foreign_keys;",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM constraint_parents WHERE Id = 1;",
            cancellationToken: CancellationToken.None);

        var observedScalar3 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_foreign_key_check;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            0,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);

        Assert.Equal(
            0,
            observedScalar3);
    }

    /// <summary>
    /// Rebuild with foreign keys disabled does not validate pre existing orphans.
    /// </summary>
    [Fact]
    public async Task RebuildWithForeignKeysDisabled_DoesNotValidatePreExistingOrphans()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE unrelated_parents (Id INTEGER NOT NULL PRIMARY KEY); CREATE
            TABLE unrelated_children (Id INTEGER NOT NULL PRIMARY KEY,
            ParentId INTEGER NULL, FOREIGN KEY (ParentId) REFERENCES unrelated_parents
            (Id)); PRAGMA foreign_keys = OFF; INSERT INTO unrelated_children (Id,
            ParentId) VALUES (1, 999); CREATE TABLE constraint_parents (Id INTEGER NOT
            NULL,
            CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); INSERT INTO constraint_parents
            (Id) VALUES (1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_constraint_parents_positive",
            "constraint_parents",
            "Id > 0");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "PRAGMA foreign_keys;",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM unrelated_children;",
            cancellationToken: CancellationToken.None);

        var observedScalar3 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM constraint_parents;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            0,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);

        Assert.Equal(
            1,
            observedScalar3);
    }

    /// <summary>
    /// Rebuild with foreign keys enabled validates only affected relationship closure.
    /// </summary>
    [Fact]
    public async Task RebuildWithForeignKeysEnabled_ValidatesOnlyAffectedRelationshipClosure()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE unrelated_parents (Id INTEGER NOT NULL PRIMARY KEY); CREATE
            TABLE unrelated_children (Id INTEGER NOT NULL PRIMARY KEY,
            ParentId INTEGER NULL, FOREIGN KEY (ParentId) REFERENCES unrelated_parents
            (Id)); PRAGMA foreign_keys = OFF; INSERT INTO unrelated_children (Id,
            ParentId) VALUES (1, 999); PRAGMA foreign_keys = ON; CREATE TABLE constraint_parents
            (Id INTEGER NOT NULL,
            CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); INSERT INTO constraint_parents
            (Id) VALUES (1);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_constraint_parents_positive",
            "constraint_parents",
            "Id > 0");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "PRAGMA foreign_keys;",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM unrelated_children;",
            cancellationToken: CancellationToken.None);

        var observedScalar3 = await ScalarIntAsync(
            connection,
            """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'table' AND name = 'constraint_parents'
            AND sql LIKE '%ck_constraint_parents_positive%';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);

        Assert.Equal(
            1,
            observedScalar3);
    }

    /// <summary>
    /// Rebuild with existing foreign key violation rolls back and restores enforcement.
    /// </summary>
    [Fact]
    public async Task RebuildWithExistingForeignKeyViolation_RollsBackAndRestoresEnforcement()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents
            PRIMARY KEY (Id)); CREATE TABLE constraint_children (Id INTEGER NOT NULL,
            ParentId INTEGER NULL, CONSTRAINT pk_constraint_children PRIMARY KEY (Id),
            CONSTRAINT fk_constraint_children_parent FOREIGN KEY (ParentId) REFERENCES
            constraint_parents (Id) ON DELETE CASCADE); PRAGMA foreign_keys = OFF; INSERT
            INTO constraint_children (Id,
            ParentId) VALUES (11, 999); PRAGMA foreign_keys = ON;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_constraint_parents_positive",
            "constraint_parents",
            "Id > 0");

        // Act
        var failure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None));

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "PRAGMA foreign_keys;",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM constraint_children;",
            cancellationToken: CancellationToken.None);

        var observedScalar3 = await ScalarIntAsync(
            connection,
            """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'table' AND name = 'constraint_parents'
            AND sql LIKE '%ck_constraint_parents_positive%';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Contains("foreign-key validation failed",
            Assert.IsType<InvalidOperationException>(failure)
                .Message,
            StringComparison.Ordinal);

        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);

        Assert.Equal(
            0,
            observedScalar3);
    }

    private static Task CreateColumnCheckDropTableAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    ) => ExecuteSqlAsync(
        connection,
        """
        CREATE TABLE drop_entities (Id INTEGER NOT NULL, Code TEXT CONSTRAINT nn_drop_entities_code
        NOT NULL CONSTRAINT ck_drop_entities_code CHECK (length(Code) > 0),
        ParentId INTEGER NULL, CONSTRAINT pk_drop_entities PRIMARY KEY (Id)); INSERT
        INTO drop_entities (Id,
        Code, ParentId) VALUES (11, 'existing', NULL);
        """,
        cancellationToken: cancellationToken);

    /// <summary>Checks assertion strength without adding a catalog feature leaf or inheriting its tests.</summary>
    public sealed class AssessmentEvidenceTests
    {
        /// <summary>
        /// The same exact assertion used by native tests rejects absent, incomplete or mismatched evidence.
        /// </summary>
        /// <param name="operationCount">The real two-operation ensure or three-operation drop contract.</param>
        /// <param name="mutation">The evidence omission or identity/action mutation applied to the control.</param>
        [Theory]
        [InlineData(2, 0)]
        [InlineData(2, 1)]
        [InlineData(2, 2)]
        [InlineData(2, 3)]
        [InlineData(2, 4)]
        [InlineData(2, 5)]
        [InlineData(3, 0)]
        [InlineData(3, 1)]
        [InlineData(3, 2)]
        [InlineData(3, 3)]
        [InlineData(3, 4)]
        [InlineData(3, 5)]
        public void ConstraintAssessmentAssertionRejectsIncompleteOrDifferentEvidence(
            int operationCount,
            int mutation
        )
        {
            // Arrange
            (SafeMigrationOperationKind Kind, string Name)[] expected = operationCount == 2
                ?
                [
                    (SafeMigrationOperationKind.EnsureIndex, "index"),
                    (SafeMigrationOperationKind.EnsureForeignKey, "foreign_key"),
                ]
                :
                [
                    (SafeMigrationOperationKind.DropUniqueConstraint, "unique"),
                    (SafeMigrationOperationKind.DropCheckConstraint, "check"),
                    (SafeMigrationOperationKind.DropForeignKey, "foreign_key"),
                ];

            var action = operationCount == 2
                ? SafeMigrationAction.Apply
                : SafeMigrationAction.NoOp;

            var assessments = expected
                .Select((item, ordinal) => new SafeMigrationAssessment(
                    mutation == 2 && ordinal == operationCount - 1
                        ? 0
                        : ordinal,
                    typeof(SafeMigrationOperation).FullName!,
                    true,
                    mutation == 3 && ordinal == operationCount - 1
                        ? SafeMigrationOperationKind.EnsureTable
                        : item.Kind,
                    mutation == 4 && ordinal == operationCount - 1
                        ? "wrong"
                        : item.Name,
                    SafeMigrationObservedState.Missing,
                    mutation == 5 && ordinal == operationCount - 1
                        ? SafeMigrationAction.Repair
                        : action,
                    false,
                    "missing"))
                .ToArray();

            var actual = mutation switch { 0 => [], 1 => assessments[..^1], _ => assessments };

            // Act
            var failure = Record.Exception(() => AssertConstraintAssessmentSequence(actual, expected, action));

            // Assert
            Assert.IsType<XunitException>(failure, exactMatch: false);
        }
    }
}
