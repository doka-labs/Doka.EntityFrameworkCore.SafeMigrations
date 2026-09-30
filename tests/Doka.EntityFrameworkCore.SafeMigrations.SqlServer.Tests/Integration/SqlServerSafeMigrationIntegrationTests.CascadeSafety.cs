namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>A single supported cascade applies, recognizes replay, and performs its DELETE.</summary>
    [SqlServerLiveFact]
    public async Task SingleCascadeAppliesAndDeletesDependentRows()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.cascade_parent (Id int NOT NULL PRIMARY KEY); "
            + "CREATE TABLE dbo.cascade_child (Id int NOT NULL PRIMARY KEY, ParentId int NOT NULL); "
            + "INSERT dbo.cascade_parent VALUES (1); INSERT dbo.cascade_child VALUES (2, 1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "FK_cascade_child_parent", "cascade_child", ["ParentId"], "cascade_parent", ["Id"],
            onDelete: ReferentialAction.Cascade);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("single-cascade"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connectionString, "DELETE dbo.cascade_parent WHERE Id = 1;");
        var children = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.cascade_child;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(0, children);
    }

    /// <summary>Rejects a second same-stream DELETE or UPDATE path before that FK's DDL.</summary>
    [SqlServerLiveTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameStreamCascadePathsAreRejected(bool delete)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.paths_parent (Id int NOT NULL PRIMARY KEY); "
            + "CREATE TABLE dbo.paths_child (Id int NOT NULL PRIMARY KEY, "
            + "FirstId int NOT NULL, SecondId int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var onDelete = delete ? ReferentialAction.Cascade : ReferentialAction.NoAction;
        var onUpdate = delete ? ReferentialAction.NoAction : ReferentialAction.Cascade;

        builder.AddForeignKeyIfNotExists("FK_paths_first", "paths_child", ["FirstId"], "paths_parent", ["Id"],
            onDelete: onDelete, onUpdate: onUpdate);
        builder.AddForeignKeyIfNotExists("FK_paths_second", "paths_child", ["SecondId"], "paths_parent", ["Id"],
            onDelete: onDelete, onUpdate: onUpdate);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("multiple-cascade-paths"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var secondCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_paths_second';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.Contains("doka_sm_prerequisite_missing", Assert.IsType<SqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, secondCount);
    }

    /// <summary>An inline pair of cascades is rejected before creating the child table.</summary>
    [SqlServerLiveFact]
    public async Task InlineMultipleCascadePathsAreRejectedBeforeTableDdl()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.inline_parent (Id int NOT NULL PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("inline_child",
            [new ExpectedColumnDefinition("FirstId", typeof(int), false, "int"),
                new ExpectedColumnDefinition("SecondId", typeof(int), false, "int")],
            foreignKeys:
            [
                new ExpectedForeignKeyDefinition("FK_inline_first", "inline_child", ["FirstId"], "inline_parent",
                    ["Id"], onDelete: ReferentialAction.Cascade),
                new ExpectedForeignKeyDefinition("FK_inline_second", "inline_child", ["SecondId"], "inline_parent",
                    ["Id"], onDelete: ReferentialAction.Cascade),
            ]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("inline-cascade-paths"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var childCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'inline_child';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_prerequisite_missing", Assert.IsType<SqlException>(exception).Message,
            StringComparison.Ordinal);
        Assert.Equal(0, childCount);
    }

    /// <summary>A newly introduced inline dependency prevents a later parent DROP.</summary>
    [SqlServerLiveFact]
    public async Task NewInlineForeignKeyPreventsSameStreamParentDrop()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("new_parent",
            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")],
            primaryKey: new ExpectedPrimaryKeyDefinition("PK_new_parent", "new_parent", ["Id"])),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureTable(new ExpectedTableDefinition("new_child",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")],
            foreignKeys:
            [
                new ExpectedForeignKeyDefinition("FK_new_child_parent", "new_child", ["ParentId"],
                    "new_parent", ["Id"]),
            ]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropTableIfExists("new_parent");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("inline-parent-drop"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[2].Action);
        Assert.Equal("projected_incoming_foreign_key", report.Assessments[2].AnalysisCode);
    }

    /// <summary>Ordered removal of an old cascade permits a replacement with a different physical name.</summary>
    [SqlServerLiveFact]
    public async Task DroppedCascadeDoesNotBlockSameStreamReplacement()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.replace_parent (Id int NOT NULL PRIMARY KEY); "
            + "CREATE TABLE dbo.replace_child (Id int NOT NULL PRIMARY KEY, "
            + "FirstId int NOT NULL, SecondId int NOT NULL, "
            + "CONSTRAINT FK_replace_old FOREIGN KEY (FirstId) REFERENCES dbo.replace_parent(Id) ON DELETE CASCADE);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("FK_replace_old", "replace_child");
        builder.AddForeignKeyIfNotExists("FK_replace_new", "replace_child", ["SecondId"], "replace_parent", ["Id"],
            onDelete: ReferentialAction.Cascade);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("cascade-replacement"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var newCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_replace_new';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(1, newCount);
    }

    /// <summary>Rejects corresponding INSTEAD OF trigger conflicts before a cascading FK is created.</summary>
    [SqlServerLiveTheory]
    [InlineData("DELETE", ReferentialAction.Cascade, ReferentialAction.NoAction)]
    [InlineData("UPDATE", ReferentialAction.NoAction, ReferentialAction.Cascade)]
    public async Task CorrespondingInsteadOfTriggerRejectsCascade(
        string triggerEvent,
        ReferentialAction onDelete,
        ReferentialAction onUpdate
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.trigger_parent (Id int NOT NULL PRIMARY KEY); "
            + "CREATE TABLE dbo.trigger_child (Id int NOT NULL PRIMARY KEY, ParentId int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            $"CREATE TRIGGER dbo.tr_fk_child ON dbo.trigger_child INSTEAD OF {triggerEvent} "
            + "AS BEGIN SET NOCOUNT ON; END;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "FK_trigger_child_parent", "trigger_child", ["ParentId"], "trigger_parent", ["Id"],
            onDelete: onDelete, onUpdate: onUpdate);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("cascade-trigger-conflict"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_prerequisite_missing", Assert.IsType<SqlException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>SET DEFAULT needs either a real default or the nullable implicit default.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task SetDefaultRequiresExplicitOrNullableImplicitDefault(
        bool nullable,
        bool hasDefault,
        bool expectedReady
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_parent (Id int NOT NULL PRIMARY KEY); "
            + "CREATE TABLE dbo.default_child (Id int NOT NULL PRIMARY KEY, ParentId int "
            + (nullable ? "NULL" : "NOT NULL") + (hasDefault ? " DEFAULT (1)" : string.Empty) + ");");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "FK_default_child_parent", "default_child", ["ParentId"], "default_parent", ["Id"],
            onDelete: ReferentialAction.SetDefault);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("cascade-set-default"));

        // Assert
        Assert.Equal(expectedReady ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(expectedReady ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            Assert.Single(report.Assessments).Action);
    }

    /// <summary>A referenced rowversion cannot participate in a mutating referential action.</summary>
    [SqlServerLiveFact]
    public async Task RowVersionCascadeRejectsBeforeProviderDdl()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.version_parent (Version rowversion NOT NULL PRIMARY KEY); "
            + "CREATE TABLE dbo.version_child (Version rowversion NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists(
            "FK_version_child_parent", "version_child", ["Version"], "version_parent", ["Version"],
            onDelete: ReferentialAction.Cascade);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("cascade-rowversion"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_prerequisite_missing", Assert.IsType<SqlException>(exception).Message,
            StringComparison.Ordinal);
    }

    /// <summary>Rowversion FKs without a mutating referential action remain supported.</summary>
    [SqlServerLiveFact]
    public async Task RowVersionNoActionForeignKeyRemainsSupported()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.noaction_version_parent (Version rowversion NOT NULL PRIMARY KEY); "
            + "CREATE TABLE dbo.noaction_version_child (Version rowversion NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("FK_noaction_version_child_parent", "noaction_version_child", ["Version"],
            "noaction_version_parent", ["Version"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("rowversion-no-action"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var count = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'FK_noaction_version_child_parent';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(1, count);
    }

    /// <summary>Pins SQL Server's self-cascade rejection and the corresponding guarded failure.</summary>
    [SqlServerLiveFact]
    public async Task SelfCascadeMatchesServerTopologyBoundary()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.self_cascade (Id int NOT NULL PRIMARY KEY, ParentId int NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddForeignKeyIfNotExists("FK_self_guarded", "self_cascade", ["ParentId"], "self_cascade", ["Id"],
            onDelete: ReferentialAction.Cascade);

        // Act
        var serverException = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "ALTER TABLE dbo.self_cascade ADD CONSTRAINT FK_self_vendor FOREIGN KEY (ParentId) "
            + "REFERENCES dbo.self_cascade(Id) ON DELETE CASCADE;"));

        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("self-cascade-boundary"));

        var guardedException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Contains(Assert.IsType<SqlException>(serverException).Errors.Cast<SqlError>(),
            static error => error.Number == 1785);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, Assert.Single(report.Assessments).Action);
        Assert.Contains("doka_sm_prerequisite_missing", Assert.IsType<SqlException>(guardedException).Message,
            StringComparison.Ordinal);
    }
}
