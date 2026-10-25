namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies actual EF history, rollback and recovery for a populated generated-column upgrade.</summary>
public sealed class SqlServerSafeMigrationGeneratedHistoryIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>Creates the isolated native history suite.</summary>
    /// <param name="fixture">The assembly-owned native SQL Server container fixture.</param>
    public SqlServerSafeMigrationGeneratedHistoryIntegrationTests(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>Applies a populated upgrade, replays it, downgrades and upgrades without duplicating history.</summary>
    [SqlServerLiveFact]
    public async Task GeneratedUpgradeRunsThroughRealEfHistoryAndReplays()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = new SqlServerGeneratedUpgradeContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(SqlServerGeneratedInitialMigration.MigrationIdentifier);
        var migration = new SqlServerGeneratedUpgradeMigration { ActiveProvider = context.Database.ProviderName! };

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, migration.UpOperations,
            new SafeMigrationRunOptions("generated-real-history"));

        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();
        await ExecuteOperationsAsync(context, migration.UpOperations);
        await context.GetService<IMigrator>().MigrateAsync(SqlServerGeneratedInitialMigration.MigrationIdentifier);
        await context.Database.MigrateAsync();
        var flags = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.generated_upgrade_nodes WHERE (Id=1 AND HasChildren=1) "
            + "OR (Id=2 AND HasChildren=0);");

        var checks = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints "
            + "WHERE parent_object_id=OBJECT_ID(N'dbo.generated_upgrade_nodes') "
            + "AND is_disabled=0 AND is_not_trusted=0;");

        var history = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'"
            + SqlServerGeneratedUpgradeMigration.MigrationIdentifier + "';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(2, flags);
        Assert.Equal(4, checks);
        Assert.Equal(1, history);
    }

    /// <summary>A later CHECK failure rolls back the earlier computed ADD and upgrade history.</summary>
    [SqlServerLiveFact]
    public async Task GeneratedUpgradeCheckFailureRollsBackColumnAndHistory()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = new SqlServerGeneratedUpgradeContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(SqlServerGeneratedInitialMigration.MigrationIdentifier);
        await ExecuteSqlAsync(connectionString, "UPDATE dbo.generated_upgrade_nodes SET L=0 WHERE Id=1;");

        // Act
        var failure = await Record.ExceptionAsync(() => context.Database.MigrateAsync());
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.computed_columns WHERE object_id=OBJECT_ID(N'dbo.generated_upgrade_nodes');");

        var history = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'"
            + SqlServerGeneratedUpgradeMigration.MigrationIdentifier + "';");

        var original = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.generated_upgrade_nodes WHERE Id=1 AND L=0 AND R=4;");

        // Assert
        Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(0, columns);
        Assert.Equal(0, history);
        Assert.Equal(1, original);
    }

    /// <summary>A corrected prerequisite permits retry after a fully rolled-back generated-column upgrade.</summary>
    [SqlServerLiveFact]
    public async Task GeneratedUpgradeRecoversAfterCorrectingBlockingRows()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = new SqlServerGeneratedUpgradeContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(SqlServerGeneratedInitialMigration.MigrationIdentifier);
        await ExecuteSqlAsync(connectionString, "UPDATE dbo.generated_upgrade_nodes SET L=0 WHERE Id=1;");
        _ = await Record.ExceptionAsync(() => context.Database.MigrateAsync());
        await ExecuteSqlAsync(connectionString, "UPDATE dbo.generated_upgrade_nodes SET L=1 WHERE Id=1;");

        // Act
        await context.Database.MigrateAsync();
        var history = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'"
            + SqlServerGeneratedUpgradeMigration.MigrationIdentifier + "';");

        var flags = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.generated_upgrade_nodes WHERE (Id=1 AND HasChildren=1) "
            + "OR (Id=2 AND HasChildren=0);");

        // Assert
        Assert.Equal(1, history);
        Assert.Equal(2, flags);
    }
}
