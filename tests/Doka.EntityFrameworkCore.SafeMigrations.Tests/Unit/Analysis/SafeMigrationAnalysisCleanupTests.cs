namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies protected completion and failure identity without wallclock-based observations.</summary>
public sealed class SafeMigrationAnalysisCleanupTests
{
    private const string DisposeEvidence = "SafeMigrations.AnalysisScopeDisposeException";
    private const string CloseEvidence = "SafeMigrations.AnalysisConnectionCloseException";

    /// <summary>Cleanup retains actual pending work even when the original operation was canceled.</summary>
    /// <param name="closeConnection">Whether the owned boundary closes a connection or disposes a scope.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupAsync_CanceledOperationStillAwaitsActualCompletion(
        bool closeConnection
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var primary = new OperationCanceledException(cancellation.Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new CleanupResource(release.Task);
        var connection = new CleanupConnection(release.Task);

        // Act
        var cleanup = closeConnection
            ? SafeMigrationAnalysisCleanup.CloseAsync(connection, primary)
            : SafeMigrationAnalysisCleanup
                .DisposeAsync(resource, primary, DisposeEvidence)
                .AsTask();

        var completedBeforeRelease = cleanup.IsCompleted;
        var started = closeConnection
            ? connection.CloseStarted
            : resource.DisposeStarted;

        release.SetResult();
        await cleanup;

        // Assert
        Assert.True(started);
        Assert.False(completedBeforeRelease);
        Assert.True(release.Task.IsCompletedSuccessfully);
        Assert.Empty(primary.Data);
    }

    /// <summary>A secondary failure cannot replace the original work or cancellation identity.</summary>
    /// <param name="closeConnection">Whether the tested boundary is connection close.</param>
    /// <param name="canceled">Whether the primary failure is cancellation.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CleanupAsync_PrimaryFailureRetainsSecondaryEvidence(
        bool closeConnection,
        bool canceled
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Exception primary = canceled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Primary work failure.");

        var secondary = new InvalidOperationException("Secondary cleanup failure.");
        var failed = Task.FromException(secondary);
        var resource = new CleanupResource(failed);
        var connection = new CleanupConnection(failed);

        // Act
        var failure = await Record.ExceptionAsync(() => closeConnection
            ? SafeMigrationAnalysisCleanup.CloseAsync(connection, primary)
            : SafeMigrationAnalysisCleanup
                .DisposeAsync(resource, primary, DisposeEvidence)
                .AsTask());

        // Assert
        Assert.Null(failure);
        Assert.Same(secondary,
            primary.Data[closeConnection
                ? CloseEvidence
                : DisposeEvidence]);

        if (canceled)
        {
            Assert.Equal(cancellation.Token,
                Assert.IsType<OperationCanceledException>(primary)
                    .CancellationToken);
        }
    }

    /// <summary>A cleanup-only fault remains a real failure rather than a successful result.</summary>
    /// <param name="closeConnection">Whether the tested boundary is connection close.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupAsync_WithoutPrimaryFailurePropagatesCleanupFailure(
        bool closeConnection
    )
    {
        // Arrange
        var secondary = new InvalidOperationException("Cleanup-only failure.");
        var failed = Task.FromException(secondary);
        var resource = new CleanupResource(failed);
        var connection = new CleanupConnection(failed);

        // Act
        var failure = await Record.ExceptionAsync(() => closeConnection
            ? SafeMigrationAnalysisCleanup.CloseAsync(connection, null)
            : SafeMigrationAnalysisCleanup
                .DisposeAsync(resource, null, DisposeEvidence)
                .AsTask());

        // Assert
        Assert.Same(secondary, failure);
    }

    /// <summary>Multiple cleanup failures at the same stage remain observable in their original order.</summary>
    [Fact]
    public async Task DisposeAsync_RepeatedCleanupFailuresPreserveAllEvidence()
    {
        // Arrange
        var primary = new InvalidOperationException("Primary work failure.");
        var first = new InvalidOperationException("First cleanup failure.");
        var second = new InvalidOperationException("Second cleanup failure.");
        var firstResource = new CleanupResource(Task.FromException(first));
        var secondResource = new CleanupResource(Task.FromException(second));

        // Act
        await SafeMigrationAnalysisCleanup.DisposeAsync(firstResource, primary, DisposeEvidence);
        await SafeMigrationAnalysisCleanup.DisposeAsync(secondResource, primary, DisposeEvidence);

        // Assert
        var failures = Assert.IsType<AggregateException>(primary.Data[DisposeEvidence]);
        Assert.Collection(
            failures.InnerExceptions,
            exception => Assert.Same(first, exception),
            exception => Assert.Same(second, exception));
    }

    /// <summary>A failed acquisition does not require a fabricated disposable resource.</summary>
    [Fact]
    public async Task DisposeAsync_NoAcquiredResourceCompletesWithoutFailure()
    {
        // Arrange
        var primary = new InvalidOperationException("Acquisition did not complete.");

        // Act
        await SafeMigrationAnalysisCleanup.DisposeAsync(null, primary, DisposeEvidence);

        // Assert
        Assert.Empty(primary.Data);
    }

    /// <summary>A failing diagnostic extension point cannot replace the work failure being preserved.</summary>
    [Fact]
    public async Task DisposeAsync_ThrowingExceptionDataDoesNotReplacePrimaryFailure()
    {
        // Arrange
        var primary = new ThrowingDataException();
        var secondary = new InvalidOperationException("Secondary cleanup failure.");
        var resource = new CleanupResource(Task.FromException(secondary));

        // Act
        var failure = await Record.ExceptionAsync(() =>
            SafeMigrationAnalysisCleanup
                .DisposeAsync(resource, primary, DisposeEvidence)
                .AsTask());

        // Assert
        Assert.Null(failure);
        Assert.True(resource.DisposeStarted);
        Assert.Equal(1, primary.DataAccessCount);
    }

    /// <summary>Models a custom exception that refuses diagnostic dictionary access.</summary>
    private sealed class ThrowingDataException : Exception
    {
        /// <summary>Gets the number of attempted diagnostic accesses.</summary>
        public int DataAccessCount { get; private set; }

        /// <inheritdoc />
        public override IDictionary Data
        {
            get
            {
                DataAccessCount++;

                throw new InvalidOperationException("Diagnostic data is unavailable.");
            }
        }
    }

    /// <summary>Records actual disposal admission and exposes test-controlled completion.</summary>
    private sealed class CleanupResource : IAsyncDisposable
    {
        private readonly Task _completion;

        /// <summary>Creates a resource whose actual disposal completes with the supplied task.</summary>
        /// <param name="completion">The actual disposal operation, not a cancelable observer.</param>
        public CleanupResource(
            Task completion
        )
        {
            _completion = completion;
        }

        /// <summary>Gets whether disposal has been admitted.</summary>
        public bool DisposeStarted { get; private set; }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            DisposeStarted = true;

            return new ValueTask(_completion);
        }
    }

    /// <summary>Exposes a controlled native close without allocating external database resources.</summary>
    private sealed class CleanupConnection : DbConnection
    {
        private readonly Task _completion;

        /// <summary>Creates a connection with test-controlled actual close completion.</summary>
        /// <param name="completion">The actual native close task.</param>
        public CleanupConnection(
            Task completion
        )
        {
            _completion = completion;
        }

        /// <summary>Gets whether the native close has started.</summary>
        public bool CloseStarted { get; private set; }

        /// <inheritdoc />
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        /// <inheritdoc />
        public override string Database => "cleanup-probe";

        /// <inheritdoc />
        public override string DataSource => "cleanup-probe";

        /// <inheritdoc />
        public override string ServerVersion => "1";

        /// <inheritdoc />
        public override ConnectionState State => ConnectionState.Open;

        /// <inheritdoc />
        public override Task CloseAsync()
        {
            CloseStarted = true;

            return _completion;
        }

        /// <inheritdoc />
        public override void Close() => throw new NotSupportedException("The test requires actual asynchronous close.");

        /// <inheritdoc />
        public override void Open() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void ChangeDatabase(
            string databaseName
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
