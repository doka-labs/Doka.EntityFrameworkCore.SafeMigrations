namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    private const string RestrictedLoginPassword = "SafeMigrations_Login_2026!";

    /// <summary>
    /// Rejects an unqualified operation when the principal's default schema is not dbo.
    /// </summary>
    [SqlServerLiveFact]
    public async Task NonDboPrincipal_UnqualifiedOperationFailsClosed()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        var login = $"sm_user_{Guid.NewGuid():N}";
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA application;");
        await ExecuteSqlAsync(
            connectionString,
            $"CREATE LOGIN [{login}] WITH PASSWORD = '{RestrictedLoginPassword}', CHECK_POLICY = OFF;");
        await ExecuteSqlAsync(
            connectionString,
            $"CREATE USER [{login}] FOR LOGIN [{login}] WITH DEFAULT_SCHEMA = application;");
        await ExecuteSqlAsync(connectionString, $"GRANT VIEW DEFINITION TO [{login}];");
        var userConnectionString = new SqlConnectionStringBuilder(connectionString)
        {
            UserID = login,
            Password = RestrictedLoginPassword,
        }.ConnectionString;

        await using var context = CreateContext(userConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("absent_table");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-non-dbo-default"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.NotEqual(SafeMigrationObservedState.Missing, Assert.Single(report.Assessments).ObservedState);
    }

    /// <summary>
    /// Never treats a hidden catalog row as proof that an object is absent.
    /// </summary>
    [SqlServerLiveFact]
    public async Task RestrictedMetadataVisibility_DoesNotClassifyExistingTableAsMissing()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        var login = $"sm_user_{Guid.NewGuid():N}";
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.hidden_orders (Id int NOT NULL);");
        await ExecuteSqlAsync(
            connectionString,
            $"CREATE LOGIN [{login}] WITH PASSWORD = '{RestrictedLoginPassword}', CHECK_POLICY = OFF;");
        await ExecuteSqlAsync(connectionString, $"CREATE USER [{login}] FOR LOGIN [{login}];");
        var userConnectionString = new SqlConnectionStringBuilder(connectionString)
        {
            UserID = login,
            Password = RestrictedLoginPassword,
        }.ConnectionString;

        await using var context = CreateContext(userConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "hidden_orders",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-hidden-metadata"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.NotEqual(SafeMigrationObservedState.Missing, Assert.Single(report.Assessments).ObservedState);
    }

    /// <summary>
    /// Keeps differently cased names distinct when the database uses a case-sensitive catalog collation.
    /// </summary>
    [SqlServerLiveFact]
    public async Task CaseSensitiveCatalog_DoesNotDropDifferentlyCasedTable()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "ALTER DATABASE CURRENT COLLATE Latin1_General_100_CS_AS;");
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.CaseOrders (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("caseorders");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);

        var existingCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.CaseOrders', N'U');");

        // Assert
        Assert.Equal(1, existingCount);
    }
}
