namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies model managed data key column identity SQL Server contracts against isolated databases.</summary>
public sealed class SqlServerSafeMigrationModelManagedDataKeyColumnIdentityIntegrationTests
    : SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Creates the feature suite with the assembly-owned SQL Server fixture.</summary>
    /// <param name="fixture">The shared fixture providing a database per test.</param>
    public SqlServerSafeMigrationModelManagedDataKeyColumnIdentityIntegrationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>
    /// Verifies successive exact-key changes as separate authored operation streams.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ManagedRowSameExactKey_SeparateStreamsVerifyEachTransition()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.managed_key_identity (Id int NOT NULL "
            + "CONSTRAINT PK_managed_key_identity PRIMARY KEY, Caption nvarchar(80) NOT NULL); "
            + "INSERT INTO dbo.managed_key_identity (Id, Caption) VALUES (7, N'old');");
        await using var context = CreateContext(connectionString);
        var ensure = new MigrationBuilder(context.Database.ProviderName!);
        ensure.EnsureModelManagedDataFromModel("managed_key_identity", ["Id"], ["int"],
            ["Id", "Caption"], ["int", "nvarchar(80)"], new object?[,] { { 7, "old" } });
        var update = new MigrationBuilder(context.Database.ProviderName!);
        update.UpdateModelManagedDataFromModel("managed_key_identity", ["Id"], ["int"],
            new object?[,] { { 7 } }, ["Caption"], ["nvarchar(80)"],
            new object?[,] { { "old" } }, new object?[,] { { "new" } });
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var ensurePreflight = await runner.AnalyzeAsync(context, ensure.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-key-exact-ensure-preflight"));

        await ExecuteOperationsAsync(context, ensure.Operations);
        var ensurePostflight = await runner.VerifyAsync(context, ensure.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-key-exact-ensure-postflight"));

        // WHY: A migration owns at most one transition for a typed seed key.
        // Verify that authored boundary before the next migration changes it;
        // standalone VerifyAsync does not accept invented migration-ID groups.
        var updatePreflight = await runner.AnalyzeAsync(context, update.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-key-exact-update-preflight"));

        await ExecuteOperationsAsync(context, update.Operations);
        var updatePostflight = await runner.VerifyAsync(context, update.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-key-exact-update-postflight"));

        var finalRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.managed_key_identity WHERE Id = 7 AND Caption = N'new';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, ensurePreflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, ensurePostflight.Status);
        Assert.True(Assert.Single(ensurePostflight.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationReportStatus.Ready, updatePreflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(updatePreflight.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, updatePostflight.Status);
        Assert.True(Assert.Single(updatePostflight.Assessments).PostconditionSatisfied);
        Assert.NotEqual("postcondition_superseded", Assert.Single(updatePostflight.Assessments).Code);
        Assert.Equal(1, finalRows);
    }

    /// <summary>
    /// Retains standalone duplicate-key rejection before any migration mutation can occur.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ManagedRowSameExactKey_CombinedStandaloneStreamRemainsRejected()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.managed_key_duplicate (Id int NOT NULL "
            + "CONSTRAINT PK_managed_key_duplicate PRIMARY KEY, Caption nvarchar(80) NOT NULL); "
            + "INSERT INTO dbo.managed_key_duplicate (Id, Caption) VALUES (7, N'old');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("managed_key_duplicate", ["Id"], ["int"],
            ["Id", "Caption"], ["int", "nvarchar(80)"], new object?[,] { { 7, "old" } });
        builder.UpdateModelManagedDataFromModel("managed_key_duplicate", ["Id"], ["int"],
            new object?[,] { { 7 } }, ["Caption"], ["nvarchar(80)"],
            new object?[,] { { "old" } }, new object?[,] { { "new" } });

        // Act
        var rejected = await Record.ExceptionAsync(() => context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-managed-key-duplicate")));

        var unchangedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.managed_key_duplicate WHERE Id = 7 AND Caption = N'old';");

        // Assert
        Assert.Contains("same typed model-managed key", Assert.IsType<InvalidOperationException>(rejected).Message,
            StringComparison.Ordinal);
        Assert.Equal(1, unchangedRows);
    }

    /// <summary>
    /// Retains the provider's exact-spelling guard rather than silently broadening CI catalog aliases.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ManagedRowCaseAliasedKey_RemainsRejectedBeforeMutation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.managed_key_alias (Id int NOT NULL "
            + "CONSTRAINT PK_managed_key_alias PRIMARY KEY, Caption nvarchar(80) NOT NULL); "
            + "INSERT INTO dbo.managed_key_alias (Id, Caption) VALUES (7, N'old');");
        var catalogAliasesCase = await ScalarIntAsync(connectionString,
            "SELECT CASE WHEN N'Id' COLLATE CATALOG_DEFAULT = N'ID' COLLATE CATALOG_DEFAULT THEN 1 ELSE 0 END;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("managed_key_alias", ["Id"], ["int"],
            ["Id", "Caption"], ["int", "nvarchar(80)"], new object?[,] { { 7, "old" } });
        builder.UpdateModelManagedDataFromModel("managed_key_alias", ["ID"], ["int"],
            new object?[,] { { 7 } }, ["Caption"], ["nvarchar(80)"],
            new object?[,] { { "old" } }, new object?[,] { { "new" } });

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-key-alias-preflight"));

        var rejectedExecution = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var unchangedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.managed_key_alias WHERE Id = 7 AND Caption = N'old';");

        // Assert
        Assert.Equal(1, catalogAliasesCase);
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.All(preflight.Assessments, static assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
            Assert.Equal("identifier_collation_unproven", assessment.AnalysisCode);
        });

        Assert.Equal(51002, Assert.IsType<SqlException>(rejectedExecution).Number);
        Assert.Equal(1, unchangedRows);
    }
}
