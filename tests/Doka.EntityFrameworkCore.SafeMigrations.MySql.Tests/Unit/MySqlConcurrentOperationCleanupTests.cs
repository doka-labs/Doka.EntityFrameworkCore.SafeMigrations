namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Proves that native MySQL test coordinators retain work ownership through failed cleanup.</summary>
public sealed class MySqlConcurrentOperationCleanupTests
{
    /// <summary>Rollback and cancellation callback failures cannot abandon the actual atomic-add task.</summary>
    /// <param name="hasPrimaryFailure">Whether the coordinator already has an original failure.</param>
    /// <param name="throwingCancellation">Whether requesting cancellation also fails.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AtomicAddCleanupDrainsPendingWorkAfterFailedRollback(
        bool hasPrimaryFailure,
        bool throwingCancellation
    )
    {
        // Arrange
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executionCancellation = new CancellationTokenSource();
        var callbackFailure = new InvalidOperationException("Controlled cancellation callback failure.");
        Exception? primaryFailure = hasPrimaryFailure
            ? new InvalidOperationException("Original coordination failure.")
            : null;

        using var registration = executionCancellation.Token.Register(() =>
        {
            cancellationObserved.SetResult();
            if (throwingCancellation)
            {
                throw callbackFailure;
            }
        });

        var method = typeof(MySqlSafeMigrationIntegrationTests).GetMethod(
            "FinishAtomicAddConcurrentAttemptAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        // Act
        // WHY: A null gate deliberately makes the real rollback adapter fail
        // synchronously without a database. This exercises the provider coordinator,
        // not just the shared cleanup helper, while its actual work remains pending.
        var cleanup = (Task)method!.Invoke(
            null,
            [primaryFailure, completion.Task, executionCancellation, null, false])!;

        await Task.WhenAny(cleanup, cancellationObserved.Task);
        var exitedBeforeWorkCompleted = cleanup.IsCompleted;
        var workWasPending = !completion.Task.IsCompleted;
        completion.SetResult();
        var failure = await Record.ExceptionAsync(() => cleanup);

        // Assert
        Assert.False(exitedBeforeWorkCompleted);
        Assert.True(workWasPending);
        Assert.True(executionCancellation.IsCancellationRequested);
        if (primaryFailure is null
            && !throwingCancellation)
        {
            Assert.IsType<NullReferenceException>(failure);
        }
        else
        {
            var aggregate = Assert.IsType<AggregateException>(failure);
            Assert.Equal(1
                + (hasPrimaryFailure
                    ? 1
                    : 0)
                + (throwingCancellation
                    ? 1
                    : 0),
                aggregate.InnerExceptions.Count);

            Assert.Contains(aggregate.InnerExceptions, static cause => cause is NullReferenceException);
            if (primaryFailure is not null)
            {
                Assert.Same(primaryFailure, aggregate.InnerExceptions[0]);
            }

            Assert.Equal(throwingCancellation,
                aggregate.InnerExceptions.Any(cause => ReferenceEquals(cause, callbackFailure)));
        }
    }

    /// <summary>An already-faulted task is still observed through the actual coordinator adapter.</summary>
    [Fact]
    public async Task AtomicAddCleanupObservesAlreadyCompletedFault()
    {
        // Arrange
        var expected = new InvalidOperationException("Completed operation failure.");
        var execution = Task.FromException(expected);
        using var cancellation = new CancellationTokenSource();
        var method = typeof(MySqlSafeMigrationIntegrationTests).GetMethod(
            "FinishAtomicAddConcurrentAttemptAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        // Act
        var cleanup = (Task)method!.Invoke(null, [null, execution, cancellation, null, true])!;
        var failure = await Record.ExceptionAsync(() => cleanup);

        // Assert
        Assert.Same(expected, failure);
        Assert.False(cancellation.IsCancellationRequested);
    }

    /// <summary>Already handled native faults are deduplicated without replacing the raw cleanup task.</summary>
    /// <param name="capturedFailure">Whether normal observation handles an expected native refusal.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicAddCleanupPreservesHandledNativeOutcome(
        bool capturedFailure
    )
    {
        // Arrange
        Exception? expected = capturedFailure
            ? new InvalidOperationException("Expected recorded native refusal.")
            : null;

        var execution = expected is null
            ? Task.CompletedTask
            : Task.FromException(expected);

        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(static () =>
            throw new InvalidOperationException("Completed work must not request cancellation."));

        var method = typeof(MySqlSafeMigrationIntegrationTests).GetMethod(
            "FinishAtomicAddConcurrentAttemptAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        // Act
        var observed = await Record.ExceptionAsync(() => execution);
        var cleanup = (Task)method!.Invoke(null, [observed, execution, cancellation, null, true])!;
        var failure = await Record.ExceptionAsync(() => cleanup);

        // Assert
        Assert.Null(failure);
        Assert.Same(expected, observed);
        Assert.False(cancellation.IsCancellationRequested);
    }

    /// <summary>A coordination error cannot hide a later raw native failure during protected drain.</summary>
    [Fact]
    public async Task AtomicAddCleanupRetainsPrimaryAndLateNativeFailure()
    {
        // Arrange
        var primaryFailure = new InvalidOperationException("Original coordination failure.");
        var nativeFailure = new InvalidOperationException("Later native operation failure.");
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() => cancellationObserved.SetResult());
        var method = typeof(MySqlSafeMigrationIntegrationTests).GetMethod(
            "FinishAtomicAddConcurrentAttemptAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        // Act
        var cleanup = (Task)method!.Invoke(
            null,
            [primaryFailure, execution.Task, cancellation, null, true])!;

        await cancellationObserved.Task;
        var exitedBeforeNativeCompletion = cleanup.IsCompleted;
        execution.SetException(nativeFailure);
        var failure = await Record.ExceptionAsync(() => cleanup);

        // Assert
        Assert.False(exitedBeforeNativeCompletion);
        var aggregate = Assert.IsType<AggregateException>(failure);

        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Same(primaryFailure, aggregate.InnerExceptions[0]);
        Assert.Same(nativeFailure, aggregate.InnerExceptions[1]);
    }
}
