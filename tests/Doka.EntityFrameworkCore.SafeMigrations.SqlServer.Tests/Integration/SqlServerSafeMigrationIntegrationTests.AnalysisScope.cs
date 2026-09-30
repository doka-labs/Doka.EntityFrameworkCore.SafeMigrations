namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Leaves transaction disposal to its owner while classifying uncommitted ordinary and delayed catalog work.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalyzeAsync_OwnedAndBorrowedScopesObserveUncommittedCatalogWithoutTakingCallerOwnership(
        bool callerOwnsTransaction
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        await using var transaction = callerOwnsTransaction
            ? await context.Database.BeginTransactionAsync()
            : null;

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("uncommitted_items", new ExpectedColumnDefinition("Id", typeof(int), false, "int"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureColumn(
            "uncommitted_items", new ExpectedColumnDefinition("RequiredValue", typeof(int), false, "int"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE dbo.uncommitted_items (Id int NOT NULL);");
        var analyses = await analyzer.AnalyzeAsync(
            context, builder.Operations.Cast<SafeMigrationOperation>().ToArray());

        var unexpected = await analyzer.FindUnexpectedObjectsAsync(context, builder.Operations);
        await scope.DisposeAsync();
        var activeTransaction = context.Database.CurrentTransaction;
        if (transaction is not null)
        {
            await transaction.CommitAsync();
        }

        var tableCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.uncommitted_items', N'U');");

        // Assert
        Assert.Equal(2, analyses.Count);
        Assert.Equal(SafeMigrationObservedState.Matching, analyses[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, analyses[1].ObservedState);
        Assert.Empty(unexpected);
        Assert.Same(transaction, activeTransaction);
        Assert.Equal(callerOwnsTransaction ? 1 : 0, tableCount);
    }

    /// <summary>
    /// Executes catalog classification and unexpected-object inventory inside either analysis transaction
    /// ownership mode.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisScope_BindsAllCatalogCommandsToActiveTransaction(bool callerOwnsTransaction)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.scoped_items (Id int NOT NULL);");
        await using var context = CreateContext(connectionString);
        await using var transaction = callerOwnsTransaction
            ? await context.Database.BeginTransactionAsync()
            : null;

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "scoped_items",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });
        var operation = (SafeMigrationOperation)builder.Operations.Single();

        // Act
        await using var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        var analyses = await analyzer.AnalyzeAsync(context, [operation]);
        var unexpected = await analyzer.FindUnexpectedObjectsAsync(context, builder.Operations);

        // Assert
        Assert.IsType<SafeMigrationOperation>(Assert.Single(builder.Operations));
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(analyses).ObservedState);
        Assert.Empty(unexpected);
        Assert.Equal(callerOwnsTransaction, transaction is not null);
    }

    /// <summary>
    /// Releases the transaction-owned analysis lock when the caller keeps its transaction open.
    /// </summary>
    [SqlServerLiveFact]
    public async Task CallerOwnedAnalysisScope_ReleasesLockBeforeCallerTransactionCommits()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        await using var competingConnection = new SqlConnection(connectionString);
        await competingConnection.OpenAsync();
        await using var competingCommand = competingConnection.CreateCommand();
        competingCommand.CommandText = "DECLARE @result int; "
            + "EXEC @result = sys.sp_getapplock "
            + "@Resource = N'doka-sm-catalog-analysis', @LockMode = N'Exclusive', "
            + "@LockOwner = N'Session', @LockTimeout = 0; SELECT @result;";

        // Act
        var scope = await analyzer.AcquireAnalysisScopeAsync(context);
        var blockedResult = Convert.ToInt32(await competingCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        await scope.DisposeAsync();
        var acquiredResult = Convert.ToInt32(await competingCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        await using var releaseCommand = competingConnection.CreateCommand();
        releaseCommand.CommandText = "DECLARE @result int; "
            + "EXEC @result = sys.sp_releaseapplock "
            + "@Resource = N'doka-sm-catalog-analysis', @LockOwner = N'Session'; SELECT @result;";
        var releasedResult = Convert.ToInt32(await releaseCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

        // Assert
        Assert.True(blockedResult < 0);
        Assert.True(acquiredResult >= 0);
        Assert.True(releasedResult >= 0);
        Assert.NotNull(context.Database.CurrentTransaction);
    }
}
