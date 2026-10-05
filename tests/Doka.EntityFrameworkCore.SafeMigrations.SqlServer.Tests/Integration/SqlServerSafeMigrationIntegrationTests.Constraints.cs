namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Rejects removal of a principal table after an earlier operation introduced a dependent foreign key.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ProjectedForeignKey_PreventsLaterPrincipalTableDrop()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.parents (Id int NOT NULL CONSTRAINT PK_parents PRIMARY KEY); "
            + "CREATE TABLE dbo.children (Id int NOT NULL, ParentId int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "FK_children_parents",
            "children",
            ["ParentId"],
            "parents",
            ["Id"]);
        builder.DropTableIfExists("parents");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-projected-fk"));

        var parentCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.parents', N'U');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(1, parentCount);
    }

    /// <summary>
    /// Requires a physically recreated index after an ordered drop, despite the original catalog match.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DropThenEnsureIndex_DoesNotReuseStaleCatalogMatch()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.index_orders (Id int NOT NULL, Caption nvarchar(80) NULL); "
            + "CREATE INDEX IX_index_orders_Caption ON dbo.index_orders (Caption);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_index_orders_Caption", "index_orders");
        builder.CreateIndexIfNotExists("IX_index_orders_Caption", "index_orders", ["Caption"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-drop-ensure-index"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var indexCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.index_orders', N'U') "
            + "AND name = N'IX_index_orders_Caption';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(1, indexCount);
    }

    /// <summary>
    /// Distinguishes a disabled SQL Server foreign key from a trusted matching key.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DisabledForeignKey_IsNotAcceptedAsMatching()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.fk_parents (Id int NOT NULL CONSTRAINT PK_fk_parents PRIMARY KEY); "
            + "CREATE TABLE dbo.fk_children (Id int NOT NULL, ParentId int NOT NULL); "
            + "ALTER TABLE dbo.fk_children ADD CONSTRAINT FK_fk_children_parents "
            + "FOREIGN KEY (ParentId) REFERENCES dbo.fk_parents (Id); "
            + "ALTER TABLE dbo.fk_children NOCHECK CONSTRAINT FK_fk_children_parents;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "FK_fk_children_parents",
            "fk_children",
            ["ParentId"],
            "fk_parents",
            ["Id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-disabled-fk"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.NotEqual(SafeMigrationObservedState.Matching, Assert.Single(report.Assessments).ObservedState);
    }

    /// <summary>
    /// Rejects a filtered index when the expected model requires an unfiltered index.
    /// </summary>
    [SqlServerLiveFact]
    public async Task FilteredIndex_IsDifferentFromUnfilteredIndex()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.filtered_orders (Id int NOT NULL, Caption nvarchar(80) NULL); "
            + "CREATE INDEX IX_filtered_orders_Caption ON dbo.filtered_orders (Caption) "
            + "WHERE Caption IS NOT NULL;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("IX_filtered_orders_Caption", "filtered_orders", ["Caption"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-filtered-index"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
    }

    /// <summary>
    /// Adds primary, unique, check, and foreign-key constraints once and safely replays them.
    /// </summary>
    [SqlServerLiveFact]
    public async Task AddConstraints_AppliesAndReplaysEachConstraintFamily()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.add_parents (Id int NOT NULL); "
            + "CREATE TABLE dbo.add_children (Id int NOT NULL, ParentId int NOT NULL, Amount int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddPrimaryKeyIfNotExists("PK_add_parents", "add_parents", ["Id"]);
        builder.AddUniqueConstraintIfNotExists("UQ_add_children_Id", "add_children", ["Id"]);
        builder.AddCheckConstraintIfNotExists("CK_add_children_Amount", "add_children", "[Amount] >= 0");
        builder.AddForeignKeyIfNotExists(
            "FK_add_children_parents",
            "add_children",
            ["ParentId"],
            "add_parents",
            ["Id"]);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var constraintCount = await ScalarIntAsync(
            connectionString,
            "SELECT (SELECT COUNT(*) FROM sys.key_constraints WHERE name = N'PK_add_parents') "
            + "+ (SELECT COUNT(*) FROM sys.key_constraints WHERE name = N'UQ_add_children_Id') "
            + "+ (SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'CK_add_children_Amount' "
            + "AND is_disabled = 0 AND is_not_trusted = 0) "
            + "+ (SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_add_children_parents' "
            + "AND is_disabled = 0 AND is_not_trusted = 0);");

        // Assert
        Assert.Equal(4, constraintCount);
    }

    /// <summary>
    /// Drops each physical constraint family once and treats the absent state as replay-safe.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DropConstraints_AppliesAndReplaysEachConstraintFamily()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.drop_pk (Id int NOT NULL CONSTRAINT PK_drop_pk PRIMARY KEY); "
            + "CREATE TABLE dbo.drop_uq (Id int NOT NULL CONSTRAINT UQ_drop_uq UNIQUE); "
            + "CREATE TABLE dbo.drop_ck (Amount int NOT NULL CONSTRAINT CK_drop_ck CHECK (Amount >= 0)); "
            + "CREATE TABLE dbo.drop_fk_parent (Id int NOT NULL CONSTRAINT PK_drop_fk_parent PRIMARY KEY); "
            + "CREATE TABLE dbo.drop_fk_child (ParentId int NOT NULL); "
            + "ALTER TABLE dbo.drop_fk_child ADD CONSTRAINT FK_drop_fk_child_parent "
            + "FOREIGN KEY (ParentId) REFERENCES dbo.drop_fk_parent (Id);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropPrimaryKeyIfExists("PK_drop_pk", "drop_pk");
        builder.DropUniqueConstraintIfExists("UQ_drop_uq", "drop_uq");
        builder.DropCheckConstraintIfExists("CK_drop_ck", "drop_ck");
        builder.DropForeignKeyIfExists("FK_drop_fk_child_parent", "drop_fk_child");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var remainingCount = await ScalarIntAsync(
            connectionString,
            "SELECT (SELECT COUNT(*) FROM sys.key_constraints WHERE name IN (N'PK_drop_pk', N'UQ_drop_uq')) "
            + "+ (SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'CK_drop_ck') "
            + "+ (SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_drop_fk_child_parent');");

        // Assert
        Assert.Equal(0, remainingCount);
    }
}
