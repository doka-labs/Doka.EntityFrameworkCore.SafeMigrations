namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Keeps an explicitly qualified rename in its source schema under a non-dbo default.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ExplicitSchemaRename_NonDboDefaultKeepsSchemaAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "EXEC sys.sp_executesql N'CREATE SCHEMA application;'; "
            + "EXEC sys.sp_executesql N'CREATE SCHEMA caller_default;'; "
            + "CREATE USER schema_runner WITHOUT LOGIN WITH DEFAULT_SCHEMA = caller_default; "
            + "GRANT CONTROL TO schema_runner; "
            + "CREATE TABLE application.old_orders (Id int NOT NULL); "
            + "INSERT INTO application.old_orders VALUES (19);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER = N'schema_runner';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("old_orders", "renamed_orders", schema: "application");

        // Act
        var initial = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-qualified-rename"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-qualified-rename-replay"));

        await context.Database.ExecuteSqlRawAsync("REVERT;");
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM application.renamed_orders WHERE Id = 19;");

        var wrongSchemaCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'renamed_orders' "
            + "AND schema_id <> SCHEMA_ID(N'application');");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(initial.Assessments).Action);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(1, rows);
        Assert.Equal(0, wrongSchemaCount);
    }

    /// <summary>An explicit schema transfer is independent of a different caller default and replays safely.</summary>
    [SqlServerLiveFact]
    public async Task ExplicitSchemaTransfer_NonDboDefaultUsesCapturedDestinationAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "EXEC sys.sp_executesql N'CREATE SCHEMA application;'; "
            + "EXEC sys.sp_executesql N'CREATE SCHEMA destination;'; "
            + "EXEC sys.sp_executesql N'CREATE SCHEMA caller_default;'; "
            + "CREATE USER schema_runner WITHOUT LOGIN WITH DEFAULT_SCHEMA = caller_default; "
            + "GRANT CONTROL TO schema_runner; "
            + "CREATE TABLE application.orders (Id int NOT NULL); "
            + "INSERT INTO application.orders VALUES (19);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER = N'schema_runner';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("orders", schema: "application", newSchema: "destination");

        // Act
        var initial = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-qualified-transfer"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-qualified-transfer-replay"));

        await context.Database.ExecuteSqlRawAsync("REVERT;");
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM destination.orders WHERE Id = 19;");

        var wrongSchemaCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'orders' "
            + "AND schema_id <> SCHEMA_ID(N'destination');");

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(initial.Assessments).Action);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(1, rows);
        Assert.Equal(0, wrongSchemaCount);
    }

    /// <summary>
    /// An unchanged explicit table identity stays in its schema and still rejects wrong object kinds.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalExplicitRenamePreservesPhysicalObjectKind(bool sourceIsView)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA application;");
        await ExecuteSqlAsync(connectionString, sourceIsView
            ? "CREATE VIEW application.orders AS SELECT 19 AS Id;"
            : "CREATE TABLE application.orders (Id int NOT NULL); INSERT INTO application.orders VALUES (19);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("orders", "orders", schema: "application", newSchema: "application");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-unchanged-explicit-identity"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM application.orders WHERE Id = 19;");

        // Assert
        Assert.Equal(sourceIsView ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, rows);
        if (sourceIsView)
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(exception).Number);
        }
        else
        {
            Assert.Null(exception);
        }
    }

    /// <summary>
    /// Rejects an implicit FK principal schema inside an explicit table under a non-dbo default.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ExplicitTable_ImplicitForeignKeyPrincipalSchemaRejectsBeforeMutation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "EXEC sys.sp_executesql N'CREATE SCHEMA application;'; "
            + "EXEC sys.sp_executesql N'CREATE SCHEMA caller_default;'; "
            + "CREATE USER schema_runner WITHOUT LOGIN WITH DEFAULT_SCHEMA = caller_default; "
            + "GRANT CONTROL TO schema_runner; "
            + "CREATE TABLE dbo.parent_items (Id int NOT NULL PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER = N'schema_runner';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("child_items",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")],
            schema: "application",
            foreignKeys:
            [
                new ExpectedForeignKeyDefinition("FK_child_parent", "child_items", ["ParentId"],
                    "parent_items", ["Id"], schema: "application"),
            ]), SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        await context.Database.ExecuteSqlRawAsync("REVERT;");
        var childCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'child_items';");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("default_schema_mismatch", analysis.Code);
        Assert.Equal(51004, Assert.IsType<SqlException>(exception).Number);
        Assert.Equal(0, childCount);
    }

    /// <summary>
    /// Requires an explicit destination schema before creating or moving a table.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDestinationSchema_EnsureAndRenameRejectPrerequisiteBeforeDdl(bool rename)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.schema_source (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (rename)
        {
            builder.RenameTableIfExists("schema_source", "schema_target", schema: "dbo", newSchema: "missing_schema");
        }
        else
        {
            builder.EnsureTable(new ExpectedTableDefinition("schema_target",
                [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], schema: "missing_schema"),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(
            context, builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var sourceCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.schema_source', N'U');");

        var targetCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'schema_target';");

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, Assert.Single(analyses).ObservedState);
        Assert.Equal(51004, Assert.IsType<SqlException>(exception).Number);
        Assert.Equal(1, sourceCount);
        Assert.Equal(0, targetCount);
    }
}
