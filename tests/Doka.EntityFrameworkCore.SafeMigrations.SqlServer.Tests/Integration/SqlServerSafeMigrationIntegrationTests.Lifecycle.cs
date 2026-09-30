namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Renames an existing table exactly once while preserving its rows.
    /// </summary>
    [SqlServerLiveFact]
    public async Task RenameTable_PreservesRowsAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.old_orders (Id int NOT NULL); "
            + "INSERT INTO dbo.old_orders (Id) VALUES (19);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("old_orders", "renamed_orders");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var rowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.renamed_orders WHERE Id = 19;");

        var oldTableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.old_orders', N'U');");

        // Assert
        Assert.Equal(1, rowCount);
        Assert.Equal(0, oldTableCount);
    }

    /// <summary>
    /// Refuses to rename a table when the target already exists.
    /// </summary>
    [SqlServerLiveFact]
    public async Task RenameTable_TargetCollisionLeavesBothTablesUnchanged()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.rename_source (Id int NOT NULL); "
            + "CREATE TABLE dbo.rename_target (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "rename_target");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-rename-table-collision"));

        var tableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name IN (N'rename_source', N'rename_target');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(2, tableCount);
    }

    /// <summary>
    /// Drops an existing table and treats its absence as a safe replay state.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DropTable_DropsOnceAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.obsolete_orders (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("obsolete_orders");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var tableCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.obsolete_orders', N'U');");

        // Assert
        Assert.Equal(0, tableCount);
    }

    /// <summary>
    /// Renames an existing column without losing its stored value.
    /// </summary>
    [SqlServerLiveFact]
    public async Task RenameColumn_PreservesValueAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.column_orders (Id int NOT NULL, Caption nvarchar(80) NULL); "
            + "INSERT INTO dbo.column_orders (Id, Caption) VALUES (1, N'kept');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameColumnIfExists("Caption", "column_orders", "Title");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var rowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.column_orders WHERE Id = 1 AND Title = N'kept';");

        var oldColumnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns "
            + "WHERE object_id = OBJECT_ID(N'dbo.column_orders', N'U') AND name = N'Caption';");

        // Assert
        Assert.Equal(1, rowCount);
        Assert.Equal(0, oldColumnCount);
    }

    /// <summary>
    /// Refuses a column rename when both source and target columns are already present.
    /// </summary>
    [SqlServerLiveFact]
    public async Task RenameColumn_TargetCollisionLeavesBothColumnsUnchanged()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.rename_column_collision (Id int NOT NULL, OldValue int NULL, NewValue int NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameColumnIfExists("OldValue", "rename_column_collision", "NewValue");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-rename-column-collision"));

        var columnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.rename_column_collision', N'U') "
            + "AND name IN (N'OldValue', N'NewValue');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(2, columnCount);
    }

    /// <summary>
    /// Drops a column only when it exists, preserving its sibling column and rows.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DropColumn_PreservesRemainingRowsAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.drop_column_orders (Id int NOT NULL, Obsolete nvarchar(80) NULL); "
            + "INSERT INTO dbo.drop_column_orders (Id, Obsolete) VALUES (1, N'old');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("Obsolete", "drop_column_orders");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var rowCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.drop_column_orders WHERE Id = 1;");

        var droppedColumnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns "
            + "WHERE object_id = OBJECT_ID(N'dbo.drop_column_orders', N'U') AND name = N'Obsolete';");

        // Assert
        Assert.Equal(1, rowCount);
        Assert.Equal(0, droppedColumnCount);
    }

    /// <summary>
    /// Renames an index once without losing its target key.
    /// </summary>
    [SqlServerLiveFact]
    public async Task RenameIndex_PreservesPhysicalIndexAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.rename_index_orders (Id int NOT NULL); "
            + "CREATE INDEX IX_old_orders_Id ON dbo.rename_index_orders (Id);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameIndexIfExists("IX_old_orders_Id", "rename_index_orders", "IX_new_orders_Id");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var newIndexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes "
            + "WHERE object_id = OBJECT_ID(N'dbo.rename_index_orders', N'U') AND name = N'IX_new_orders_Id';");

        var oldIndexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes "
            + "WHERE object_id = OBJECT_ID(N'dbo.rename_index_orders', N'U') AND name = N'IX_old_orders_Id';");

        // Assert
        Assert.Equal(1, newIndexCount);
        Assert.Equal(0, oldIndexCount);
    }

    /// <summary>
    /// Refuses an index rename when the target name is already occupied on that table.
    /// </summary>
    [SqlServerLiveFact]
    public async Task RenameIndex_TargetCollisionLeavesBothIndexesUnchanged()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.rename_index_collision (Id int NOT NULL); "
            + "CREATE INDEX IX_old_collision ON dbo.rename_index_collision (Id); "
            + "CREATE INDEX IX_new_collision ON dbo.rename_index_collision (Id);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameIndexIfExists("IX_old_collision", "rename_index_collision", "IX_new_collision");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-rename-index-collision"));

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.rename_index_collision', N'U') "
            + "AND name IN (N'IX_old_collision', N'IX_new_collision');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(2, indexCount);
    }

    /// <summary>
    /// Drops a physical index once and treats its absence as a replay-safe state.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DropIndex_DropsOnceAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.drop_index_orders (Id int NOT NULL); "
            + "CREATE INDEX IX_drop_index_orders_Id ON dbo.drop_index_orders (Id);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_drop_index_orders_Id", "drop_index_orders");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.drop_index_orders', N'U') "
            + "AND name = N'IX_drop_index_orders_Id';");

        // Assert
        Assert.Equal(0, indexCount);
    }

    /// <summary>
    /// Rejects a narrowing alter when existing text would be truncated.
    /// </summary>
    [SqlServerLiveFact]
    public async Task AlterColumn_RejectsDataLossBeforeMutation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.alter_orders (Id int NOT NULL, Caption nvarchar(80) NOT NULL); "
            + "INSERT INTO dbo.alter_orders (Id, Caption) VALUES (1, N'longer-than-ten-characters');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferentFromModel(
            operation => operation.AlterColumn<string>(
                "Caption",
                "alter_orders",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(80)",
                oldMaxLength: 80),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-alter-data-loss"));

        var actualLength = await ScalarIntAsync(
            connectionString,
            "SELECT max_length / 2 FROM sys.columns "
            + "WHERE object_id = OBJECT_ID(N'dbo.alter_orders', N'U') AND name = N'Caption';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(80, actualLength);
    }

    /// <summary>
    /// Adds a required column without a default only when the table is empty.
    /// </summary>
    [SqlServerLiveFact]
    public async Task AddRequiredColumn_EmptyTableAppliesAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.empty_add_orders (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>(
            "RequiredValue",
            "empty_add_orders",
            type: "int",
            nullable: false,
            policy: SafeMigrationPolicy.RepairIfSafe);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var requiredColumnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.empty_add_orders', N'U') "
            + "AND name = N'RequiredValue' AND is_nullable = 0;");

        // Assert
        Assert.Equal(1, requiredColumnCount);
    }

    /// <summary>
    /// Rejects a required column without a default when existing rows need a value.
    /// </summary>
    [SqlServerLiveFact]
    public async Task AddRequiredColumn_PopulatedTableIsDataBlocked()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.populated_add_orders (Id int NOT NULL); "
            + "INSERT INTO dbo.populated_add_orders (Id) VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>(
            "RequiredValue",
            "populated_add_orders",
            type: "int",
            nullable: false,
            policy: SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-populated-required"));

        var requiredColumnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.populated_add_orders', N'U') "
            + "AND name = N'RequiredValue';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(0, requiredColumnCount);
    }

    /// <summary>
    /// Drops an empty explicitly named schema and safely repeats the drop.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DropSchema_DropsOnceAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA obsolete;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropSchemaIfExists("obsolete");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var schemaCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.schemas WHERE name = N'obsolete';");

        // Assert
        Assert.Equal(0, schemaCount);
    }
}
