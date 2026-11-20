namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Exercises real catalog transport cleanup boundaries with deterministic native failure controls.</summary>
public sealed class SafeMigrationCatalogCleanupFailureTests
{
    /// <summary>Reader cleanup does not replace callback failures or their cancellation identity.</summary>
    /// <param name="native">Whether the transport uses a native batch or sequential commands.</param>
    /// <param name="canceled">Whether the callback failure represents operation cancellation.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForEachResultSetAsync_CallbackFailureRetainsReaderCleanupEvidence(
        bool native,
        bool canceled
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Callback failed.");

        var secondary = new InvalidOperationException("Reader disposal failed.");
        var readerDisposeCompletion = Task.FromException(secondary);
        await using var connection = new CatalogProbeTestConnection(native)
        {
            ReaderDisposeCompletion = readerDisposeCompletion,
        };

        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: null);
        batch.CreateCommand()
            .CommandText = "1";

        var observedToken = CancellationToken.None;

        // Act
        var failure = await Record.ExceptionAsync(() => batch.ForEachResultSetAsync(
            (_, callbackCancellationToken) =>
            {
                observedToken = callbackCancellationToken;
                if (canceled)
                {
                    cancellation.Cancel();
                }

                return Task.FromException(primary);
            },
            cancellation.Token));

        // Assert
        Assert.Same(primary, failure);
        Assert.Equal(cancellation.Token, observedToken);
        Assert.Same(secondary, primary.Data["SafeMigrations.CatalogReaderDisposeException"]);
        Assert.Equal(1, connection.ReaderDisposals);
        if (canceled)
        {
            Assert.Equal(cancellation.Token,
                Assert.IsType<OperationCanceledException>(failure)
                    .CancellationToken);
        }
    }

    /// <summary>A cleanup-only reader failure remains a failure rather than being silently suppressed.</summary>
    /// <param name="native">Whether the transport uses a native batch.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachResultSetAsync_ReaderCleanupOnlyFailurePropagates(
        bool native
    )
    {
        // Arrange
        var secondary = new InvalidOperationException("Reader disposal failed.");
        var readerDisposeCompletion = Task.FromException(secondary);
        await using var connection = new CatalogProbeTestConnection(native)
        {
            ReaderDisposeCompletion = readerDisposeCompletion,
        };

        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: null);
        batch.CreateCommand()
            .CommandText = "1";

        // Act
        var failure = await Record.ExceptionAsync(() => batch.ForEachResultSetAsync(
            (_, _) => Task.CompletedTask,
            CancellationToken.None));

        // Assert
        Assert.Same(secondary, failure);
        Assert.Equal(1, connection.ReaderDisposals);
    }

    /// <summary>Sequential cleanup releases every owned command and retains later failures in order.</summary>
    [Fact]
    public async Task DisposeAsync_SequentialFailureStillReleasesEveryCommand()
    {
        // Arrange
        var first = new InvalidOperationException("First command disposal failed.");
        var second = new InvalidOperationException("Second command disposal failed.");
        await using var connection = new CatalogProbeTestConnection(native: false);
        connection.CommandDisposeCompletions.AddRange([Task.FromException(first), Task.FromException(second)]);
        var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: null);
        batch.CreateCommand()
            .CommandText = "1";

        batch.CreateCommand()
            .CommandText = "2";

        batch.CreateCommand()
            .CommandText = "3";

        // Act
        var failure = await Record.ExceptionAsync(() => batch
            .DisposeAsync()
            .AsTask());

        // Assert
        Assert.Same(first, failure);
        Assert.Same(second, first.Data["SafeMigrations.CatalogCommandDisposeException"]);
        Assert.Contains(
            nameof(CatalogProbeTestConnection),
            Assert.IsType<string>(first.StackTrace),
            StringComparison.Ordinal);

        Assert.Equal(3, connection.Disposals);
    }

    /// <summary>The independent-probe owner cannot replace its callback failure with batch cleanup.</summary>
    /// <param name="native">Whether the transport owns one native batch or sequential commands.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_CallbackFailureRetainsBatchCleanupEvidence(
        bool native
    )
    {
        // Arrange
        var primary = new InvalidOperationException("Probe callback failed.");
        var secondary = new InvalidOperationException("Owned batch disposal failed.");
        var batchDisposeCompletion = native
            ? Task.FromException(secondary)
            : Task.CompletedTask;

        await using var connection = new CatalogProbeTestConnection(native)
        {
            BatchDisposeCompletion = batchDisposeCompletion,
        };

        if (!native)
        {
            connection.CommandDisposeCompletions.Add(Task.FromException(secondary));
        }

        // Act
        var failure = await Record.ExceptionAsync(() => SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            operationCount: 1,
            maximumPayloadBytes: 4096,
            commandTimeout: null,
            static (command, offset) =>
            {
                command.CommandText = "1";

                return new SafeMigrationCatalogProbeStatement(1, 0, offset);
            },
            (_, _, _, _) => Task.FromException(primary),
            transaction: null,
            maximumParameters: SafeMigrationCatalogQueryLimits.MaximumParameters,
            CancellationToken.None));

        // Assert
        Assert.Same(primary, failure);
        Assert.Same(secondary, primary.Data["SafeMigrations.CatalogBatchDisposeException"]);
        Assert.Equal(1, connection.ReaderDisposals);
        Assert.Equal(1, connection.Disposals);
    }

    /// <summary>Construction failures remain primary when the already-acquired batch fails during cleanup.</summary>
    /// <param name="native">Whether the transport uses the native or sequential path.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_BuilderFailureRetainsBatchCleanupEvidence(
        bool native
    )
    {
        // Arrange
        var primary = new InvalidOperationException("Probe builder failed.");
        var secondary = new InvalidOperationException("Owned batch disposal failed.");
        var batchDisposeCompletion = native
            ? Task.FromException(secondary)
            : Task.CompletedTask;

        await using var connection = new CatalogProbeTestConnection(native)
        {
            BatchDisposeCompletion = batchDisposeCompletion,
        };

        if (!native)
        {
            connection.CommandDisposeCompletions.Add(Task.FromException(secondary));
        }

        // Act
        var failure = await Record.ExceptionAsync(() => SafeMigrationCatalogProbeBatch.ReadAsync(
            connection,
            operationCount: 1,
            maximumPayloadBytes: 4096,
            commandTimeout: null,
            (_, _) => throw primary,
            (_, _, _, _) => Task.CompletedTask,
            transaction: null,
            maximumParameters: SafeMigrationCatalogQueryLimits.MaximumParameters,
            CancellationToken.None));

        // Assert
        Assert.Same(primary, failure);
        Assert.Same(secondary, primary.Data["SafeMigrations.CatalogBatchDisposeException"]);
        Assert.Equal(0, connection.ReaderDisposals);
        Assert.Equal(1, connection.Disposals);
    }

    /// <summary>Callback failure does not permit return before actual reader disposal has finished.</summary>
    /// <param name="native">Whether the transport owns a native batch.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachResultSetAsync_CallbackFailureDrainsActualReaderCleanup(
        bool native
    )
    {
        // Arrange
        var primary = new InvalidOperationException("Callback failed.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new CatalogProbeTestConnection(native)
        {
            ReaderDisposeStarted = entered,
            ReaderDisposeCompletion = release.Task,
        };

        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: null);
        batch.CreateCommand()
            .CommandText = "1";

        // Act
        var operation = batch.ForEachResultSetAsync((_, _) => Task.FromException(primary), CancellationToken.None);
        var reachedBoundary = ReferenceEquals(await Task.WhenAny(operation, entered.Task), entered.Task);
        var returnedBeforeRelease = operation.IsCompleted;
        release.TrySetResult();
        var failure = await Record.ExceptionAsync(() => operation);

        // Assert
        Assert.True(reachedBoundary);
        Assert.False(returnedBeforeRelease);
        Assert.Same(primary, failure);
        Assert.Equal(1, connection.ReaderDisposals);
    }

    /// <summary>Sequential cleanup does not advance to another command until the current one is finished.</summary>
    [Fact]
    public async Task DisposeAsync_DelayedCommandCleanupRetainsSequentialOwnership()
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new CatalogProbeTestConnection(native: false)
        {
            CommandDisposeStarted = entered,
        };

        connection.CommandDisposeCompletions.Add(release.Task);
        var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: null);
        batch.CreateCommand()
            .CommandText = "1";

        batch.CreateCommand()
            .CommandText = "2";

        // Act
        var operation = batch
            .DisposeAsync()
            .AsTask();

        var reachedBoundary = ReferenceEquals(await Task.WhenAny(operation, entered.Task), entered.Task);
        var completedBeforeRelease = connection.Disposals;
        var returnedBeforeRelease = operation.IsCompleted;
        release.TrySetResult();
        await operation;

        // Assert
        Assert.True(reachedBoundary);
        Assert.False(returnedBeforeRelease);
        Assert.Equal(0, completedBeforeRelease);
        Assert.Equal(2, connection.Disposals);
    }
}
