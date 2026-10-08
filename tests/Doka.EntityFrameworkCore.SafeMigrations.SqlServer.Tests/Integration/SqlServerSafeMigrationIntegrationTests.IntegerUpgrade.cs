namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Executes an ordered EF migration, preserving history, values, defaults, and trusted checks.</summary>
    [SqlServerLiveFact]
    public async Task IntegerUpgrade_OrderedPreflightEfMigrationAndHistoryReplay()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = new SqlServerIntegerUpgradeContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(SqlServerIntegerInitialMigration.MigrationIdentifier);
        var upgrade = new SqlServerIntegerUpgradeMigration { ActiveProvider = context.Database.ProviderName! };

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, upgrade.UpOperations,
            new SafeMigrationRunOptions("sqlserver-ordered-integer-upgrade"));

        await context.Database.MigrateAsync();
        await context.Database.MigrateAsync();
        await ExecuteOperationsAsync(context, upgrade.UpOperations);
        var widened = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ordered_values') "
            + "AND name IN(N'Lower',N'Upper',N'Depth',N'Position') AND system_type_id=127;");

        var trusted = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.ordered_values') "
            + "AND is_disabled=0 AND is_not_trusted=0;");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordered_values WHERE (Id=1 AND Lower=1 AND Upper=2 AND Position IS NULL) "
            + "OR (Id=2 AND Lower=2147483646 AND Upper=2147483647 AND Position=0);");

        var history = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'"
            + SqlServerIntegerUpgradeMigration.MigrationIdentifier + "';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(4, report.Assessments.Count(static assessment => assessment.Action == SafeMigrationAction.Repair));
        Assert.Equal(4, widened);
        Assert.Equal(4, trusted);
        Assert.Equal(2, rows);
        Assert.Equal(1, history);
    }

    /// <summary>A later failing CHECK rolls back dependency drops, all widenings, and upgrade history.</summary>
    [SqlServerLiveTheory]
    [InlineData("char")]
    [InlineData("nvarchar")]
    public async Task IntegerUpgrade_CheckFailureRollsBackEntireEfTransactionAndHistory(
        string payloadType
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = new SqlServerIntegerUpgradeContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(SqlServerIntegerInitialMigration.MigrationIdentifier);
        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE dbo.ordered_values ADD Payload " + payloadType + "(100) NOT NULL "
            + "DEFAULT REPLICATE('x',100);");
        // WHY: Retained fixed-width ALTER versions consume row capacity. Keep
        // this history fixture bounded so the later CHECK owns the rollback.
        var widths = payloadType == "char" ? new[] { 200, 300, 100 } : new[] { 2000, 3000, 100 };
        for (var cycle = 0; cycle < 8; cycle++)
        {
            foreach (var width in widths)
            {
                try
                {
                    await ExecuteSqlAsync(connectionString,
                        "ALTER TABLE dbo.ordered_values ALTER COLUMN Payload " + payloadType + "(" + width
                        + ") NOT NULL;");
                }
                catch (SqlException setupFailure)
                {
                    throw new InvalidOperationException(
                        $"Prior ALTER history fixture failed at cycle {cycle}, {payloadType}({width}).", setupFailure);
                }
            }
        }

        await ExecuteSqlAsync(connectionString, "UPDATE dbo.ordered_values SET Lower=0 WHERE Id=1;");

        // Act
        var failure = await Record.ExceptionAsync(() => context.Database.MigrateAsync());
        var sourceColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ordered_values') "
            + "AND name IN(N'Lower',N'Upper',N'Depth',N'Position') AND system_type_id=56;");

        var oldDependencies = await ScalarIntAsync(connectionString,
            "SELECT (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ordered_values') "
            + "AND name=N'IX_old_range') + (SELECT COUNT(*) FROM sys.check_constraints "
            + "WHERE parent_object_id=OBJECT_ID(N'dbo.ordered_values') AND name=N'CK_lower_old');");

        var marker = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.upgrade_marker');");

        var upgradeHistory = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'"
            + SqlServerIntegerUpgradeMigration.MigrationIdentifier + "';");

        var payloadRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordered_values WHERE Payload=REPLICATE('x',100) AND LEN(Payload)=100;");

        // Assert
        Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(4, sourceColumns);
        Assert.Equal(2, oldDependencies);
        Assert.Equal(0, marker);
        Assert.Equal(0, upgradeHistory);
        Assert.Equal(2, payloadRows);
    }
}
