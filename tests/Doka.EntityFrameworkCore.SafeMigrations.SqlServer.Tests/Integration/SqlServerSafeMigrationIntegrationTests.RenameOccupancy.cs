namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// A combined rename and transfer rejects an occupied intermediate name before changing either table.
    /// </summary>
    [SqlServerLiveFact]
    public async Task CrossSchemaRename_OccupiedIntermediateRejectsBeforeMutation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA rename_target;");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.rename_source (Id int NOT NULL); INSERT INTO dbo.rename_source VALUES (11); "
            + "CREATE TABLE dbo.rename_destination (Id int NOT NULL); INSERT INTO dbo.rename_destination VALUES (29);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "rename_destination", "dbo", "rename_target");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-rename-intermediate-occupied"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var sourceRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.rename_source WHERE Id = 11;");

        var intermediateRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.rename_destination WHERE Id = 29;");

        var targetCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'rename_target.rename_destination', N'U');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, sourceRows);
        Assert.Equal(1, intermediateRows);
        Assert.Equal(0, targetCount);
    }

    /// <summary>A free intermediate identity permits the provider's rename-then-transfer sequence.</summary>
    [SqlServerLiveFact]
    public async Task CrossSchemaRename_FreeIntermediatePreservesRowsAtFinalDestination()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA rename_target;");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.rename_source (Id int NOT NULL); INSERT INTO dbo.rename_source VALUES (11);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "rename_destination", "dbo", "rename_target");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-rename-intermediate-free"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var targetRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM rename_target.rename_destination WHERE Id = 11;");

        var sourceCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') "
            + "AND name IN (N'rename_source', N'rename_destination');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, targetRows);
        Assert.Equal(0, sourceCount);
    }

    /// <summary>
    /// An absent source remains an idempotent replay even when the intermediate name is now occupied.
    /// </summary>
    [SqlServerLiveFact]
    public async Task CrossSchemaRename_AbsentSourceReplayIgnoresIntermediateOccupancy()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA rename_target;");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE rename_target.rename_destination (Id int NOT NULL); "
            + "INSERT INTO rename_target.rename_destination VALUES (11); "
            + "CREATE TABLE dbo.rename_destination (Id int NOT NULL); INSERT INTO dbo.rename_destination VALUES (29);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "rename_destination", "dbo", "rename_target");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-rename-intermediate-replay"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var targetRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM rename_target.rename_destination WHERE Id = 11;");

        var intermediateRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.rename_destination WHERE Id = 29;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
        Assert.Equal(1, targetRows);
        Assert.Equal(1, intermediateRows);
    }
}
