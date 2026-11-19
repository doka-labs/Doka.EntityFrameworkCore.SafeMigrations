namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Exercises real public runner nesting through SQLite model services and deterministic cleanup controls.
/// </summary>

// WHY: Meter observations are process-wide and must not include unrelated concurrent runner calls.
[Collection(ProcessWideTestGroup.Name)]
public sealed class SqliteRunnerCleanupOwnershipTests
{
    private const string ScopeEvidence = "SafeMigrations.AnalysisScopeDisposeException";
    private const string CloseEvidence = "SafeMigrations.AnalysisConnectionCloseException";

    /// <summary>Analysis or cancellation remains primary when both acquired-scope disposal and close fail.</summary>
    /// <param name="canceled">Whether the admitted analysis cancels its operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalyzeAsync_WorkAndBothCleanupFailuresPreserveOriginalIdentity(
        bool canceled
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Actual provider analysis failed.");

        var scopeFailure = new InvalidOperationException("Scope disposal failed.");
        var closeFailure = new InvalidOperationException("Connection close failed.");
        await using var connection = new SqliteRunnerCleanupConnection(Task.FromException(closeFailure));
        await using var analyzer = new SqliteRunnerCleanupAnalyzer(
            Task.FromException(scopeFailure),
            primary,
            canceled
                ? cancellation.Cancel
                : null);

        await using var context = CreateContext(connection);
        var runner = new SafeMigrationRunner(analyzer);
        using var telemetry = new SqliteRunnerCleanupTelemetry();

        // Act
        var failure = await Record.ExceptionAsync(() => runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("runner-cleanup"),
            cancellation.Token));

        // Assert
        Assert.Same(primary, failure);
        Assert.Same(scopeFailure, primary.Data[ScopeEvidence]);
        Assert.Same(closeFailure, primary.Data[CloseEvidence]);
        Assert.Contains(nameof(SqliteRunnerCleanupAnalyzer), primary.StackTrace, StringComparison.Ordinal);
        Assert.Equal(1, analyzer.AnalyzeCount);
        Assert.Equal(1, analyzer.DisposeCount);
        Assert.Equal(1, connection.OpenCount);
        Assert.Equal(1, connection.CloseCount);
        Assert.Equal(0, connection.SynchronousCalls);
        Assert.Equal(0, telemetry.Runs);
        Assert.Equal(canceled
                ? 0
                : 1,
            telemetry.Failures);

        if (canceled)
        {
            Assert.Equal(
                cancellation.Token,
                Assert.IsType<OperationCanceledException>(failure)
                    .CancellationToken);
        }
    }

    /// <summary>
    /// A disposal-only failure becomes primary while later connection-close evidence remains attached.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_ScopeFailureRemainsPrimaryWhenCloseAlsoFails()
    {
        // Arrange
        var scopeFailure = new InvalidOperationException("Scope disposal failed.");
        var closeFailure = new InvalidOperationException("Connection close failed.");
        await using var connection = new SqliteRunnerCleanupConnection(Task.FromException(closeFailure));
        await using var analyzer = new SqliteRunnerCleanupAnalyzer(Task.FromException(scopeFailure));
        await using var context = CreateContext(connection);
        var runner = new SafeMigrationRunner(analyzer);
        using var telemetry = new SqliteRunnerCleanupTelemetry();

        // Act
        var failure = await Record.ExceptionAsync(() => runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("runner-cleanup"),
            CancellationToken.None));

        // Assert
        Assert.Same(scopeFailure, failure);
        Assert.Same(closeFailure, scopeFailure.Data[CloseEvidence]);
        Assert.Equal(1, analyzer.AnalyzeCount);
        Assert.Equal(1, analyzer.DisposeCount);
        Assert.Equal(1, connection.CloseCount);
        Assert.Equal(0, telemetry.Runs);
        Assert.Equal(1, telemetry.Failures);
    }

    /// <summary>A native close failure after otherwise successful work is not silently accepted.</summary>
    [Fact]
    public async Task AnalyzeAsync_CloseOnlyFailureIsObservable()
    {
        // Arrange
        var closeFailure = new InvalidOperationException("Connection close failed.");
        await using var connection = new SqliteRunnerCleanupConnection(Task.FromException(closeFailure));
        await using var analyzer = new SqliteRunnerCleanupAnalyzer(Task.CompletedTask);
        await using var context = CreateContext(connection);
        var runner = new SafeMigrationRunner(analyzer);

        // Act
        var failure = await Record.ExceptionAsync(() => runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("runner-cleanup"),
            CancellationToken.None));

        // Assert
        Assert.Same(closeFailure, failure);
        Assert.Equal(1, analyzer.AnalyzeCount);
        Assert.Equal(1, analyzer.DisposeCount);
        Assert.Equal(1, connection.CloseCount);
    }

    /// <summary>A successful borrowed connection remains open and is never closed by the runner.</summary>
    [Fact]
    public async Task AnalyzeAsync_CallerOpenedConnectionRetainsCallerOwnership()
    {
        // Arrange
        await using var connection = new SqliteRunnerCleanupConnection(Task.CompletedTask, alreadyOpen: true);
        await using var analyzer = new SqliteRunnerCleanupAnalyzer(Task.CompletedTask);
        await using var context = CreateContext(connection);
        var runner = new SafeMigrationRunner(analyzer);

        // Act
        var report = await runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("runner-cleanup"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.NoOperations, report.Status);
        Assert.Equal(1, analyzer.AnalyzeCount);
        Assert.Equal(1, analyzer.DisposeCount);
        Assert.Equal(0, connection.OpenCount);
        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(0, connection.SynchronousCalls);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>The runner cannot close its owner or return while actual scope disposal remains pending.</summary>
    [Fact]
    public async Task AnalyzeAsync_PendingScopeDisposalPreventsConnectionClose()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new SqliteRunnerCleanupConnection(Task.CompletedTask);
        await using var analyzer = new SqliteRunnerCleanupAnalyzer(release.Task);
        await using var context = CreateContext(connection);
        var runner = new SafeMigrationRunner(analyzer);
        bool entered;
        bool completedBeforeRelease;
        int closeBeforeRelease;
        Exception? failure;

        // Act
        var run = runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("runner-cleanup"),
            CancellationToken.None);

        try
        {
            var first = await Task.WhenAny(run, analyzer.DisposeEntered);
            entered = ReferenceEquals(first, analyzer.DisposeEntered);
            completedBeforeRelease = run.IsCompleted;
            closeBeforeRelease = connection.CloseCount;
        }
        finally
        {
            // WHY: Even an unexpected early runner failure must release and drain
            // the actual operation rather than leaving a test-owned task pending.
            release.TrySetResult();
            failure = await Record.ExceptionAsync(async () => _ = await run);
        }

        // Assert
        Assert.True(entered);
        Assert.False(completedBeforeRelease);
        Assert.Equal(0, closeBeforeRelease);
        Assert.Null(failure);
        Assert.Equal(1, analyzer.DisposeCount);
        Assert.Equal(1, connection.CloseCount);
    }

    /// <summary>The runner must observe native close completion rather than only starting close.</summary>
    [Fact]
    public async Task AnalyzeAsync_PendingClosePreventsRunnerCompletion()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new SqliteRunnerCleanupConnection(release.Task);
        await using var analyzer = new SqliteRunnerCleanupAnalyzer(Task.CompletedTask);
        await using var context = CreateContext(connection);
        var runner = new SafeMigrationRunner(analyzer);
        bool entered;
        bool completedBeforeRelease;
        Exception? failure;

        // Act
        var run = runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("runner-cleanup"),
            CancellationToken.None);

        try
        {
            var first = await Task.WhenAny(run, connection.CloseEntered.Task);
            entered = ReferenceEquals(first, connection.CloseEntered.Task);
            completedBeforeRelease = run.IsCompleted;
        }
        finally
        {
            release.TrySetResult();
            failure = await Record.ExceptionAsync(async () => _ = await run);
        }

        // Assert
        Assert.True(entered);
        Assert.False(completedBeforeRelease);
        Assert.Null(failure);
        Assert.Equal(1, analyzer.DisposeCount);
        Assert.Equal(1, connection.CloseCount);
    }

    /// <summary>Constructs the real SQLite design-time model without opening a native database.</summary>
    /// <param name="connection">The independently controlled caller connection.</param>
    /// <returns>A normal EF DbContext configured with the actual SQLite provider.</returns>
    private static DbContext CreateContext(
        DbConnection connection
    ) => new(new DbContextOptionsBuilder().UseSqlite(connection)
        .Options);
}
