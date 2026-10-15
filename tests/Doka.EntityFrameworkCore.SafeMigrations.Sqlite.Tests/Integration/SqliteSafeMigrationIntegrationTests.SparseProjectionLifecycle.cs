namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises sparse projection lifecycle with independently owned SQLite state.</summary>
public sealed class SqliteSafeMigrationSparseProjectionLifecycleIntegrationTests
    : SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>A newly accepted index is dropped once, without reviving immutable live state on replay.</summary>
    /// <param name="createTable">Whether the owner is new instead of an incompletely projected live table.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexEnsureThenRepeatedDrop_LeavesNoPhysicalIndex(bool createTable)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        if (!createTable)
        {
            await ExecuteSqlAsync(connection,
                "CREATE TABLE projection_index (Id INTEGER NOT NULL, Code TEXT NULL); "
                + "INSERT INTO projection_index (Id, Code) VALUES (1, 'preserve');");
        }

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (createTable)
        {
            builder.CreateTableIfNotExists("projection_index", table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                Code = table.Column<string>(type: "TEXT", nullable: true),
            });
        }

        builder.CreateIndexIfNotExists("ix_projection_code", "projection_index", ["Code"]);
        builder.DropIndexIfExists("ix_projection_code", "projection_index");
        builder.DropIndexIfExists("ix_projection_code", "projection_index");
        var runner = context.GetService<ISafeMigrationRunner>();
        var indexOrdinal = createTable ? 1 : 0;

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-index-sparse-lifecycle"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-index-sparse-postflight"));

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var remainingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM projection_index WHERE Id = 1 AND Code = 'preserve';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[indexOrdinal].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[indexOrdinal + 1].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[indexOrdinal + 2].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.DoesNotContain(snapshot.Tables["projection_index"].Indexes,
            index => index.Name == "ix_projection_code");
        Assert.Equal(createTable ? 0 : 1, remainingRows);
    }

    /// <summary>An accepted live-index drop cannot be executed again from stale matching evidence.</summary>
    [Fact]
    public async Task ExistingIndexRepeatedDrop_ProjectsMissingForTheSecondDrop()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE projection_index (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "CREATE INDEX ix_projection_code ON projection_index (Code); "
            + "INSERT INTO projection_index (Id, Code) VALUES (1, 'preserve');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_projection_code", "projection_index");
        builder.DropIndexIfExists("ix_projection_code", "projection_index");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-live-index-repeated-drop"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var remainingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM projection_index WHERE Id = 1 AND Code = 'preserve';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Empty(snapshot.Tables["projection_index"].Indexes);
        Assert.Equal(1, remainingRows);
    }

    /// <summary>
    /// A standalone accepted check is retained even before its live owner has any prerequisite entries.
    /// </summary>
    [Fact]
    public async Task StandaloneCheckRepeatedEnsure_AddsOnePhysicalConstraintAndReplays()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "INSERT INTO constraint_parents (Id) VALUES (1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_constraint_parents_positive", "constraint_parents", "Id > 0");
        builder.AddCheckConstraintIfNotExists("ck_constraint_parents_positive", "constraint_parents", "Id > 0");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-standalone-check-evidence"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-standalone-check-postflight"));

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var remainingRows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_parents WHERE Id = 1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal("ck_constraint_parents_positive",
            Assert.Single(snapshot.Tables["constraint_parents"].Checks).Name);
        Assert.Equal(1, remainingRows);
    }

    /// <summary>
    /// Same-name incompatible checks must be rejected before an immutable snapshot can authorize both.
    /// </summary>
    [Fact]
    public async Task StandaloneCheckConflictingEnsure_RejectsBeforeAnyConstraintIsApplied()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "INSERT INTO constraint_parents (Id) VALUES (1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_constraint_parents_positive", "constraint_parents", "Id > 0");
        builder.AddCheckConstraintIfNotExists("ck_constraint_parents_positive", "constraint_parents", "Id < 10");
        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-standalone-check-conflict"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var remainingRows = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_parents WHERE Id = 1;");
        var finalSchemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Contains("different", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Empty(snapshot.Tables["constraint_parents"].Checks);
        Assert.Equal(1, remainingRows);
        Assert.Equal(schemaVersion, finalSchemaVersion);
    }

    /// <summary>A matching source column does not prove that its untracked live rename destination is free.</summary>
    [Fact]
    public async Task EnsuredColumnRenameToOccupiedLiveTarget_RejectsWithoutOverwritingEitherColumn()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE projection_rename (Id INTEGER NOT NULL, Source TEXT NULL, Target TEXT NULL); "
            + "INSERT INTO projection_rename (Id, Source, Target) VALUES (1, 'source', 'target');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>("Source", "projection_rename", type: "TEXT", nullable: true);
        builder.RenameColumnIfExists("Source", "projection_rename", "Target");
        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-column-rename-live-collision"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var remainingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM projection_rename WHERE Id = 1 AND Source = 'source' AND Target = 'target';");

        var finalSchemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Contains("different", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(1, remainingRows);
        Assert.Equal(schemaVersion, finalSchemaVersion);
    }

    /// <summary>A source tombstone takes precedence over a dropped destination's former live collision.</summary>
    [Fact]
    public async Task DroppedSourceAndTargetThenRename_RemainsNoOpAndPreservesUnrelatedRows()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE structural_posts (Id INTEGER NOT NULL, AuthorId INTEGER NULL, Title TEXT NOT NULL, "
            + "Source TEXT NULL, Target TEXT NULL, CONSTRAINT pk_structural_posts PRIMARY KEY (Id)); "
            + "INSERT INTO structural_posts (Id, AuthorId, Title, Source, Target) "
            + "VALUES (1, NULL, 'preserve', 'source', 'target');");
        await using var context = new SqliteStructuralSequenceTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("Source", "structural_posts");
        builder.DropColumnIfExists("Target", "structural_posts");
        builder.RenameColumnIfExists("Source", "structural_posts", "Target");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-column-rename-both-dropped"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var remainingRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM structural_posts WHERE Id = 1 AND AuthorId IS NULL AND Title = 'preserve';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[2].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[2].Action);
        Assert.DoesNotContain("Source", snapshot.Tables["structural_posts"].Columns.Keys);
        Assert.DoesNotContain("Target", snapshot.Tables["structural_posts"].Columns.Keys);
        Assert.Equal(1, remainingRows);
    }

    /// <summary>A newly created owner's complete shape can prove an absent rename destination.</summary>
    [Fact]
    public async Task CreatedTableColumnRenameToFreeTarget_AppliesAndPreservesTheFinalContract()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "projection_rename",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                Source = table.Column<string>(type: "TEXT", nullable: true),
            },
            mode: SafeMigrationTableMode.ConvergenceContainer,
            policy: SafeMigrationPolicy.ExistenceOnly);
        builder.RenameColumnIfExists("Source", "projection_rename", "Target");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-created-column-rename"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.DoesNotContain("Source", snapshot.Tables["projection_rename"].Columns.Keys);
        Assert.Contains("Target", snapshot.Tables["projection_rename"].Columns.Keys);
        Assert.Equal("TEXT", snapshot.Tables["projection_rename"].Columns["Target"].StoreType);
    }
}
