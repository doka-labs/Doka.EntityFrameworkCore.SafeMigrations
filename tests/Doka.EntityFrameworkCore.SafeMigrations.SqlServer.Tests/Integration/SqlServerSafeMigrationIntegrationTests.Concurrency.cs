namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Serializes application through two independent EF migrators and records the complete migration exactly once.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ConcurrentEfMigratorsApplyHistoryAndSchemaExactlyOnce()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var firstContext = CreateContext(connectionString);
        await using var secondContext = CreateContext(connectionString);

        // Act
        // WHY: Separate contexts exercise EF's database migration lock, not
        // unsupported concurrent commands on one mutable DbContext instance.
        await Task.WhenAll(firstContext.Database.MigrateAsync(), secondContext.Database.MigrateAsync());
        var migrationCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory "
            + $"WHERE MigrationId = N'{SqlServerHistoryMigration.MigrationIdentifier}';");

        var totalHistoryRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory;");

        var safeTableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.safe_history_probe', N'U');");

        var ordinaryTableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.ordinary_pipeline_probe', N'U');");

        var requiredCaptionCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.safe_history_probe', N'U') "
            + "AND name = N'Caption' AND system_type_id = 231 AND max_length = 160 "
            + "AND is_nullable = 0 AND default_object_id <> 0;");

        var primaryKeyCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id = "
            + "OBJECT_ID(N'dbo.safe_history_probe', N'U') AND name = N'PK_safe_history_probe' AND type = N'PK';");

        var checkCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = "
            + "OBJECT_ID(N'dbo.safe_history_probe', N'U') AND name = N'CK_safe_history_probe_Id' "
            + "AND is_disabled = 0 AND is_not_trusted = 0;");

        var safeRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.safe_history_probe;");
        var ordinaryRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.ordinary_pipeline_probe;");

        // Assert
        Assert.Equal(1, migrationCount);
        Assert.Equal(1, totalHistoryRows);
        Assert.Equal(1, safeTableCount);
        Assert.Equal(1, ordinaryTableCount);
        Assert.Equal(1, requiredCaptionCount);
        Assert.Equal(1, primaryKeyCount);
        Assert.Equal(1, checkCount);
        Assert.Equal(0, safeRows);
        Assert.Equal(0, ordinaryRows);
    }

    /// <summary>
    /// Rolls back both incompatible applications without ordinary-prefix persistence or changed drifted data.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ConcurrentEfMigratorsRejectDriftWithoutPartialApplication()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.safe_history_probe (Id bigint NOT NULL); "
            + "INSERT dbo.safe_history_probe (Id) VALUES (17);");
        await using var firstContext = CreateContext(connectionString);
        await using var secondContext = CreateContext(connectionString);

        // Act
        var originalTableId = await ScalarIntAsync(
            connectionString,
            "SELECT OBJECT_ID(N'dbo.safe_history_probe', N'U');");

        var failures = await Task.WhenAll(
            Record.ExceptionAsync(() => firstContext.Database.MigrateAsync()),
            Record.ExceptionAsync(() => secondContext.Database.MigrateAsync()));

        var remainingTableId = await ScalarIntAsync(
            connectionString,
            "SELECT OBJECT_ID(N'dbo.safe_history_probe', N'U');");

        var ordinaryTableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.ordinary_pipeline_probe', N'U');");

        var historyRows = await ScalarIntAsync(
            connectionString,
            "IF OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL SELECT 0; "
            + "ELSE SELECT COUNT(*) FROM dbo.__EFMigrationsHistory;");

        var originalIdColumnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.safe_history_probe', N'U') "
            + "AND name = N'Id' AND system_type_id = 127 AND is_nullable = 0;");

        var newCaptionCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.safe_history_probe', N'U') "
            + "AND name = N'Caption';");

        var newConstraintCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.objects WHERE parent_object_id = OBJECT_ID(N'dbo.safe_history_probe', N'U') "
            + "AND type IN (N'PK', N'C');");

        var remainingRowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.safe_history_probe;");
        var unchangedRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.safe_history_probe WHERE Id = 17;");

        // Assert
        Assert.Equal(2, failures.Length);
        Assert.All(failures, failure => Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number));
        Assert.Equal(originalTableId, remainingTableId);
        Assert.Equal(0, ordinaryTableCount);
        Assert.Equal(0, historyRows);
        Assert.Equal(1, originalIdColumnCount);
        Assert.Equal(0, newCaptionCount);
        Assert.Equal(0, newConstraintCount);
        Assert.Equal(1, remainingRowCount);
        Assert.Equal(1, unchangedRows);
    }
}
