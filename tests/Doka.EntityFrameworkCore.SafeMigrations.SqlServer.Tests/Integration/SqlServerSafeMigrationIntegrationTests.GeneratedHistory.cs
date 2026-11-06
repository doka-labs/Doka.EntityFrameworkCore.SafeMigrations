namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies actual EF history, rollback and recovery for a populated generated-column upgrade.</summary>
public sealed class SqlServerSafeMigrationGeneratedHistoryIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>Creates the isolated native history suite.</summary>
    /// <param name="fixture">The assembly-owned native SQL Server container fixture.</param>
    public SqlServerSafeMigrationGeneratedHistoryIntegrationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Applies a populated upgrade, replays it, downgrades and upgrades without duplicating history.</summary>
    [SqlServerLiveFact]
    public async Task GeneratedUpgradeRunsThroughRealEfHistoryAndReplays()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(cancellationToken: CancellationToken.None);
        await using var context = new SqlServerGeneratedUpgradeContext(connectionString);
        await context
            .GetService<IMigrator>()
            .MigrateAsync(SqlServerGeneratedInitialMigration.MigrationIdentifier);

        var migration = new SqlServerGeneratedUpgradeMigration { ActiveProvider = context.Database.ProviderName! };

        // Act
        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                migration.UpOperations,
                new SafeMigrationRunOptions("generated-real-history"),
                cancellationToken: CancellationToken.None);

        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();
        await ExecuteOperationsAsync(context, migration.UpOperations, cancellationToken: CancellationToken.None);
        await context
            .GetService<IMigrator>()
            .MigrateAsync(SqlServerGeneratedInitialMigration.MigrationIdentifier);

        await context.Database.MigrateAsync();
        var flags = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.generated_upgrade_nodes WHERE (Id=1 AND HasChildren=1) "
            + "OR (Id=2 AND HasChildren=0);",
            cancellationToken: CancellationToken.None);

        var checks = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints "
            + "WHERE parent_object_id=OBJECT_ID(N'dbo.generated_upgrade_nodes') "
            + "AND is_disabled=0 AND is_not_trusted=0;",
            cancellationToken: CancellationToken.None);

        var history = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'"
            + SqlServerGeneratedUpgradeMigration.MigrationIdentifier
            + "';",
            cancellationToken: CancellationToken.None);

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
        var connectionString = await CreateDatabaseAsync(cancellationToken: CancellationToken.None);
        await using var context = new SqlServerGeneratedUpgradeContext(connectionString);
        await context
            .GetService<IMigrator>()
            .MigrateAsync(SqlServerGeneratedInitialMigration.MigrationIdentifier);

        await ExecuteSqlAsync(
            connectionString,
            "UPDATE dbo.generated_upgrade_nodes SET L=0 WHERE Id=1;",
            cancellationToken: CancellationToken.None);

        // Act
        var failure = await Record.ExceptionAsync(() => context.Database.MigrateAsync());
        var columns = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.computed_columns WHERE object_id=OBJECT_ID(N'dbo.generated_upgrade_nodes');",
            cancellationToken: CancellationToken.None);

        var history = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'"
            + SqlServerGeneratedUpgradeMigration.MigrationIdentifier
            + "';",
            cancellationToken: CancellationToken.None);

        var original = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.generated_upgrade_nodes WHERE Id=1 AND L=0 AND R=4;",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(51003,
            Assert.IsType<SqlException>(failure)
                .Number);

        Assert.Equal(0, columns);
        Assert.Equal(0, history);
        Assert.Equal(1, original);
    }

    /// <summary>A corrected prerequisite permits retry after a fully rolled-back generated-column upgrade.</summary>
    [SqlServerLiveFact]
    public async Task GeneratedUpgradeRecoversAfterCorrectingBlockingRows()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(cancellationToken: CancellationToken.None);
        await using var context = new SqlServerGeneratedUpgradeContext(connectionString);
        await context
            .GetService<IMigrator>()
            .MigrateAsync(SqlServerGeneratedInitialMigration.MigrationIdentifier);

        await ExecuteSqlAsync(
            connectionString,
            "UPDATE dbo.generated_upgrade_nodes SET L=0 WHERE Id=1;",
            cancellationToken: CancellationToken.None);

        _ = await Record.ExceptionAsync(() => context.Database.MigrateAsync());
        await ExecuteSqlAsync(
            connectionString,
            "UPDATE dbo.generated_upgrade_nodes SET L=1 WHERE Id=1;",
            cancellationToken: CancellationToken.None);

        // Act
        await context.Database.MigrateAsync();
        var history = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'"
            + SqlServerGeneratedUpgradeMigration.MigrationIdentifier
            + "';",
            cancellationToken: CancellationToken.None);

        var flags = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.generated_upgrade_nodes WHERE (Id=1 AND HasChildren=1) "
            + "OR (Id=2 AND HasChildren=0);",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(1, history);
        Assert.Equal(2, flags);
    }
}
