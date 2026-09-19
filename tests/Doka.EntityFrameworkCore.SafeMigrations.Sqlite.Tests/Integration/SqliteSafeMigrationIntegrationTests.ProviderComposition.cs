namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    [Fact]
    public async Task ContiguousProviderOperations_AreRewrittenAsOneLosslessTransition()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE provider_rewrite_entities ("
            + "Id INTEGER NOT NULL PRIMARY KEY, Legacy TEXT NULL, Replacement TEXT NULL); "
            + "INSERT INTO provider_rewrite_entities (Id, Legacy, Replacement) "
            + "VALUES (1, 'discard', 'preserve');");
        await using var context = new SqliteProviderRewriteTestContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumn("Legacy", "provider_rewrite_entities");
        builder.RenameColumn("Replacement", "provider_rewrite_entities", newName: "Legacy");

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM provider_rewrite_entities "
                + "WHERE Id = 1 AND Legacy = 'preserve';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('provider_rewrite_entities') "
                + "WHERE name = 'Replacement';"));
    }

    [Fact]
    public async Task ProviderColumnFollowedBySafeRelationshipOperations_AppliesAndReplays()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE constraint_parents (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_parents PRIMARY KEY (Id)); "
            + "CREATE TABLE constraint_children (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_constraint_children PRIMARY KEY (Id)); "
            + "INSERT INTO constraint_parents (Id) VALUES (1); "
            + "INSERT INTO constraint_children (Id) VALUES (11);");
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

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-provider-safe-composition"));

        await ExecuteOperationsAsync(context, builder.Operations);

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
        await ExecuteOperationsAsync(context, replay.Operations);

        Assert.True(
            preflight.Status == SafeMigrationReportStatus.ReadyWithProviderOperations,
            string.Join(
                Environment.NewLine,
                preflight.Assessments
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
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('constraint_children') WHERE name = 'ParentId';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_foreign_key_list('constraint_children') "
                + "WHERE \"table\" = 'constraint_parents' AND \"from\" = 'ParentId';"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM constraint_children WHERE Id = 11;"));
    }

    [Fact]
    public async Task MultilinePartialIndex_DoesNotMatchFullIndex()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE partial_index_entities (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "CREATE UNIQUE INDEX ix_partial_index_entities_code "
            + "ON partial_index_entities (Code)\nWHERE Code IS NOT NULL;");
        await using var context = CreateContext(connection);
        var ensureFull = new MigrationBuilder(context.Database.ProviderName!);
        ensureFull.CreateIndexIfNotExists(
            "ix_partial_index_entities_code",
            "partial_index_entities",
            ["Code"],
            unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                ensureFull.Operations,
                new SafeMigrationRunOptions("sqlite-partial-index-drift"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
    }

    [Fact]
    public async Task MultilinePartialIndex_RenamePreservesFilter()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE partial_index_entities (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "CREATE UNIQUE INDEX ix_partial_index_entities_code "
            + "ON partial_index_entities (Code)\nWHERE Code IS NOT NULL;");
        await using var context = CreateContext(connection);
        var rename = new MigrationBuilder(context.Database.ProviderName!);

        rename.RenameIndexIfExists(
            "ix_partial_index_entities_code",
            "partial_index_entities",
            "ix_partial_index_entities_code_v2");

        await ExecuteOperationsAsync(context, rename.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' "
                + "AND name = 'ix_partial_index_entities_code_v2' "
                + "AND lower(sql) LIKE '%where code is not null%';"));
    }

    [Fact]
    public async Task CheckConstraintStringLiterals_RemainCaseSensitive()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE literal_check_entities (Id INTEGER NOT NULL, Status TEXT NOT NULL, "
            + "CONSTRAINT ck_literal_check_entities_status CHECK (Status = 'Active')); ");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "ck_literal_check_entities_status",
            "literal_check_entities",
            "Status = 'ACTIVE'");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-check-literal-case"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
    }

    [Fact]
    public async Task ColumnCommentsAndIdentifierText_DoNotChangeParsedFacets()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE comment_entities (\n"
            + "Id INTEGER NOT NULL PRIMARY KEY, -- owner's ) COLLATE RTRIM\n"
            + "AutoIncrementSeed INTEGER NULL,\n"
            + "Code TEXT COLLATE NOCASE NULL /* apostrophe's ) COLLATE BINARY */\n"
            + ");");
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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-comment-parsing"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
    }
}
