namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Exercises SQL Server-specific model-managed data boundaries against an isolated database.
/// </summary>
[Collection(SqlServerSharedContainer.Name)]
public sealed class SqlServerModelManagedDataIntegrationTests : SqlServerIntegrationTestBase
{
    /// <summary>
    /// Creates the suite with an isolated SQL Server container.
    /// </summary>
    public SqlServerModelManagedDataIntegrationTests(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// Inserts explicit identity keys and keeps exact replay idempotent.
    /// </summary>
    [SqlServerLiveFact]
    public async Task IdentitySeed_AppliesAndReplaysWithoutLeakingIdentityInsert()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_roles (Id int IDENTITY(1,1) NOT NULL "
            + "CONSTRAINT PK_identity_roles PRIMARY KEY, Caption nvarchar(80) NOT NULL); "
            + "CREATE TABLE dbo.identity_probe (Id int IDENTITY(1,1) NOT NULL "
            + "CONSTRAINT PK_identity_probe PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "identity_roles", ["Id"], ["int"], ["Id", "Caption"], ["int", "nvarchar(80)"],
            new object?[,] { { 7, "Administrator" } });
        await context.Database.OpenConnectionAsync();

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await context.Database.ExecuteSqlRawAsync(
            "SET IDENTITY_INSERT dbo.identity_probe ON; "
            + "INSERT INTO dbo.identity_probe (Id) VALUES (11); "
            + "SET IDENTITY_INSERT dbo.identity_probe OFF;");
        var count = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.identity_roles WHERE Id = 7 AND Caption = N'Administrator';");

        var probeCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.identity_probe WHERE Id = 11;");

        await context.Database.CloseConnectionAsync();

        // Assert
        Assert.Equal(1, count);
        Assert.Equal(1, probeCount);
    }

    /// <summary>
    /// Restores session-scoped IDENTITY_INSERT after a failed managed-data insert.
    /// </summary>
    [SqlServerLiveFact]
    public async Task IdentitySeed_DmlFailureRestoresSessionState()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_reject (Id int IDENTITY(1,1) NOT NULL "
            + "CONSTRAINT PK_identity_reject PRIMARY KEY, Caption nvarchar(80) NOT NULL "
            + "CONSTRAINT CK_identity_reject_Caption CHECK (Caption <> N'bad')); "
            + "CREATE TABLE dbo.identity_probe (Id int IDENTITY(1,1) NOT NULL "
            + "CONSTRAINT PK_identity_probe PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "identity_reject", ["Id"], ["int"], ["Id", "Caption"], ["int", "nvarchar(80)"],
            new object?[,] { { 9, "bad" } });
        await context.Database.OpenConnectionAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        await context.Database.ExecuteSqlRawAsync(
            "SET IDENTITY_INSERT dbo.identity_probe ON; "
            + "INSERT INTO dbo.identity_probe (Id) VALUES (11); "
            + "SET IDENTITY_INSERT dbo.identity_probe OFF;");
        var rejectedCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.identity_reject;");
        var probeCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.identity_probe WHERE Id = 11;");

        await context.Database.CloseConnectionAsync();

        // Assert
        Assert.IsType<SqlException>(exception);
        Assert.Equal(0, rejectedCount);
        Assert.Equal(1, probeCount);
    }

    /// <summary>
    /// Treats a case-only content edit as model-managed drift under a CI collation.
    /// </summary>
    [SqlServerLiveFact]
    public async Task CaseInsensitiveCollation_DoesNotHideDifferentStoredContent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.collated_roles (Id int NOT NULL CONSTRAINT PK_collated_roles PRIMARY KEY, "
            + "Caption nvarchar(80) COLLATE Latin1_General_100_CI_AS NOT NULL); "
            + "INSERT INTO dbo.collated_roles (Id, Caption) VALUES (1, N'administrator');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "collated_roles", ["Id"], ["int"], ["Id", "Caption"], ["int", "nvarchar(80)"],
            new object?[,] { { 1, "Administrator" } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-collation-drift"));

        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.collated_roles "
            + "WHERE Id = 1 AND CONVERT(varbinary(max), Caption) = CONVERT(varbinary(max), N'administrator');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, unchanged);
    }

    /// <summary>
    /// Blocks a new NULL value that conflicts with an existing SQL Server unique key.
    /// </summary>
    [SqlServerLiveFact]
    public async Task NullableUniqueKey_RejectsDuplicateNullTarget()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.unique_roles (Id int NOT NULL CONSTRAINT PK_unique_roles PRIMARY KEY, "
            + "Code nvarchar(40) NULL CONSTRAINT UQ_unique_roles_Code UNIQUE); "
            + "INSERT INTO dbo.unique_roles (Id, Code) VALUES (1, NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "unique_roles", ["Id"], ["int"], ["Id", "Code"], ["int", "nvarchar(40)"],
            new object?[,] { { 2, null } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-null-unique"));

        var count = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.unique_roles;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, count);
    }

    /// <summary>
    /// Rejects two source keys that collide only under the database collation.
    /// </summary>
    [SqlServerLiveFact]
    public async Task CaseInsensitiveKeys_RejectSourceBatchCollision()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.collated_keys (Code nvarchar(40) "
            + "NOT NULL CONSTRAINT PK_collated_keys PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "collated_keys", ["Code"], ["nvarchar(40)"], ["Code"], ["nvarchar(40)"],
            new object?[,] { { "Foo" }, { "foo" } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-key-collision"));

        var count = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.collated_keys;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// Rejects a multi-row source comparison when key and database collations differ.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ExplicitKeyCollation_RejectsUnprovenSourceBatchComparison()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.explicit_collation_keys (Code nvarchar(40) "
            + "COLLATE Latin1_General_100_BIN2 NOT NULL "
            + "CONSTRAINT PK_explicit_collation_keys PRIMARY KEY);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "explicit_collation_keys", ["Code"], ["nvarchar(40)"],
            ["Code"], ["nvarchar(40)"], new object?[,] { { "Foo" }, { "foo" } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-explicit-collation"));

        var count = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.explicit_collation_keys;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// Applies a captured source-to-target update once and replays it without mutation.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ManagedUpdate_AppliesAndReplays()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.update_roles (Id int NOT NULL CONSTRAINT PK_update_roles PRIMARY KEY, "
            + "Caption nvarchar(80) NOT NULL); "
            + "INSERT INTO dbo.update_roles (Id, Caption) VALUES (1, N'old');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.UpdateModelManagedDataFromModel(
            "update_roles", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Caption"], ["nvarchar(80)"],
            new object?[,] { { "old" } }, new object?[,] { { "new" } });

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var updated = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.update_roles WHERE Id = 1 AND Caption = N'new';");

        // Assert
        Assert.Equal(1, updated);
    }

    /// <summary>
    /// Does not overwrite an update row that diverged from both captured states.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ManagedUpdate_RejectsSourceDrift()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.update_drift (Id int NOT NULL CONSTRAINT PK_update_drift PRIMARY KEY, "
            + "Caption nvarchar(80) NOT NULL); "
            + "INSERT INTO dbo.update_drift (Id, Caption) VALUES (1, N'user-edited');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.UpdateModelManagedDataFromModel(
            "update_drift", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Caption"], ["nvarchar(80)"],
            new object?[,] { { "old" } }, new object?[,] { { "new" } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-update-drift"));

        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.update_drift WHERE Id = 1 AND Caption = N'user-edited';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, unchanged);
    }

    /// <summary>
    /// Deletes an exact source row once and treats its absence as an idempotent replay.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ManagedDelete_AppliesAndReplays()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.delete_roles (Id int NOT NULL CONSTRAINT PK_delete_roles PRIMARY KEY, "
            + "Caption nvarchar(80) NOT NULL); "
            + "INSERT INTO dbo.delete_roles (Id, Caption) VALUES (1, N'old');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DeleteModelManagedDataFromModel(
            "delete_roles", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Id", "Caption"], ["int", "nvarchar(80)"],
            new object?[,] { { 1, "old" } });

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var remaining = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.delete_roles;");

        // Assert
        Assert.Equal(0, remaining);
    }

    /// <summary>
    /// Blocks a modeled dependent row before deleting its principal seed row.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ManagedDelete_RejectsDependentRow()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.delete_principals (Id int NOT NULL "
            + "CONSTRAINT PK_delete_principals PRIMARY KEY, Caption nvarchar(80) NOT NULL); "
            + "CREATE TABLE dbo.delete_children (Id int NOT NULL CONSTRAINT PK_delete_children PRIMARY KEY, "
            + "PrincipalId int NOT NULL CONSTRAINT FK_delete_children_principals "
            + "REFERENCES dbo.delete_principals(Id)); "
            + "INSERT INTO dbo.delete_principals (Id, Caption) VALUES (1, N'old'); "
            + "INSERT INTO dbo.delete_children (Id, PrincipalId) VALUES (7, 1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DeleteModelManagedDataFromModel(
            "delete_principals", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Id", "Caption"], ["int", "nvarchar(80)"],
            new object?[,] { { 1, "old" } },
            foreignKeys: [new ExpectedModelManagedDataForeignKeyDefinition(
                "delete_children", ["PrincipalId"], ["Id"])]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-delete-dependent"));

        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.delete_principals WHERE Id = 1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, remaining);
    }

    /// <summary>
    /// Does not assume that an unmodeled incoming foreign key is harmless.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ManagedDelete_RejectsUnmodeledIncomingForeignKey()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.unmodeled_principals (Id int NOT NULL "
            + "CONSTRAINT PK_unmodeled_principals PRIMARY KEY); "
            + "CREATE TABLE dbo.unmodeled_children (Id int NOT NULL "
            + "CONSTRAINT PK_unmodeled_children PRIMARY KEY, PrincipalId int NOT NULL "
            + "CONSTRAINT FK_unmodeled_children_principals "
            + "REFERENCES dbo.unmodeled_principals(Id)); "
            + "INSERT INTO dbo.unmodeled_principals (Id) VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DeleteModelManagedDataFromModel(
            "unmodeled_principals", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Id"], ["int"], new object?[,] { { 1 } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-unmodeled-fk"));

        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.unmodeled_principals WHERE Id = 1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, remaining);
    }

    /// <summary>
    /// Classifies a physical column type mismatch before evaluating row content.
    /// </summary>
    [SqlServerLiveFact]
    public async Task MismatchedPhysicalStoreType_IsDifferentBeforeRowProbe()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.typed_roles (Id int NOT NULL CONSTRAINT PK_typed_roles PRIMARY KEY, "
            + "Caption nvarchar(20) NOT NULL); "
            + "INSERT INTO dbo.typed_roles (Id, Caption) VALUES (1, N'Old');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.UpdateModelManagedDataFromModel(
            "typed_roles", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Caption"], ["nvarchar(80)"],
            new object?[,] { { "Old" } }, new object?[,] { { "New" } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-type-drift"));

        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.typed_roles WHERE Id = 1 AND Caption = N'Old';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, unchanged);
    }

    /// <summary>
    /// Rejects an enabled DML trigger before model-managed data can cause side effects.
    /// </summary>
    [SqlServerLiveFact]
    public async Task EnabledDmlTrigger_IsUnsupportedBeforeManagedInsert()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.triggered_roles (Id int NOT NULL "
            + "CONSTRAINT PK_triggered_roles PRIMARY KEY);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER dbo.tr_triggered_roles ON dbo.triggered_roles AFTER INSERT AS "
            + "BEGIN SET NOCOUNT ON; END;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "triggered_roles", ["Id"], ["int"], ["Id"], ["int"],
            new object?[,] { { 1 } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-enabled-trigger"));

        var count = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.triggered_roles;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// Rejects an attempted write to a computed column before DML generation.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ComputedTargetColumn_IsUnsupportedBeforeManagedInsert()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.computed_roles (Id int NOT NULL "
            + "CONSTRAINT PK_computed_roles PRIMARY KEY, Code int NOT NULL, "
            + "Doubled AS (Code * 2));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "computed_roles", ["Id"], ["int"],
            ["Id", "Code", "Doubled"], ["int", "int", "int"],
            new object?[,] { { 1, 2, 4 } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-computed-seed"));

        var count = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.computed_roles;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// Rejects an identity-column update that SQL Server cannot perform.
    /// </summary>
    [SqlServerLiveFact]
    public async Task IdentityTargetColumn_IsUnsupportedBeforeManagedUpdate()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_update (Id int IDENTITY(1,1) NOT NULL "
            + "CONSTRAINT PK_identity_update PRIMARY KEY); "
            + "INSERT INTO dbo.identity_update DEFAULT VALUES;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.UpdateModelManagedDataFromModel(
            "identity_update", ["Id"], ["int"], new object?[,] { { 1 } },
            ["Id"], ["int"], new object?[,] { { 1 } },
            new object?[,] { { 2 } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-identity-update"));

        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.identity_update WHERE Id = 1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, unchanged);
    }
}
