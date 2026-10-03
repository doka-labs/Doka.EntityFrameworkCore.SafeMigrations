namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Inserts a source-controlled row once and treats an exact replay as a no-op.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ModelManagedInsert_AppliesAndReplaysExactRow()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.managed_settings (Id int NOT NULL CONSTRAINT PK_managed_settings PRIMARY KEY, "
            + "Caption nvarchar(80) NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            table: "managed_settings",
            keyColumns: ["Id"],
            keyColumnTypes: ["int"],
            columns: ["Id", "Caption"],
            columnTypes: ["int", "nvarchar(80)"],
            values: new object?[,] { { 1, "source" } });

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var rowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.managed_settings WHERE Id = 1 AND Caption = N'source';");

        var totalCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.managed_settings;");

        // Assert
        Assert.Equal(1, rowCount);
        Assert.Equal(1, totalCount);
    }

    /// <summary>
    /// Preserves user-modified model-managed data instead of silently overwriting it.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ModelManagedInsert_RejectsDifferentExistingRowWithoutMutation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.managed_drift (Id int NOT NULL CONSTRAINT PK_managed_drift PRIMARY KEY, "
            + "Caption nvarchar(80) NOT NULL); "
            + "INSERT INTO dbo.managed_drift (Id, Caption) VALUES (1, N'user-edited');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            table: "managed_drift",
            keyColumns: ["Id"],
            keyColumnTypes: ["int"],
            columns: ["Id", "Caption"],
            columnTypes: ["int", "nvarchar(80)"],
            values: new object?[,] { { 1, "source" } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-drift"));

        var rowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.managed_drift WHERE Id = 1 AND Caption = N'user-edited';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, rowCount);
    }
}
