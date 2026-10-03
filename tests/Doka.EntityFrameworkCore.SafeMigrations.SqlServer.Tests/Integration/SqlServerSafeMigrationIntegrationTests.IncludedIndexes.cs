namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Creates a covering index and recognizes its included column on replay.
    /// </summary>
    [SqlServerLiveFact]
    public async Task IncludedIndex_AppliesAndReplaysWithPhysicalIncludeFacet()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.covering_users (Id int NOT NULL, Email nvarchar(320) NOT NULL, "
            + "DisplayName nvarchar(120) NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexWithIncludesIfNotExistsFromModel(
            "IX_covering_users_Email",
            "covering_users",
            "Email",
            ["DisplayName"]);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var includedColumnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes AS i "
            + "JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id "
            + "JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
            + "WHERE i.object_id = OBJECT_ID(N'dbo.covering_users', N'U') "
            + "AND i.name = N'IX_covering_users_Email' "
            + "AND ic.is_included_column = 1 AND c.name = N'DisplayName';");

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.covering_users', N'U') "
            + "AND name = N'IX_covering_users_Email';");

        // Assert
        Assert.Equal(1, includedColumnCount);
        Assert.Equal(1, indexCount);
    }

    /// <summary>
    /// Rejects an existing same-name index that lacks the required included column.
    /// </summary>
    [SqlServerLiveFact]
    public async Task IncludedIndex_MissingIncludeFacetIsDifferent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.covering_users (Id int NOT NULL, Email nvarchar(320) NOT NULL, "
            + "DisplayName nvarchar(120) NOT NULL); "
            + "CREATE INDEX IX_covering_users_Email ON dbo.covering_users (Email);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexWithIncludesIfNotExistsFromModel(
            "IX_covering_users_Email",
            "covering_users",
            "Email",
            ["DisplayName"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-included-index-drift"));

        var includedColumnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes AS i "
            + "JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id "
            + "WHERE i.object_id = OBJECT_ID(N'dbo.covering_users', N'U') "
            + "AND i.name = N'IX_covering_users_Email' AND ic.is_included_column = 1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(0, includedColumnCount);
    }
}
