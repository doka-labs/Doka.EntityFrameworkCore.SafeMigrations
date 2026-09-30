namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Rejects primary or unique keys whose existing rows violate uniqueness.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CandidateKey_DuplicateRowsAreDataBlocked(bool primaryKey)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.duplicate_keys (Id int NOT NULL, Code int NOT NULL); "
            + "INSERT INTO dbo.duplicate_keys (Id, Code) VALUES (1, 7), (1, 7);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var constraintName = primaryKey ? "PK_duplicate_keys" : "UQ_duplicate_keys_Code";

        if (primaryKey)
        {
            builder.AddPrimaryKeyIfNotExists(constraintName, "duplicate_keys", ["Id"]);
        }
        else
        {
            builder.AddUniqueConstraintIfNotExists(constraintName, "duplicate_keys", ["Code"]);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-duplicate-key"));

        var keyCount = await ScalarIntAsync(
            connectionString,
            $"SELECT COUNT(*) FROM sys.key_constraints WHERE name = N'{constraintName}';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(0, keyCount);
    }

    /// <summary>
    /// Rejects a declared key width above the physical SQL Server limit before DDL.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(true, 500)]
    [InlineData(false, 1000)]
    public async Task CandidateKey_DeclaredWidthAboveLimitIsUnsupported(
        bool primaryKey,
        int length
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE dbo.wide_keys (Code nvarchar({length}) NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var constraintName = primaryKey ? "PK_wide_keys" : "UQ_wide_keys_Code";

        if (primaryKey)
        {
            builder.AddPrimaryKeyIfNotExists(constraintName, "wide_keys", ["Code"]);
        }
        else
        {
            builder.AddUniqueConstraintIfNotExists(constraintName, "wide_keys", ["Code"]);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-key-width"));

        var keyCount = await ScalarIntAsync(
            connectionString,
            $"SELECT COUNT(*) FROM sys.key_constraints WHERE name = N'{constraintName}';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(0, keyCount);
    }
}
