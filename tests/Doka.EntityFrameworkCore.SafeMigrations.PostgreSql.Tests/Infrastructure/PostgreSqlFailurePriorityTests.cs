namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Exercises native analysis ownership with deterministic, independently failing cleanup boundaries.</summary>
public sealed class PostgreSqlFailurePriorityTests
{
    /// <summary>Connection closing cannot replace an environment failure or its cancellation identity.</summary>
    /// <param name="canceled">Whether the original failure represents cancellation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetEnvironmentAsync_CloseFailurePreservesPrimary(
        bool canceled
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var primary = CreatePrimary(canceled, cancellation.Token);
        var secondary = new InvalidOperationException("Synthetic close failure.");
        await using var connection = new PostgreSqlFailurePriorityConnection
        {
            EnvironmentFailure = primary,
            CloseFailure = secondary,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.GetEnvironmentAsync(context, cancellation.Token));

        // Assert
        Assert.Same(primary, failure);
        Assert.Same(secondary, primary.Data["SafeMigrations.AnalysisConnectionCloseException"]);
        Assert.Equal(1, connection.AsyncCloseCount);
    }

    /// <summary>A close-only failure remains observable instead of becoming a successful environment result.</summary>
    [Fact]
    public async Task GetEnvironmentAsync_CloseOnlyFailurePropagates()
    {
        // Arrange
        var secondary = new InvalidOperationException("Synthetic close failure.");
        await using var connection = new PostgreSqlFailurePriorityConnection { CloseFailure = secondary };
        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.GetEnvironmentAsync(context, CancellationToken.None));

        // Assert
        Assert.Same(secondary, failure);
        Assert.Equal(1, connection.AsyncCloseCount);
    }

    /// <summary>Cleanup observes actual delayed close completion even after the work has failed.</summary>
    [Fact]
    public async Task GetEnvironmentAsync_DelayedCloseRetainsOwnership()
    {
        // Arrange
        var primary = new InvalidOperationException("Synthetic environment failure.");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new PostgreSqlFailurePriorityConnection
        {
            EnvironmentFailure = primary,
            CloseCompletion = release.Task,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);

        // Act
        var attempt = Record.ExceptionAsync(() => analyzer.GetEnvironmentAsync(context, CancellationToken.None));
        _ = await Task.WhenAny(connection.CloseStarted.Task, attempt);
        var returnedBeforeClose = attempt.IsCompleted;
        release.SetResult();
        var failure = await attempt;

        // Assert
        Assert.True(connection.CloseStarted.Task.IsCompletedSuccessfully);
        Assert.False(returnedBeforeClose);
        Assert.Same(primary, failure);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>Command and transaction disposal errors cannot replace acquisition failure or cancellation.</summary>
    /// <param name="canceled">Whether command execution reports cancellation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireAnalysisScopeAsync_DisposeFailuresPreservePrimary(
        bool canceled
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var primary = CreatePrimary(canceled, cancellation.Token);
        var commandFailure = new InvalidOperationException("Synthetic command disposal failure.");
        var transactionFailure = new InvalidOperationException("Synthetic transaction disposal failure.");
        await using var connection = new PostgreSqlFailurePriorityConnection
        {
            ExecutionFailure = primary,
            CommandDisposeFailure = commandFailure,
            TransactionDisposeFailure = transactionFailure,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.AcquireAnalysisScopeAsync(
            context,
            cancellation.Token));

        // Assert
        Assert.Same(primary, failure);
        Assert.Same(commandFailure, primary.Data["SafeMigrations.AnalysisCommandDisposeException"]);
        Assert.Same(transactionFailure, primary.Data["SafeMigrations.AnalysisTransactionDisposeException"]);
        Assert.Equal(1, connection.CommandDisposeCount);
        Assert.Equal(1, connection.AsyncTransactionDisposeCount);
        Assert.Contains("ExecuteScalar", primary.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>A cleanup-only command failure remains primary and still releases its transaction.</summary>
    [Fact]
    public async Task AcquireAnalysisScopeAsync_DisposeOnlyFailureRemainsObservable()
    {
        // Arrange
        var commandFailure = new InvalidOperationException("Synthetic command disposal failure.");
        await using var connection = new PostgreSqlFailurePriorityConnection
        {
            CommandDisposeFailure = commandFailure,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.AcquireAnalysisScopeAsync(
            context,
            CancellationToken.None));

        // Assert
        Assert.Same(commandFailure, failure);
        Assert.Equal(1, connection.AsyncTransactionDisposeCount);
    }

    /// <summary>An owned transaction is not disposed while its acquisition command still borrows it.</summary>
    [Fact]
    public async Task AcquireAnalysisScopeAsync_DelayedCommandDisposalPrecedesTransactionDisposal()
    {
        // Arrange
        var primary = new InvalidOperationException("Synthetic acquisition failure.");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new PostgreSqlFailurePriorityConnection
        {
            ExecutionFailure = primary,
            CommandDisposeCompletion = release.Task,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);

        // Act
        var attempt = Record.ExceptionAsync(() => analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None));
        _ = await Task.WhenAny(connection.CommandDisposeStarted.Task, attempt);
        var returnedBeforeDisposal = attempt.IsCompleted;
        var transactionsDisposedEarly = connection.AsyncTransactionDisposeCount;
        release.SetResult();
        var failure = await attempt;

        // Assert
        Assert.True(connection.CommandDisposeStarted.Task.IsCompletedSuccessfully);
        Assert.False(returnedBeforeDisposal);
        Assert.Equal(0, transactionsDisposedEarly);
        Assert.Equal(1, connection.AsyncTransactionDisposeCount);
        Assert.Same(primary, failure);
    }

    /// <summary>Successful acquisition transfers transaction ownership to the returned scope.</summary>
    [Fact]
    public async Task AcquireAnalysisScopeAsync_SuccessTransfersTransactionOwnership()
    {
        // Arrange
        await using var connection = new PostgreSqlFailurePriorityConnection();
        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);

        // Act
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);
        var transactionDisposalsBeforeScope = connection.AsyncTransactionDisposeCount;
        await scope.DisposeAsync();

        // Assert
        Assert.Equal(0, transactionDisposalsBeforeScope);
        Assert.Equal(1, connection.CommandDisposeCount);
        Assert.Equal(1, connection.AsyncTransactionDisposeCount);
    }

    /// <summary>Rejected caller transaction state remains primary when its validation command cannot dispose.</summary>
    [Fact]
    public async Task AcquireAnalysisScopeAsync_CallerValidationPreservesFailureAndBorrowedTransaction()
    {
        // Arrange
        var commandFailure = new InvalidOperationException("Synthetic validation cleanup failure.");
        await using var connection = new PostgreSqlFailurePriorityConnection
        {
            CommandDisposeFailure = commandFailure,
            DisposeCommandText = "SHOW transaction_read_only;",
        };

        await connection.OpenAsync(CancellationToken.None);
        await using var nativeTransaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            CancellationToken.None);

        await using var context = CreateContext(connection);
        _ = await context.Database.UseTransactionAsync(nativeTransaction, CancellationToken.None);
        var analyzer = CreateAnalyzer(context);

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.AcquireAnalysisScopeAsync(
            context,
            CancellationToken.None));

        // Assert
        var rejected = Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(
            "PostgreSQL SafeMigrations analysis requires a caller-owned transaction to be read-only.",
            rejected.Message);

        Assert.Same(commandFailure, rejected.Data["SafeMigrations.AnalysisCommandDisposeException"]);
        Assert.Equal(0, connection.AsyncTransactionDisposeCount);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>A failed nonempty analysis retains its native failure when its owned connection cannot close.</summary>
    [Fact]
    public async Task AnalyzeAsync_CloseFailurePreservesPrimary()
    {
        // Arrange
        var primary = new InvalidOperationException("Synthetic catalog failure.");
        var closeFailure = new InvalidOperationException("Synthetic close failure.");
        await using var connection = new PostgreSqlFailurePriorityConnection
        {
            ExecutionFailure = primary,
            CloseFailure = closeFailure,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);
        var operation = new SafeMigrationOperation(
            new EnsureSchemaIntent("owned"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.AnalyzeAsync(
            context,
            [operation],
            CancellationToken.None));

        // Assert
        Assert.Same(primary, failure);
        Assert.Same(closeFailure, primary.Data["SafeMigrations.AnalysisConnectionCloseException"]);
        Assert.Equal(1, connection.AsyncCloseCount);
    }

    /// <summary>Creates an independent primary failure without any machine-speed cancellation timer.</summary>
    /// <param name="canceled">Whether the primary failure represents a provider cancellation.</param>
    /// <param name="cancellationToken">The identity preserved by a provider cancellation.</param>
    /// <returns>The exact failure instance used by the deterministic native boundary.</returns>
    private static Exception CreatePrimary(
        bool canceled,
        CancellationToken cancellationToken
    ) => canceled
        ? new OperationCanceledException("Synthetic provider cancellation.", cancellationToken)
        : new InvalidOperationException("Synthetic provider failure.");

    /// <summary>Uses the real EF provider services over an entirely deterministic native boundary.</summary>
    /// <param name="connection">The caller-owned synthetic connection.</param>
    /// <returns>A real provider context which does not own the supplied connection.</returns>
    private static DbContext CreateContext(
        DbConnection connection
    ) => new(new DbContextOptionsBuilder().UseNpgsql(connection)
        .Options);

    /// <summary>Uses actual provider mapping and SQL-generation services without opening a native server.</summary>
    /// <param name="context">The context supplying the actual Npgsql services.</param>
    /// <returns>The analyzer under test, using the context's provider services.</returns>
    private static PostgreSqlSafeMigrationProviderAnalyzer CreateAnalyzer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
