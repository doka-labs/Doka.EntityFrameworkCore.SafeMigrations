namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>
    /// Native key-column case aliases identify the same row across ordered final-writer verification.
    /// </summary>
    [Fact]
    public async Task ModelManagedKeyColumnIdentityVerifiesOrderedEnsureAndUpdate()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ModelManagedKeyColumnIdentityFixtureSql("managed_key_items"));
        await using var context = CreateContext(connectionString);
        var builder = ModelManagedKeyColumnIdentityOperations(context.Database.ProviderName!, "ID");
        var runner = context.GetService<ISafeMigrationRunner>();
        var finalOperation = builder.Operations[1];

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var whole = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("model-managed-key-case-whole"), CancellationToken.None);

        var freshFinal = await runner.VerifyAsync(context, [finalOperation],
            new SafeMigrationRunOptions("model-managed-key-case-final"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, [finalOperation],
            new SafeMigrationRunOptions("model-managed-key-case-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [finalOperation], CancellationToken.None);
        var afterReplay = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("model-managed-key-case-after-replay"), CancellationToken.None);

        var retainedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `managed_key_items` WHERE `Id` = 1 "
            + "AND CAST(`value` AS BINARY) = CAST('new' AS BINARY);");

        var totalRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `managed_key_items`;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, whole.Status);
        Assert.Equal("postcondition_superseded", whole.Assessments[0].Code);
        Assert.True(whole.Assessments[0].PostconditionSatisfied);
        Assert.True(whole.Assessments[1].PostconditionSatisfied);
        Assert.Equal(SafeMigrationObservedState.Matching, whole.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationReportStatus.Ready, freshFinal.Status);
        Assert.True(Assert.Single(freshFinal.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, afterReplay.Status);
        Assert.Equal("postcondition_superseded", afterReplay.Assessments[0].Code);
        Assert.Equal(1, retainedRows);
        Assert.Equal(1, totalRows);
    }

    /// <summary>Separate exact-key migrations verify and replay without relaxing the one-batch contract.</summary>
    [Fact]
    public async Task ModelManagedKeyColumnIdentityExactOperationsVerifySeparately()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ModelManagedKeyColumnIdentityFixtureSql("managed_key_items"));
        await using var context = CreateContext(connectionString);
        var builder = ModelManagedKeyColumnIdentityOperations(context.Database.ProviderName!, "Id");
        var runner = context.GetService<ISafeMigrationRunner>();
        var ensure = builder.Operations[0];
        var update = builder.Operations[1];

        // Act
        await ExecuteOperationsAsync(context, [ensure], CancellationToken.None);
        var original = await runner.VerifyAsync(context, [ensure],
            new SafeMigrationRunOptions("model-managed-exact-key-original"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [update], CancellationToken.None);
        var final = await runner.VerifyAsync(context, [update],
            new SafeMigrationRunOptions("model-managed-exact-key-final"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, [update],
            new SafeMigrationRunOptions("model-managed-exact-key-replay"), CancellationToken.None);

        await ExecuteOperationsAsync(context, [update], CancellationToken.None);
        var retainedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `managed_key_items` WHERE `Id` = 1 "
            + "AND CAST(`value` AS BINARY) = CAST('new' AS BINARY);");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, original.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(original.Assessments).ObservedState);
        Assert.True(Assert.Single(original.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationReportStatus.Ready, final.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(final.Assessments).ObservedState);
        Assert.True(Assert.Single(final.Assessments).PostconditionSatisfied);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>
    /// A standalone batch still rejects two transitions of the same exact typed key before any mutation.
    /// </summary>
    [Fact]
    public async Task ModelManagedKeyColumnIdentityExactDuplicateBatchIsRejectedBeforeMutation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ModelManagedKeyColumnIdentityFixtureSql("managed_key_items"));
        await using var context = CreateContext(connectionString);
        var builder = ModelManagedKeyColumnIdentityOperations(context.Database.ProviderName!, "Id");

        // Act
        var exception = await Record.ExceptionAsync(() => context.GetService<ISafeMigrationRunner>().VerifyAsync(
            context, builder.Operations, new SafeMigrationRunOptions("model-managed-exact-key-duplicate-batch"),
            CancellationToken.None));

        var originalRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `managed_key_items` WHERE `Id` = 1 "
            + "AND CAST(`value` AS BINARY) = CAST('old' AS BINARY);");

        // Assert
        Assert.Contains("same typed model-managed key more than once",
            Assert.IsType<InvalidOperationException>(exception).Message, StringComparison.Ordinal);
        Assert.Equal(1, originalRows);
    }

    /// <summary>A case-aliased earlier key cannot hide a final row value that violates the update contract.</summary>
    [Fact]
    public async Task ModelManagedKeyColumnIdentityDoesNotHideFinalValueDrift()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ModelManagedKeyColumnIdentityFixtureSql("managed_key_items"));
        await using var context = CreateContext(connectionString);
        var builder = ModelManagedKeyColumnIdentityOperations(context.Database.ProviderName!, "ID");
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Act
        await ExecuteSqlAsync(connectionString, "UPDATE `managed_key_items` SET `value` = 'wrong' WHERE `Id` = 1;");
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("model-managed-key-case-wrong-value"), CancellationToken.None);

        var retainedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `managed_key_items` WHERE `Id` = 1 "
            + "AND CAST(`value` AS BINARY) = CAST('wrong' AS BINARY);");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("postcondition_superseded", report.Assessments[0].Code);
        Assert.True(report.Assessments[0].PostconditionSatisfied);
        Assert.False(report.Assessments[1].PostconditionSatisfied);
        Assert.Equal("postcondition_failed", report.Assessments[1].Code);
        Assert.Equal(1, retainedRows);
    }

    /// <summary>Equal keys with aliased column spellings remain distinct when their table owners differ.</summary>
    [Fact]
    public async Task ModelManagedKeyColumnIdentityDoesNotSupersedeAnotherOwner()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, ModelManagedKeyColumnIdentityFixtureSql("managed_key_items")
            + ModelManagedKeyColumnIdentityFixtureSql("managed_other_items"));

        await using var context = CreateContext(connectionString);
        var builder = ModelManagedKeyColumnIdentityOperations(context.Database.ProviderName!, "ID",
            updateTable: "managed_other_items");

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Act
        await ExecuteSqlAsync(connectionString, "UPDATE `managed_key_items` SET `value` = 'wrong' WHERE `Id` = 1;");
        var report = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("model-managed-key-case-wrong-owner"), CancellationToken.None);

        var sourceRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `managed_key_items` WHERE `Id` = 1 "
            + "AND CAST(`value` AS BINARY) = CAST('wrong' AS BINARY);");

        var targetRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `managed_other_items` WHERE `Id` = 1 "
            + "AND CAST(`value` AS BINARY) = CAST('new' AS BINARY);");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(report.Assessments[0].PostconditionSatisfied);
        Assert.Equal("postcondition_failed", report.Assessments[0].Code);
        Assert.True(report.Assessments[1].PostconditionSatisfied);
        Assert.Equal(1, sourceRows);
        Assert.Equal(1, targetRows);
    }

    /// <summary>
    /// Builds an ordered row update whose only key-identity variation is the authored column spelling.
    /// </summary>
    /// <param name="providerName">The native runtime provider used by the isolated fixture.</param>
    /// <param name="updateKeyColumn">The update's native key-column alias.</param>
    /// <param name="updateTable">The same physical owner, or a deliberately unrelated owner control.</param>
    /// <returns>The original ensure followed by its typed source-to-target update.</returns>
    private static MigrationBuilder ModelManagedKeyColumnIdentityOperations(
        string providerName,
        string updateKeyColumn,
        string updateTable = "managed_key_items"
    )
    {
        var builder = new MigrationBuilder(providerName);
        _ = builder.EnsureModelManagedDataFromModel("managed_key_items", ["Id"], ["int"],
            ["Id", "value"], ["int", "varchar(32)"], new object?[,] { { 1, "old" } });

        _ = builder.UpdateModelManagedDataFromModel(updateTable, [updateKeyColumn], ["int"],
            new object?[,] { { 1 } }, ["value"], ["varchar(32)"],
            new object?[,] { { "old" } }, new object?[,] { { "new" } });

        return builder;
    }

    /// <summary>
    /// Creates one typed native key and a single original row without changing value comparison semantics.
    /// </summary>
    /// <param name="table">The anonymous test-owned table, also used for the distinct-owner control.</param>
    /// <returns>The isolated InnoDB fixture and its explicit original value.</returns>
    private static string ModelManagedKeyColumnIdentityFixtureSql(string table)
        => "CREATE TABLE " + AtomicRequiredAddDelimited(table)
            + " (`Id` int NOT NULL PRIMARY KEY, `value` varchar(32) NOT NULL) ENGINE=InnoDB; "
            + "INSERT INTO " + AtomicRequiredAddDelimited(table) + " (`Id`, `value`) VALUES (1, 'old');";
}
