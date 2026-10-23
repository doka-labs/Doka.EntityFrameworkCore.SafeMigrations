namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies ordinary index removal before isolated integer widening and replay.</summary>
public sealed class SqlServerIndexDropColumnTransitionIntegrationTests : SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Uses the shared container while retaining per-case database ownership.</summary>
    /// <param name="fixture">The assembly-owned engine fixture.</param>
    public SqlServerIndexDropColumnTransitionIntegrationTests(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>Present and interrupted index removal preserve exact source proofs and idempotent upgrades.</summary>
    /// <param name="indexExists">Whether the removable index remains before the upgrade resumes.</param>
    [SqlServerLiveTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrdinaryIndexDropWideningAppliesAndReplays(bool indexExists)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.index_drop_values (Value int NOT NULL); "
            + "INSERT dbo.index_drop_values VALUES (-2147483648), (2147483647);"
            + (indexExists ? " CREATE INDEX IX_upgrade_value ON dbo.index_drop_values(Value);" : string.Empty));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_upgrade_value", "index_drop_values");
        builder.AlterColumnIfDifferent("index_drop_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-index-drop-widening-upgrade"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-index-drop-widening-replay"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var verified = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-index-drop-widening-postflight"));

        var widened = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.index_drop_values') "
            + "AND name=N'Value' AND system_type_id=127 AND is_nullable=0;");

        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.index_drop_values') "
            + "AND name=N'IX_upgrade_value';");

        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.index_drop_values WHERE Value IN(-2147483648,2147483647);");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(indexExists ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, verified.Status);
        Assert.Equal(1, widened);
        Assert.Equal(0, indexes);
        Assert.Equal(2, retained);
    }

    /// <summary>Index removal does not erase old-definition, stored-NULL or remaining-dependency boundaries.</summary>
    /// <param name="control">The independently invalid source or physical dependency condition.</param>
    [SqlServerLiveTheory]
    [InlineData("stale-source")]
    [InlineData("null-row")]
    [InlineData("remaining-index")]
    public async Task OrdinaryIndexDropRetainsWideningSafetyBoundaries(string control)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.index_drop_values (Value int NULL); "
            + "INSERT dbo.index_drop_values VALUES (" + (control == "null-row" ? "NULL" : "2147483647") + "); "
            + "CREATE INDEX IX_upgrade_value ON dbo.index_drop_values(Value);"
            + (control == "remaining-index"
                ? " CREATE INDEX IX_retained_value ON dbo.index_drop_values(Value);" : string.Empty));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_upgrade_value", "index_drop_values");
        builder.AlterColumnIfDifferent("index_drop_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", control == "stale-source" ? typeof(short) : typeof(int), true,
                control == "stale-source" ? "smallint" : "int"), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-index-drop-invalid-upgrade"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var source = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.index_drop_values') "
            + "AND name=N'Value' AND system_type_id=56 AND is_nullable=1;");

        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.index_drop_values WHERE "
            + (control == "null-row" ? "Value IS NULL;" : "Value=2147483647;"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.True(report.Assessments[1].Action?.RejectsExecution());
        Assert.Contains("doka_sm_", Assert.IsType<SqlException>(failure).Message, StringComparison.Ordinal);
        Assert.Equal(1, source);
        Assert.Equal(1, retained);
    }
}
