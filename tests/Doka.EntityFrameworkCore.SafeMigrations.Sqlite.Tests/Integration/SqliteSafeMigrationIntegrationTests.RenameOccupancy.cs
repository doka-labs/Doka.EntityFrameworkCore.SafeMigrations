namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>A projected new source cannot overwrite an independently occupied live destination.</summary>
    /// <param name="targetExists">Whether the destination already exists before the operation stream.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatedSourceRename_UsesIndependentLiveDestinationPresence(bool targetExists)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        if (targetExists)
        {
            await ExecuteSqlAsync(connection, "CREATE TABLE rename_destination (Id INTEGER NOT NULL);");
        }

        await using var context = CreateContext(connection);
        var builder = CreateRenameSource(context);
        builder.RenameTableIfExists("rename_source", "rename_destination");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("rename-created"));

        var sourceCount = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'rename_source';");

        // Assert
        var rename = report.Assessments[^1];
        Assert.Equal(targetExists ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(targetExists ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Apply, rename.Action);
        Assert.Equal(0, sourceCount);
    }

    /// <summary>An accepted destination drop discharges its immutable occupancy before a new-source rename.</summary>
    [Fact]
    public async Task CreatedSourceRename_AcceptedDestinationDropDischargesLiveOccupancy()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE rename_destination (Id INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = CreateRenameSource(context);
        builder.DropTableIfExists("rename_destination");
        builder.RenameTableIfExists("rename_source", "rename_destination");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("rename-dropped"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
    }

    /// <summary>Destination presence remains replay evidence without an earlier source creation.</summary>
    [Fact]
    public async Task MissingSourceRename_WithExistingDestinationRemainsNoOp()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE rename_destination (Id INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "rename_destination");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("rename-replay"));

        // Assert
        var rename = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, rename.Action);
    }

    /// <summary>Views and indexes share the table-name namespace and are not free rename destinations.</summary>
    /// <param name="createOccupant">The non-table object occupying the target name.</param>
    [Theory]
    [InlineData("CREATE VIEW rename_destination AS SELECT Id FROM occupied_owner;")]
    [InlineData("CREATE INDEX rename_destination ON occupied_owner (Id);")]
    public async Task CreatedSourceRename_RejectsNonTableDestinationOccupant(string createOccupant)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE occupied_owner (Id INTEGER NOT NULL); " + createOccupant);
        await using var context = CreateContext(connection);
        var builder = CreateRenameSource(context);
        builder.RenameTableIfExists("rename_source", "RENAME_DESTINATION");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("rename-object"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
    }

    /// <summary>
    /// A live source must not be mistaken for an absent-source replay when another object occupies the destination.
    /// </summary>
    /// <param name="createOccupant">The non-table object occupying the target name.</param>
    [Theory]
    [InlineData("CREATE VIEW rename_destination AS SELECT Id FROM occupied_owner;")]
    [InlineData("CREATE INDEX rename_destination ON occupied_owner (Id);")]
    public async Task ExistingSourceRename_RejectsNonTableDestinationOccupant(string createOccupant)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE rename_source (Id INTEGER NOT NULL); "
            + "CREATE TABLE occupied_owner (Id INTEGER NOT NULL); " + createOccupant);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "RENAME_DESTINATION");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("rename-live-source-object"));

        var sourceCount = await ScalarIntAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'rename_source';");

        // Assert
        var rename = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, rename.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, rename.Action);
        Assert.Equal(1, sourceCount);
    }

    /// <summary>
    /// An actually absent source remains a no-op when the destination belongs to another object kind.
    /// </summary>
    /// <param name="createOccupant">The non-table object occupying the target name.</param>
    [Theory]
    [InlineData("CREATE VIEW rename_destination AS SELECT Id FROM occupied_owner;")]
    [InlineData("CREATE INDEX rename_destination ON occupied_owner (Id);")]
    public async Task MissingSourceRename_NonTableDestinationRemainsNoOp(string createOccupant)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE occupied_owner (Id INTEGER NOT NULL); " + createOccupant);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "RENAME_DESTINATION");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("rename-missing-source-object"));

        // Assert
        var rename = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, rename.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, rename.Action);
    }

    /// <summary>An unrelated trigger name is not in the table namespace and must not block a rename.</summary>
    [Fact]
    public async Task CreatedSourceRename_AllowsIndependentTriggerName()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE occupied_owner (Id INTEGER NOT NULL); "
            + "CREATE TRIGGER rename_destination AFTER INSERT ON occupied_owner BEGIN SELECT 1; END;");
        await using var context = CreateContext(connection);
        var builder = CreateRenameSource(context);
        builder.RenameTableIfExists("rename_source", "rename_destination");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("rename-trigger"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
    }

    /// <summary>A projected source cannot bypass the active SQLite rename capability boundary.</summary>
    /// <param name="legacyAlterTable">Whether SQLite uses its unsupported legacy rename mode.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatedSourceRename_RetainsLegacyAlterTableBoundary(bool legacyAlterTable)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, legacyAlterTable
            ? "PRAGMA legacy_alter_table = ON;" : "PRAGMA legacy_alter_table = OFF;");
        await using var context = CreateContext(connection);
        var builder = CreateRenameSource(context);
        builder.RenameTableIfExists("rename_source", "rename_destination");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("rename-capability"));

        // Assert
        Assert.Equal(legacyAlterTable ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(legacyAlterTable ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.Apply,
            report.Assessments[^1].Action);
    }

    /// <summary>Unsupported rename mode does not reject a genuine missing-source replay.</summary>
    [Fact]
    public async Task MissingSourceRename_LegacyAlterTableDoesNotBlockReplay()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE rename_destination (Id INTEGER NOT NULL); "
            + "PRAGMA legacy_alter_table = ON;");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "rename_destination");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("rename-capability-replay"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
    }

    private static MigrationBuilder CreateRenameSource(DbContext context)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("rename_source", table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false),
        });

        return builder;
    }
}
