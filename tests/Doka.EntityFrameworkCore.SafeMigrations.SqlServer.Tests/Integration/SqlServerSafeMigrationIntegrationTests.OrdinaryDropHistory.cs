namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies ordinary native blockers through EF history, correction, retry and script replay.</summary>
public sealed class SqlServerSafeMigrationOrdinaryDropHistoryIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>Creates isolated databases on the assembly-owned native SQL Server container.</summary>
    /// <param name="fixture">The shared native container fixture.</param>
    public SqlServerSafeMigrationOrdinaryDropHistoryIntegrationTests(SqlServerContainerFixture fixture)
        : base(fixture) { }

    /// <summary>Both actual EF entry points preserve the source, rows, index and absent upgrade history.</summary>
    /// <param name="asynchronous">Whether to execute the asynchronous EF migration entry point.</param>
    /// <param name="check">Whether a CHECK rather than an index is the sole native blocker.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativeBlockerRejectsRealMigrationWithoutRecordingHistory(bool asynchronous, bool check)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = HistoryContext(connectionString, check);
        await context.GetService<IMigrator>().MigrateAsync(InitialIdentifier(check));

        // Act
        // WHY: No optional preflight may warm analysis caches before either mandatory migration entry point.
        var failure = asynchronous
            ? await Record.ExceptionAsync(() => context.Database.MigrateAsync())
            : Record.Exception(() => context.Database.Migrate());

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_history WHERE (Id=1 AND L=1 AND R=4) "
            + "OR (Id=2 AND L=2 AND R=8);");

        var history = await UpgradeHistoryAsync(connectionString, check);
        var dependency = await ScalarIntAsync(connectionString, check
            ? "SELECT COUNT(*) FROM sys.check_constraints "
                + "WHERE parent_object_id=OBJECT_ID(N'dbo.ordinary_drop_history') "
                + "AND name=N'CK_ordinary_drop_history_L';"
            : "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ordinary_drop_history') "
                + "AND name=N'IX_ordinary_drop_history_L';");

        // Assert
        Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(2, rows);
        Assert.Equal(1, dependency);
        Assert.Equal(0, history);
    }

    /// <summary>The unchanged historical migration succeeds after correcting its native index prerequisite.</summary>
    /// <param name="check">Whether a CHECK rather than an index is the sole native blocker.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrectedNativePrerequisitePermitsUnchangedRetryAndHistoryReplay(bool check)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = HistoryContext(connectionString, check);
        await context.GetService<IMigrator>().MigrateAsync(InitialIdentifier(check));
        var firstFailure = await Record.ExceptionAsync(() => context.Database.MigrateAsync());
        var rejectedHistory = await UpgradeHistoryAsync(connectionString, check);
        await ExecuteSqlAsync(connectionString, CorrectPrerequisiteSql(check));

        // Act
        await context.Database.MigrateAsync();
        context.Database.Migrate();
        var history = await UpgradeHistoryAsync(connectionString, check);
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ordinary_drop_history') AND name=N'L';");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_history WHERE (Id=1 AND R=4) OR (Id=2 AND R=8);");

        // Assert
        Assert.Equal(51001, Assert.IsType<SqlException>(firstFailure).Number);
        Assert.Equal(0, rejectedHistory);
        Assert.Equal(1, history);
        Assert.Equal(0, columns);
        Assert.Equal(2, rows);
    }

    /// <summary>Complete ordinary and idempotent scripts preserve refusal and support corrected retry.</summary>
    /// <param name="idempotent">Whether the whole script is protected by EF migration-history guards.</param>
    /// <param name="check">Whether a CHECK rather than an index is the sole native blocker.</param>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CompleteScriptRejectsBlockerAndCorrectedUpgradeReplays(bool idempotent, bool check)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = HistoryContext(connectionString, check);
        var options = idempotent ? MigrationsSqlGenerationOptions.Idempotent : MigrationsSqlGenerationOptions.Default;
        var migrator = context.GetService<IMigrator>();
        var complete = migrator.GenerateScript("0", null, options);
        var upgrade = migrator.GenerateScript(InitialIdentifier(check), null, options);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteScriptAsync(connectionString, complete));
        var before = await UpgradeHistoryAsync(connectionString, check);
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_history WHERE L=1 AND R=4;");

        await ExecuteSqlAsync(connectionString, CorrectPrerequisiteSql(check));
        await ExecuteScriptAsync(connectionString, upgrade);
        if (idempotent)
        {
            await ExecuteScriptAsync(connectionString, complete);
        }

        var history = await UpgradeHistoryAsync(connectionString, check);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ordinary_drop_history WHERE (Id=1 AND R=4) OR (Id=2 AND R=8);");

        // Assert
        Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(0, before);
        Assert.Equal(1, retained);
        Assert.Equal(1, history);
        Assert.Equal(2, rows);
    }

    private static Task<int> UpgradeHistoryAsync(string connectionString, bool check)
        => ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId=N'"
            + (check ? SqlServerOrdinaryDropCheckBlockedMigration.Identifier
                : SqlServerOrdinaryDropBlockedMigration.MigrationIdentifier) + "';");

    private static string InitialIdentifier(bool check)
        => check ? SqlServerOrdinaryDropCheckInitialMigration.Identifier
            : SqlServerOrdinaryDropInitialMigration.MigrationIdentifier;

    private static DbContext HistoryContext(string connectionString, bool check)
        => check ? new SqlServerOrdinaryDropCheckHistoryContext(connectionString)
            : new SqlServerOrdinaryDropHistoryContext(connectionString);

    private static string CorrectPrerequisiteSql(bool check)
        => check ? "ALTER TABLE dbo.ordinary_drop_history DROP CONSTRAINT CK_ordinary_drop_history_L;"
            : "DROP INDEX IX_ordinary_drop_history_L ON dbo.ordinary_drop_history;";

    private static async Task ExecuteScriptAsync(string connectionString, string script)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // WHY: GO is a client batch separator, not T-SQL. One connection preserves script transactions and variables.
        foreach (var batch in System.Text.RegularExpressions.Regex.Split(script, @"(?m)^GO\s*$"))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = SafeMigrationDbContext.CommandTimeoutSeconds;
            await command.ExecuteNonQueryAsync();
        }
    }
}
