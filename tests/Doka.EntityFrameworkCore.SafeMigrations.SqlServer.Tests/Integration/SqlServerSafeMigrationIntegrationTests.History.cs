namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Applies an EF migration and records its history exactly once across repeated migrate calls.
    /// </summary>
    [SqlServerLiveFact]
    public async Task EfMigrator_RecordsHistoryOnceAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);

        // Act
        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();

        var historyCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory "
            + $"WHERE MigrationId = N'{SqlServerHistoryMigration.MigrationIdentifier}';");

        var safeTableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.safe_history_probe', N'U');");

        // Assert
        Assert.Equal(1, historyCount);
        Assert.Equal(1, safeTableCount);
    }

    /// <summary>
    /// Rolls back preceding ordinary DDL and leaves history unapplied after a safe-operation conflict.
    /// </summary>
    [SqlServerLiveFact]
    public async Task EfMigrator_BlockedSafeOperationRollsBackOrdinaryDdlAndHistory()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.safe_history_probe (Id bigint NOT NULL);");
        await using var context = CreateContext(connectionString);

        // Act
        var exception = await Record.ExceptionAsync(() => context.Database.MigrateAsync());

        var ordinaryTableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.ordinary_pipeline_probe', N'U');");

        var historyRowCount = await ScalarIntAsync(
            connectionString,
            "IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL SELECT 0; "
            + "ELSE SELECT COUNT(*) FROM dbo.__EFMigrationsHistory "
            + $"WHERE MigrationId = N'{SqlServerHistoryMigration.MigrationIdentifier}';");

        // Assert
        Assert.NotNull(exception);
        Assert.Equal(0, ordinaryTableCount);
        Assert.Equal(0, historyRowCount);
    }

    /// <summary>
    /// Rolls back ordinary and safe DDL when the SQL Server contract stamp is rejected.
    /// </summary>
    [SqlServerLiveFact]
    public async Task EfMigrator_FailedContractStampRollsBackDdlAndHistory()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TRIGGER reject_contract_stamp ON DATABASE FOR CREATE_EXTENDED_PROPERTY AS "
            + "BEGIN THROW 51009, N'Injected extended-property failure', 1; END;");
        await using var context = CreateContext(connectionString);

        // Act
        var exception = await Record.ExceptionAsync(() => context.Database.MigrateAsync());

        var createdTableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name IN "
            + "(N'ordinary_pipeline_probe', N'safe_history_probe');");

        var historyRowCount = await ScalarIntAsync(
            connectionString,
            "IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL SELECT 0; "
            + "ELSE SELECT COUNT(*) FROM dbo.__EFMigrationsHistory "
            + $"WHERE MigrationId = N'{SqlServerHistoryMigration.MigrationIdentifier}';");

        // Assert
        Assert.NotNull(exception);
        Assert.Equal(0, createdTableCount);
        Assert.Equal(0, historyRowCount);
    }
}
