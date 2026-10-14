namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises index rename namespace with independently owned SQLite state.</summary>
public sealed class SqliteSafeMigrationIndexRenameNamespaceIntegrationTests : SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>A sparse accepted source cannot overwrite SQLite's schema-wide index-name namespace.</summary>
    /// <param name="createOccupant">The index, table or view occupying the destination name.</param>
    [Theory]
    [InlineData("CREATE INDEX rename_destination ON namespace_other (Code);")]
    [InlineData("CREATE TABLE rename_destination (Id INTEGER NOT NULL);")]
    [InlineData("CREATE VIEW rename_destination AS SELECT Id FROM namespace_other;")]
    public async Task EnsuredIndexRename_RejectsSchemaWideDestinationOccupancy(string createOccupant)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE namespace_source (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "CREATE INDEX rename_source ON namespace_source (Code); "
            + "CREATE TABLE namespace_other (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "INSERT INTO namespace_source (Id, Code) VALUES (1, 'source'); "
            + "INSERT INTO namespace_other (Id, Code) VALUES (2, 'other'); "
            + createOccupant);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("rename_source", "namespace_source", ["Code"]);
        builder.RenameIndexIfExists("rename_source", "namespace_source", "RENAME_DESTINATION");
        var runner = context.GetService<ISafeMigrationRunner>();
        var schemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-index-rename-global-collision"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var sourceRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM namespace_source WHERE Id = 1 AND Code = 'source';");

        var otherRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM namespace_other WHERE Id = 2 AND Code = 'other';");

        var finalSchemaVersion = await ScalarIntAsync(connection, "PRAGMA schema_version;");
        var targetObjects = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'rename_destination';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Contains("different", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal("rename_source", Assert.Single(snapshot.Tables["namespace_source"].Indexes).Name);
        Assert.Equal(1, sourceRows);
        Assert.Equal(1, otherRows);
        Assert.Equal(1, targetObjects);
        Assert.Equal(schemaVersion, finalSchemaVersion);
    }

    /// <summary>An accepted drop on a different owner frees the shared SQLite index namespace for a rename.</summary>
    [Fact]
    public async Task IndexRename_AcceptedOtherOwnerDestinationDropMakesTheNameAvailable()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE namespace_source (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "CREATE INDEX rename_source ON namespace_source (Code); "
            + "CREATE TABLE namespace_other (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "CREATE INDEX rename_destination ON namespace_other (Code); "
            + "INSERT INTO namespace_source (Id, Code) VALUES (1, 'source'); "
            + "INSERT INTO namespace_other (Id, Code) VALUES (2, 'other');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("rename_source", "namespace_source", ["Code"]);
        builder.DropIndexIfExists("rename_destination", "namespace_other");
        builder.RenameIndexIfExists("rename_source", "namespace_source", "rename_destination");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-index-rename-dropped-other-owner"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-index-rename-shared-postflight"));

        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var sourceRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM namespace_source WHERE Id = 1 AND Code = 'source';");

        var otherRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM namespace_other WHERE Id = 2 AND Code = 'other';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        var renamedIndex = Assert.Single(snapshot.Tables["namespace_source"].Indexes);

        Assert.Equal("rename_destination", renamedIndex.Name);
        Assert.Equal("Code", Assert.Single(renamedIndex.Keys, key => key.IsKey).Column);
        Assert.Empty(snapshot.Tables["namespace_other"].Indexes);
        Assert.Equal(1, sourceRows);
        Assert.Equal(1, otherRows);
    }

    /// <summary>A recreated destination on another owner supersedes its tombstone before the rename decision.</summary>
    [Fact]
    public async Task IndexRename_RecreatedOtherOwnerDestinationRejectsWithoutOverwritingEitherIndex()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE namespace_source (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "CREATE INDEX rename_source ON namespace_source (Code); "
            + "CREATE TABLE namespace_other (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "CREATE INDEX rename_destination ON namespace_other (Id); "
            + "INSERT INTO namespace_source (Id, Code) VALUES (1, 'source'); "
            + "INSERT INTO namespace_other (Id, Code) VALUES (2, 'other');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("rename_source", "namespace_source", ["Code"]);
        builder.DropIndexIfExists("rename_destination", "namespace_other");
        builder.CreateIndexIfNotExists("rename_destination", "namespace_other", ["Code"]);
        builder.RenameIndexIfExists("rename_source", "namespace_source", "rename_destination");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-index-rename-recreated-other-owner"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var sourceRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM namespace_source WHERE Id = 1 AND Code = 'source';");

        var otherRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM namespace_other WHERE Id = 2 AND Code = 'other';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[3].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[3].Action);
        Assert.Contains("different", Assert.IsType<InvalidOperationException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal("rename_source", Assert.Single(snapshot.Tables["namespace_source"].Indexes).Name);
        var recreatedIndex = Assert.Single(snapshot.Tables["namespace_other"].Indexes);

        Assert.Equal("rename_destination", recreatedIndex.Name);
        Assert.Equal("Code", Assert.Single(recreatedIndex.Keys, key => key.IsKey).Column);
        Assert.Equal(1, sourceRows);
        Assert.Equal(1, otherRows);
    }

    /// <summary>
    /// Dropping an occupied destination cannot make a constraint-backed source independently renameable.
    /// </summary>
    [Fact]
    public async Task IndexRename_OtherOwnerDestinationDropDoesNotApproveConstraintBackingIndex()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE namespace_source (Id INTEGER NOT NULL, Code TEXT NOT NULL, "
            + "CONSTRAINT uq_namespace_source UNIQUE (Code)); "
            + "CREATE TABLE namespace_other (Id INTEGER NOT NULL, Code TEXT NULL); "
            + "CREATE INDEX rename_destination ON namespace_other (Code); "
            + "INSERT INTO namespace_source (Id, Code) VALUES (1, 'source'); "
            + "INSERT INTO namespace_other (Id, Code) VALUES (2, 'other');");
        var sourceSnapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var source = sourceSnapshot.Tables["namespace_source"].UniqueConstraints.Single().PhysicalName;
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("rename_destination", "namespace_other");
        builder.RenameIndexIfExists(source, "namespace_source", "rename_destination");
        var rename = (SafeMigrationOperation)builder.Operations[1];
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var live = await analyzer.AnalyzeAsync(context, [rename]);
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-index-rename-source-kind"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);
        var sourceRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM namespace_source WHERE Id = 1 AND Code = 'source';");

        var otherRows = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM namespace_other WHERE Id = 2 AND Code = 'other';");

        // Assert
        var analysis = Assert.Single(live);
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.True(analysis.RenameTargetExists);
        Assert.False(analysis.RenameSourceSupported);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.IsType<InvalidOperationException>(exception);
        var unique = Assert.Single(snapshot.Tables["namespace_source"].UniqueConstraints);

        Assert.Equal("uq_namespace_source", unique.Name);
        Assert.Equal(source, unique.PhysicalName);
        Assert.Equal(1, sourceRows);
        Assert.Equal(1, otherRows);
    }
}
