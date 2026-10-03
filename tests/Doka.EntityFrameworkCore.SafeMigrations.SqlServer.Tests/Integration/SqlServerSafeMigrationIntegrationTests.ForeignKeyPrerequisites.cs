namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Adds a foreign key over valid existing rows and recognizes it on replay.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ForeignKey_ValidExistingRowsApplyAndReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.valid_parent (Id int NOT NULL CONSTRAINT PK_valid_parent PRIMARY KEY); "
            + "CREATE TABLE dbo.valid_child (Id int NOT NULL, ParentId int NOT NULL); "
            + "INSERT INTO dbo.valid_parent (Id) VALUES (3); "
            + "INSERT INTO dbo.valid_child (Id, ParentId) VALUES (1, 3);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "FK_valid_child_parent", "valid_child", ["ParentId"], "valid_parent", ["Id"]);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var trustedForeignKeyCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys "
            + "WHERE parent_object_id = OBJECT_ID(N'dbo.valid_child', N'U') "
            + "AND name = N'FK_valid_child_parent' AND is_disabled = 0 AND is_not_trusted = 0;");

        // Assert
        Assert.Equal(1, trustedForeignKeyCount);
    }

    /// <summary>
    /// Blocks an FK before DDL when dependent rows lack a parent.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ForeignKey_OrphanRowsAreDataBlocked()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.orphan_parent (Id int NOT NULL CONSTRAINT PK_orphan_parent PRIMARY KEY); "
            + "CREATE TABLE dbo.orphan_child (Id int NOT NULL, ParentId int NOT NULL); "
            + "INSERT INTO dbo.orphan_parent (Id) VALUES (3); "
            + "INSERT INTO dbo.orphan_child (Id, ParentId) VALUES (1, 4);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "FK_orphan_child_parent", "orphan_child", ["ParentId"], "orphan_parent", ["Id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-fk-orphan"));

        var foreignKeyCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_orphan_child_parent';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(0, foreignKeyCount);
    }

    /// <summary>
    /// Requires a physically compatible referenced key before evaluating FK rows.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(false, "int")]
    [InlineData(true, "bigint")]
    public async Task ForeignKey_IncompatibleTypeOrMissingCandidateKeyRejectsPrerequisite(
        bool hasCandidateKey,
        string dependentType
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var parentKey = hasCandidateKey ? "CONSTRAINT PK_prereq_parent PRIMARY KEY" : string.Empty;
        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE dbo.prereq_parent (Id int NOT NULL {parentKey}); "
            + $"CREATE TABLE dbo.prereq_child (Id int NOT NULL, ParentId {dependentType} NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "FK_prereq_child_parent", "prereq_child", ["ParentId"], "prereq_parent", ["Id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-fk-prerequisite"));

        var foreignKeyCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_prereq_child_parent';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(0, foreignKeyCount);
    }
}
