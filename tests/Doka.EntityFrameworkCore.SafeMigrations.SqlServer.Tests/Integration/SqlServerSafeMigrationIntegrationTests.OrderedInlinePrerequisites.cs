namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>A live inline proof cannot survive removal of its table or only candidate key.</summary>
    [SqlServerLiveTheory]
    [InlineData("table")]
    [InlineData("primary")]
    [InlineData("unique")]
    [InlineData("index")]
    public async Task RemovedPrincipalPrerequisiteBlocksLaterInlineForeignKey(string removal)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var key = removal switch
        {
            "unique" => "CONSTRAINT UQ_inline_principal UNIQUE (Id)",
            "index" => string.Empty,
            _ => "CONSTRAINT PK_inline_principal PRIMARY KEY (Id)",
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL"
            + (key.Length == 0 ? string.Empty : ", " + key) + ");"
            + (removal == "index"
                ? " CREATE UNIQUE INDEX IX_inline_principal ON dbo.inline_principal(Id);"
                : string.Empty));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        switch (removal)
        {
            case "table":
                builder.DropTableIfExists("inline_principal");
                break;
            case "primary":
                builder.DropPrimaryKeyIfExists("PK_inline_principal", "inline_principal");
                break;
            case "unique":
                builder.DropUniqueConstraintIfExists("UQ_inline_principal", "inline_principal");
                break;
            case "index":
                builder.DropIndexIfExists("IX_inline_principal", "inline_principal");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(removal));
        }

        builder.EnsureTable(InlineDependent("inline_principal"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("removed-inline-prerequisite"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
    }

    /// <summary>An inline FK follows the accepted physical principal rename, not its old name.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InlineForeignKeyUsesCurrentPrincipalAfterRename(
        bool useNewName,
        bool expectedReady
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL CONSTRAINT PK_inline_principal PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("inline_principal", "renamed_principal");
        builder.EnsureTable(InlineDependent(useNewName ? "renamed_principal" : "inline_principal"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("renamed-inline-principal"));

        // Assert
        Assert.Equal(expectedReady ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(expectedReady ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[1].Action);
    }

    /// <summary>Only the final accepted name retains an inline FK's physical principal proof.</summary>
    [SqlServerLiveTheory]
    [InlineData("inline_principal", false)]
    [InlineData("intermediate_principal", false)]
    [InlineData("renamed_principal", true)]
    public async Task InlineForeignKeyUsesCurrentPrincipalAfterRepeatedRenames(
        string principalTable,
        bool expectedReady
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL CONSTRAINT PK_inline_principal PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("inline_principal", "intermediate_principal");
        builder.RenameTableIfExists("intermediate_principal", "renamed_principal");
        builder.EnsureTable(InlineDependent(principalTable),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("repeated-renamed-inline-principal"));

        // Assert
        Assert.Equal(expectedReady ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.All(report.Assessments.Take(2),
            assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(expectedReady ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[2].Action);
    }

    /// <summary>An occupied rename target cannot activate the original principal's captured proof.</summary>
    [SqlServerLiveFact]
    public async Task RejectedPrincipalRenameDoesNotActivateInlineStorageProof()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL CONSTRAINT PK_inline_principal PRIMARY KEY); "
            + "CREATE TABLE dbo.renamed_principal (Id bigint NOT NULL CONSTRAINT PK_occupied_principal PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("inline_principal", "renamed_principal");
        builder.EnsureTable(InlineDependent("renamed_principal"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("rejected-renamed-inline-principal"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
    }

    /// <summary>A prior accepted rename cannot hide a table or view occupying the next target name.</summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedPrincipalRenameRetainsTargetObjectKindGuard(bool targetIsView)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL CONSTRAINT PK_inline_principal PRIMARY KEY);");
        await ExecuteSqlAsync(connectionString, targetIsView
            ? "CREATE VIEW dbo.occupied_principal AS SELECT 1 AS Id;"
            : "CREATE TABLE dbo.occupied_principal (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("inline_principal", "intermediate_principal");
        builder.RenameTableIfExists("intermediate_principal", "occupied_principal");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordered-rename-target-kind"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Equal("projected_rename_target_occupied", report.Assessments[1].AnalysisCode);
    }

    /// <summary>Opaque SQL defers a renamed principal's proof, which the runtime guard safely re-establishes.</summary>
    [SqlServerLiveFact]
    public async Task OpaqueSqlAfterRenameCannotRetainInlinePrerequisiteProof()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL CONSTRAINT PK_inline_principal PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("inline_principal", "renamed_principal");
        builder.Sql("SELECT 1;");
        builder.EnsureTable(InlineDependent("renamed_principal"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("opaque-sql-invalidates-inline-rename"));

        var preflightFailure = Record.Exception(report.ThrowIfBlocked);
        var preflightChildCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.inline_dependent',N'U');");

        await ExecuteOperationsAsync(context, builder.Operations);
        var foreignKeyCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE name=N'FK_inline_dependent' "
            + "AND parent_object_id=OBJECT_ID(N'dbo.inline_dependent',N'U') "
            + "AND referenced_object_id=OBJECT_ID(N'dbo.renamed_principal',N'U') "
            + "AND is_disabled=0 AND is_not_trusted=0;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.Equal("projected_structure_state_unknown", report.Assessments[^1].AnalysisCode);
        Assert.Null(report.Assessments[^1].ObservedState);
        Assert.Null(report.Assessments[^1].PostconditionSatisfied);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[^1].DeferredOrigin);

        Assert.Equal(1, origin.OperationOrdinal);
        Assert.Equal(typeof(SqlOperation).FullName, origin.OperationType);
        Assert.Null(origin.MigrationId);
        Assert.Null(preflightFailure);
        Assert.Equal(0, preflightChildCount);
        Assert.Equal(1, foreignKeyCount);
    }

    /// <summary>Opaque removal is deferred but the runtime guard rejects the missing inline prerequisite.</summary>
    /// <param name="dropTable">Whether opaque SQL removes the whole principal instead of its only key.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpaqueSqlAfterRenameRejectsRemovedInlinePrerequisiteBeforeChildCreation(bool dropTable)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL CONSTRAINT PK_inline_principal PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("inline_principal", "renamed_principal");
        builder.Sql(dropTable ? "DROP TABLE dbo.renamed_principal;"
            : "ALTER TABLE dbo.renamed_principal DROP CONSTRAINT PK_inline_principal;");
        builder.EnsureTable(InlineDependent("renamed_principal"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("opaque-sql-removes-inline-prerequisite"));

        var preflightFailure = Record.Exception(report.ThrowIfBlocked);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var childCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.inline_dependent',N'U');");

        var principalCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.renamed_principal',N'U');");

        var keyCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.key_constraints "
            + "WHERE parent_object_id=OBJECT_ID(N'dbo.renamed_principal',N'U');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.Equal("projected_structure_state_unknown", report.Assessments[^1].AnalysisCode);
        Assert.Null(report.Assessments[^1].ObservedState);
        Assert.Null(report.Assessments[^1].PostconditionSatisfied);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[^1].DeferredOrigin);

        Assert.Equal(1, origin.OperationOrdinal);
        Assert.Equal(typeof(SqlOperation).FullName, origin.OperationType);
        Assert.Null(origin.MigrationId);
        Assert.Null(preflightFailure);
        Assert.Equal(51004, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(0, childCount);
        Assert.Equal(dropTable ? 0 : 1, principalCount);
        Assert.Equal(0, keyCount);
    }

    /// <summary>A surviving alternate candidate key retains valid inline prerequisites.</summary>
    [SqlServerLiveFact]
    public async Task DroppedCandidateKeyDoesNotInvalidateSurvivingEquivalentKey()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL CONSTRAINT PK_inline_principal PRIMARY KEY, "
            + "CONSTRAINT UQ_inline_principal UNIQUE (Id));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropPrimaryKeyIfExists("PK_inline_principal", "inline_principal");
        builder.EnsureTable(InlineDependent("inline_principal"),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("surviving-inline-candidate-key"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
    }

    /// <summary>Accepted FK or dependent removal reconciles an incoming-FK-only immutable drop conflict.</summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedIncomingForeignKeyPermitsSameStreamPrincipalDrop(bool dropChild)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.drop_principal (Id int NOT NULL CONSTRAINT PK_drop_principal PRIMARY KEY); "
            + "CREATE TABLE dbo.drop_dependent (ParentId int NOT NULL, "
            + "CONSTRAINT FK_drop_dependent FOREIGN KEY (ParentId) REFERENCES dbo.drop_principal(Id));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        if (dropChild)
        {
            builder.DropTableIfExists("drop_dependent");
        }
        else
        {
            builder.DropForeignKeyIfExists("FK_drop_dependent", "drop_dependent");
        }

        builder.DropTableIfExists("drop_principal");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordered-incoming-fk-drop"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.drop_principal');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(0, remaining);
    }

    /// <summary>Removing an incoming FK cannot remove an independent schema-bound dependency.</summary>
    [SqlServerLiveFact]
    public async Task RemovedIncomingForeignKeyDoesNotBypassOtherDropDependencies()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.drop_principal (Id int NOT NULL CONSTRAINT PK_drop_principal PRIMARY KEY); "
            + "CREATE TABLE dbo.drop_dependent (ParentId int NOT NULL, "
            + "CONSTRAINT FK_drop_dependent FOREIGN KEY (ParentId) REFERENCES dbo.drop_principal(Id));");
        await ExecuteSqlAsync(connectionString,
            "CREATE VIEW dbo.bound_principal WITH SCHEMABINDING AS SELECT Id FROM dbo.drop_principal;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("FK_drop_dependent", "drop_dependent");
        builder.DropTableIfExists("drop_principal");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("independent-drop-dependency"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
    }

    /// <summary>
    /// Catalog-equivalent live spellings retain a candidate key, while a real different column does not.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("id", true)]
    [InlineData("OtherId", false)]
    public async Task InlineForeignKeyMatchesPhysicalPrincipalColumn(
        string principalColumn,
        bool expectedReady
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL CONSTRAINT PK_inline_principal PRIMARY KEY, "
            + "OtherId int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("inline_dependent",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")],
            foreignKeys: [new ExpectedForeignKeyDefinition("FK_inline_dependent", "inline_dependent",
                ["ParentId"], "inline_principal", [principalColumn])]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("inline-physical-column-id"));

        // Assert
        Assert.Equal(expectedReady ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(expectedReady ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[0].Action);
    }

    /// <summary>
    /// Accepted column renames retain physical key membership only under the current principal column.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InlineForeignKeyUsesCurrentPrincipalColumnAfterRename(
        bool useNewName,
        bool expectedReady
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL CONSTRAINT PK_inline_principal PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameColumnIfExists("Id", "inline_principal", "RenamedId");
        builder.EnsureTable(new ExpectedTableDefinition("inline_dependent",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")],
            foreignKeys: [new ExpectedForeignKeyDefinition("FK_inline_dependent", "inline_dependent",
                ["ParentId"], "inline_principal", [useNewName ? "RenamedId" : "Id"])]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("inline-renamed-physical-column-id"));

        // Assert
        Assert.Equal(expectedReady ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(expectedReady ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[1].Action);
    }

    /// <summary>
    /// Inline FKs after accepted table or column renames execute and replay under their final identities.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InlineForeignKeyAfterPrincipalRenameAppliesAndReplays(bool renameColumn)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.inline_principal (Id int NOT NULL CONSTRAINT PK_inline_principal PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (renameColumn)
        {
            builder.RenameColumnIfExists("Id", "inline_principal", "RenamedId");
        }
        else
        {
            builder.RenameTableIfExists("inline_principal", "renamed_principal");
        }

        builder.EnsureTable(new ExpectedTableDefinition("inline_dependent",
            [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")],
            foreignKeys: [new ExpectedForeignKeyDefinition("FK_inline_dependent", "inline_dependent",
                ["ParentId"], renameColumn ? "inline_principal" : "renamed_principal",
                [renameColumn ? "RenamedId" : "Id"])]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("inline-renamed-principal-replay"));

        var foreignKeyCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fkc "
            + "ON fkc.constraint_object_id = fk.object_id "
            + "JOIN sys.columns principal ON principal.object_id = fkc.referenced_object_id "
            + "AND principal.column_id = fkc.referenced_column_id "
            + "WHERE fk.name = N'FK_inline_dependent' AND fk.is_disabled = 0 AND fk.is_not_trusted = 0 "
            + $"AND principal.name = N'{(renameColumn ? "RenamedId" : "Id")}';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(1, foreignKeyCount);
    }

    private static ExpectedTableDefinition InlineDependent(string principalTable)
        => new("inline_dependent", [new ExpectedColumnDefinition("ParentId", typeof(int), false, "int")],
            foreignKeys: [new ExpectedForeignKeyDefinition("FK_inline_dependent", "inline_dependent",
                ["ParentId"], principalTable, ["Id"])]);
}
