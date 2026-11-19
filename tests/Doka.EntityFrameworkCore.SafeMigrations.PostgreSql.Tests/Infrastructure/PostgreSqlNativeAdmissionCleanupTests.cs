namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Faults each advisory-fixture cleanup stage without abandoning its owner or actual producer.</summary>
public sealed class PostgreSqlNativeAdmissionCleanupTests
{
    /// <summary>Successful and failing release stages retain every late native failure and readiness identity.</summary>
    /// <param name="unlockFails">Whether native unlock fails before the lock owner is closed.</param>
    /// <param name="closeFails">Whether closing the physical lock owner fails.</param>
    /// <param name="cancellationFails">Whether an owned cancellation callback throws.</param>
    /// <param name="producerFails">Whether the actual producer eventually faults independently.</param>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, true, true)]
    public async Task EveryCleanupStageClosesTheLockOwnerAndDrainsActualWork(
        bool unlockFails,
        bool closeFails,
        bool cancellationFails,
        bool producerFails
    )
    {
        // Arrange
        var primaryFailure = new InvalidOperationException("readiness sentinel");
        var unlockFailure = new InvalidOperationException("unlock sentinel");
        var closeFailure = new InvalidOperationException("close sentinel");
        var cancellationFailure = new InvalidOperationException("cancellation sentinel");
        var producerFailure = new InvalidOperationException("late producer sentinel");
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = new PostgreSqlFailurePriorityConnection
        {
            ExecutionFailure = unlockFails
                ? unlockFailure
                : null,
            CloseFailure = closeFails
                ? closeFailure
                : null,
            CloseCompletion = releaseClose.Task,
        };

        using var releaseCommand = observer.CreateCommand();
        releaseCommand.CommandText = "SELECT pg_catalog.pg_advisory_unlock(171071);";
        using var cancellation = new CancellationTokenSource();
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationCalls = 0;
        using var registration = cancellation.Token.Register(() =>
        {
            Interlocked.Increment(ref cancellationCalls);
            cancellationObserved.TrySetResult();
            if (cancellationFails)
            {
                throw cancellationFailure;
            }
        });

        bool closeEntered;
        bool completedBeforeClose;
        Exception? failure;

        // Act
        var cleanup = Record.ExceptionAsync(() => PostgreSqlNativeAdmission.CompleteAsync(
            primaryFailure,
            execution.Task,
            observer,
            releaseCommand,
            cancellation));

        try
        {
            await Task.WhenAny(observer.CloseStarted.Task, cleanup);
            closeEntered = observer.CloseStarted.Task.IsCompleted;
            completedBeforeClose = cleanup.IsCompleted;
        }
        finally
        {
            releaseClose.TrySetResult();

            // WHY: Keep the producer pending through the release stage so the tested boundary
            // must request cancellation, including when release or close has already faulted.
            await Task.WhenAny(cancellationObserved.Task, cleanup);
            if (producerFails)
            {
                execution.TrySetException(producerFailure);
            }
            else
            {
                execution.TrySetResult();
            }

            failure = await cleanup;

            // WHY: Negative controls must still drain their fake producer when the deliberately broken target does not.
            _ = await Record.ExceptionAsync(() => execution.Task);
        }

        // Assert
        Assert.True(closeEntered);
        Assert.False(completedBeforeClose);
        Assert.True(execution.Task.IsCompleted);
        Assert.Equal(1, observer.AsyncCloseCount);
        Assert.Equal(1, cancellationCalls);
        if (!unlockFails
            && !closeFails
            && !cancellationFails
            && !producerFails)
        {
            Assert.Null(failure);

            return;
        }

        var aggregate = Assert.IsType<AggregateException>(failure);
        List<Exception> expected = [primaryFailure];
        if (unlockFails)
        {
            expected.Add(unlockFailure);
        }

        if (closeFails)
        {
            expected.Add(closeFailure);
        }

        if (cancellationFails)
        {
            expected.Add(cancellationFailure);
        }

        if (producerFails)
        {
            expected.Add(producerFailure);
        }

        Assert.Equal(expected.Count, aggregate.InnerExceptions.Count);

        // WHY: Failure order and object identity distinguish retained causes from substituted wrappers.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < expected.Count; ordinal++)
        {
            Assert.Same(expected[ordinal], aggregate.InnerExceptions[ordinal]);
        }
    }
}
