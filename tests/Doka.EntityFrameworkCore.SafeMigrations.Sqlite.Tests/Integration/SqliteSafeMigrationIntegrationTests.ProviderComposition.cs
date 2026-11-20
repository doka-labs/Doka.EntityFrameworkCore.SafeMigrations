namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises provider composition with independently owned SQLite state.</summary>
public sealed class SqliteSafeMigrationProviderCompositionIntegrationTests : SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>
    /// Contiguous provider operations are rewritten as one lossless transition.
    /// </summary>
    [Fact]
    public async Task ContiguousProviderOperations_AreRewrittenAsOneLosslessTransition()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE provider_rewrite_entities (Id INTEGER NOT NULL PRIMARY KEY,
            Legacy TEXT NULL, Replacement TEXT NULL); INSERT INTO provider_rewrite_entities
            (Id,
            Legacy, Replacement) VALUES (1, 'discard', 'preserve');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = new SqliteProviderRewriteTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumn("Legacy", "provider_rewrite_entities");
        builder.RenameColumn("Replacement", "provider_rewrite_entities", newName: "Legacy");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM provider_rewrite_entities "
            + "WHERE Id = 1 AND Legacy = 'preserve';",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('provider_rewrite_entities') "
            + "WHERE name = 'Replacement';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            0,
            observedScalar2);
    }

    /// <summary>
    /// Provider column followed by safe relationship operations applies and replays.
    /// </summary>
    [Fact]
    public async Task ProviderColumnFollowedBySafeRelationshipOperations_AppliesAndReplays()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE constraint_parents (Id INTEGER NOT NULL, CONSTRAINT pk_constraint_parents
            PRIMARY KEY (Id)); CREATE TABLE constraint_children (Id INTEGER NOT NULL,
            CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); INSERT INTO constraint_parents
            (Id) VALUES (1); INSERT INTO constraint_children (Id) VALUES (11);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumn<int>(
            "ParentId",
            "constraint_children",
            type: "INTEGER",
            nullable: true);

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
            new SafeMigrationRunOptions("sqlite-provider-safe-composition"),
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, cancellationToken: CancellationToken.None);

        var replay = new MigrationBuilder(context.Database.ProviderName!);
        replay.CreateIndexIfNotExists(
            "ix_constraint_children_parent_id",
            "constraint_children",
            ["ParentId"]);

        replay.AddForeignKeyIfNotExists(
            "fk_constraint_children_parent",
            "constraint_children",
            ["ParentId"],
            "constraint_parents",
            ["Id"],
            onDelete: ReferentialAction.Cascade);

        await ExecuteOperationsAsync(context, replay.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('constraint_children') WHERE name = 'ParentId';",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            """
            SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children')
            WHERE "table" = 'constraint_parents'
            AND "from" = 'ParentId';
            """,
            cancellationToken: CancellationToken.None);

        var observedScalar3 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM constraint_children WHERE Id = 11;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.True(
            preflight.Status == SafeMigrationReportStatus.ReadyWithProviderOperations,
            string.Join(
                Environment.NewLine,
                preflight
                    .Assessments
                    .Where(static assessment => assessment.Action?.RejectsExecution() == true)
                    .Select(static assessment =>
                        $"{assessment.Ordinal}: {assessment.OperationKind} {assessment.AnalysisCode} "
                        + assessment.DecisionCode)));

        Assert.Equal("provider_owned_not_analyzed", preflight.Assessments[0].Code);
        Assert.All(
            preflight.Assessments.Skip(1),
            assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));

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
    /// Multiline partial index does not match full index.
    /// </summary>
    [Fact]
    public async Task MultilinePartialIndex_DoesNotMatchFullIndex()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE partial_index_entities (Id INTEGER NOT NULL, Code TEXT NULL);
            CREATE UNIQUE INDEX ix_partial_index_entities_code ON partial_index_entities
            (Code)
            WHERE Code IS NOT NULL;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var ensureFull = new MigrationBuilder(context.Database.ProviderName!);
        ensureFull.CreateIndexIfNotExists(
            "ix_partial_index_entities_code",
            "partial_index_entities",
            ["Code"],
            unique: true);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                ensureFull.Operations,
                new SafeMigrationRunOptions("sqlite-partial-index-drift"),
                cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
    }

    /// <summary>
    /// Multiline partial index rename preserves filter.
    /// </summary>
    [Fact]
    public async Task MultilinePartialIndex_RenamePreservesFilter()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE partial_index_entities (Id INTEGER NOT NULL, Code TEXT NULL);
            CREATE UNIQUE INDEX ix_partial_index_entities_code ON partial_index_entities
            (Code)
            WHERE Code IS NOT NULL;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var rename = new MigrationBuilder(context.Database.ProviderName!);

        rename.RenameIndexIfExists(
            "ix_partial_index_entities_code",
            "partial_index_entities",
            "ix_partial_index_entities_code_v2");

        // Act
        await ExecuteOperationsAsync(context, rename.Operations, cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'index' AND name = 'ix_partial_index_entities_code_v2'
            AND lower(sql) LIKE '%where code is not null%';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            1,
            observedScalar1);
    }

    /// <summary>
    /// Check constraint string literals remain case sensitive.
    /// </summary>
    [Fact]
    public async Task CheckConstraintStringLiterals_RemainCaseSensitive()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            $"""
            CREATE TABLE literal_check_entities (Id INTEGER NOT NULL, Status TEXT NOT
            NULL,
            CONSTRAINT ck_literal_check_entities_status CHECK (Status = 'Active'));{"\u0020"}
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_literal_check_entities_status",
            "literal_check_entities",
            "Status = 'ACTIVE'");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-check-literal-case"),
                cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
    }

    /// <summary>
    /// Column comments and identifier text do not change parsed facets.
    /// </summary>
    [Fact]
    public async Task ColumnCommentsAndIdentifierText_DoNotChangeParsedFacets()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            """
            CREATE TABLE comment_entities (
            Id INTEGER NOT NULL PRIMARY KEY, -- owner's ) COLLATE RTRIM
            AutoIncrementSeed INTEGER NULL,
            Code TEXT COLLATE NOCASE NULL /* apostrophe's ) COLLATE BINARY */
            );
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>(
            "AutoIncrementSeed",
            "comment_entities",
            type: "INTEGER",
            nullable: true);

        builder.AddColumnIfNotExists<string>(
            "Code",
            "comment_entities",
            type: "TEXT",
            nullable: true,
            collation: new SafeMigrationCollationIdentifier("NOCASE"));

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-comment-parsing"),
                cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
    }
}
