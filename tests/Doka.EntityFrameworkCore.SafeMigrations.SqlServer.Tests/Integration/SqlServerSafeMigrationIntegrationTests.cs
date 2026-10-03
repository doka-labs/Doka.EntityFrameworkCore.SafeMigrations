namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies SQL Server catalog decisions against live database state.
/// </summary>
[Collection(SqlServerSharedContainer.Name)]
public sealed partial class SqlServerSafeMigrationIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>
    /// Creates the integration suite with a dedicated SQL Server container.
    /// </summary>
    public SqlServerSafeMigrationIntegrationTests(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// Ensures a missing strict table, safely repeats the operation, and verifies postflight.
    /// </summary>
    [SqlServerLiveFact]
    public async Task StrictTable_AppliesReplaysAndPassesPostflight()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "strict_orders",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Caption = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_strict_orders", row => row.Id));
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-strict-create"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-strict-postflight"));

        var tableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.strict_orders', N'U');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(1, tableCount);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
    }

    /// <summary>
    /// Rejects a different strict table before the generator can mutate it.
    /// </summary>
    [SqlServerLiveFact]
    public async Task StrictTable_RejectsDifferentDefinitionWithoutMutation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.strict_drift (Id int NOT NULL, Caption nvarchar(20) NOT NULL); ");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "strict_drift",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Caption = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
            });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-strict-drift"));

        var actualLength = await ScalarIntAsync(
            connectionString,
            "SELECT max_length / 2 FROM sys.columns "
            + "WHERE object_id = OBJECT_ID(N'dbo.strict_drift', N'U') AND name = N'Caption';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(20, actualLength);
    }

    /// <summary>
    /// Completes a legacy table using independent column and index operations.
    /// </summary>
    [SqlServerLiveFact]
    public async Task LegacyContainer_AddsMissingColumnAndIndexWithoutChangingExistingRows()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.legacy_orders (Id int NOT NULL PRIMARY KEY); "
            + "INSERT INTO dbo.legacy_orders (Id) VALUES (7);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "legacy_orders",
            table => new { Id = table.Column<int>(type: "int", nullable: false) },
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);
        builder.AddColumnIfNotExists<string>(
            "Caption",
            "legacy_orders",
            type: "nvarchar(80)",
            maxLength: 80,
            nullable: true);
        builder.CreateIndexIfNotExists("IX_legacy_orders_Caption", "legacy_orders", ["Caption"]);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.legacy_orders WHERE Id = 7;");
        var columnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns "
            + "WHERE object_id = OBJECT_ID(N'dbo.legacy_orders', N'U') AND name = N'Caption';");

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes "
            + "WHERE object_id = OBJECT_ID(N'dbo.legacy_orders', N'U') "
            + "AND name = N'IX_legacy_orders_Caption';");

        // Assert
        Assert.Equal(1, rowCount);
        Assert.Equal(1, columnCount);
        Assert.Equal(1, indexCount);
    }

    /// <summary>
    /// Keeps an explicit non-dbo schema distinct from the default schema.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ExplicitSchema_CreatesOnlyQualifiedTableAndReplays()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureSchemaExists("application");
        builder.CreateTableIfNotExists(
            "qualified_orders",
            table => new { Id = table.Column<int>(type: "int", nullable: false) },
            schema: "application");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var qualifiedCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'application.qualified_orders', N'U');");

        var defaultCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.qualified_orders', N'U');");

        // Assert
        Assert.Equal(1, qualifiedCount);
        Assert.Equal(0, defaultCount);
    }
}
