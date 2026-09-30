namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Only an accepted schema ensure permits a following table in a new schema.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task NewSchemaTableRequiresPrecedingAcceptedEnsure(
        bool ensureSchema,
        bool expectedReady
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (ensureSchema)
        {
            builder.EnsureSchemaExists("new_application");
        }

        builder.EnsureTable(new ExpectedTableDefinition("ordered_items",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], schema: "new_application"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordered-schema-create"));

        // Assert
        Assert.Equal(expectedReady ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(expectedReady ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[^1].Action);
    }

    /// <summary>A preceding accepted schema DROP removes the table creation prerequisite.</summary>
    [SqlServerLiveFact]
    public async Task DroppedSchemaCannotAuthorizeFollowingTable()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA removed_application;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropSchemaIfExists("removed_application");
        builder.EnsureTable(new ExpectedTableDefinition("ordered_items",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], schema: "removed_application"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordered-schema-drop"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
    }

    /// <summary>A schema created earlier in the stream permits an explicit table transfer.</summary>
    [SqlServerLiveFact]
    public async Task NewlyEnsuredSchemaPermitsTableTransfer()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.transfer_items (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureSchemaExists("transfer_application");
        builder.RenameTableIfExists("transfer_items", schema: "dbo", newSchema: "transfer_application");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordered-schema-transfer"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var tableCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'transfer_application.transfer_items');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(1, tableCount);
    }

    /// <summary>A dropped live schema must be recreated before a following table is added.</summary>
    [SqlServerLiveFact]
    public async Task SchemaDropThenEnsureUsesOrderedPresence()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA restored_application;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropSchemaIfExists("restored_application");
        builder.EnsureSchemaExists("restored_application");
        builder.EnsureTable(new ExpectedTableDefinition("restored_items",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], schema: "restored_application"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("schema-drop-then-ensure"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var tableCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'restored_application.restored_items');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(1, tableCount);
    }

    /// <summary>A schema absent in the immutable snapshot is not empty after its accepted table creation.</summary>
    [SqlServerLiveFact]
    public async Task EnsuredAndPopulatedSchemaRejectsLaterDrop()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureSchemaExists("populated_application");
        builder.EnsureTable(new ExpectedTableDefinition("items",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")], schema: "populated_application"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropSchemaIfExists("populated_application");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ensured-schema-is-not-empty"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[2].Action);
        Assert.Equal("projected_schema_not_empty", report.Assessments[2].AnalysisCode);
    }

    /// <summary>Removing a schema's last table makes the following schema drop valid.</summary>
    [SqlServerLiveFact]
    public async Task DroppingLastTableMakesSchemaEmpty()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA emptied_application;");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE emptied_application.items (Id int NOT NULL CONSTRAINT PK_emptied_items PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("items", "emptied_application");
        builder.DropSchemaIfExists("emptied_application");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("table-drop-then-empty-schema"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var schemaCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.schemas WHERE name = N'emptied_application';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(0, schemaCount);
    }

    /// <summary>An accepted transfer changes the source and destination schema occupancy.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SchemaDropUsesCurrentTransferredTableLocation(
        bool dropSource,
        bool expectedReady
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA source_application;");
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA target_application;");
        await ExecuteSqlAsync(connectionString, "CREATE TABLE source_application.items (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("items", schema: "source_application", newSchema: "target_application");
        builder.DropSchemaIfExists(dropSource ? "source_application" : "target_application");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("transferred-schema-occupancy"));

        // Assert
        Assert.Equal(expectedReady ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(expectedReady ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent,
            report.Assessments[1].Action);
    }

    /// <summary>Other schema-scoped objects remain blockers even after the last resident table is dropped.</summary>
    [SqlServerLiveTheory]
    [InlineData("CREATE VIEW guarded_application.remaining AS SELECT 1 AS Id;")]
    [InlineData("CREATE TYPE guarded_application.RemainingId FROM int;")]
    [InlineData("CREATE SEQUENCE guarded_application.remaining AS int;")]
    public async Task LastTableDropCannotRemoveOtherSchemaObjects(string createOtherObject)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA guarded_application;");
        await ExecuteSqlAsync(connectionString, "CREATE TABLE guarded_application.items (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString, createOtherObject);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("items", "guarded_application");
        builder.DropSchemaIfExists("guarded_application");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("schema-non-table-object-guard"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
    }
}
