namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Guards complete borrowed-work observation on successful and adversarial cleanup paths.</summary>
public sealed class OwnedTestOperationCleanupTests
{
    /// <summary>An independent barrier is released even when no operation was started.</summary>
    [Fact]
    public async Task NoPendingWorkStillReleasesBarrier()
    {
        // Arrange
        var released = false;
        using var cancellation = new CancellationTokenSource();

        // Act
        await OwnedTestOperationCleanup.CompleteAsync(
            null,
            null,
            () =>
            {
                released = true;

                return Task.CompletedTask;
            },
            cancellation);

        // Assert
        Assert.True(released);
        Assert.False(cancellation.IsCancellationRequested);
    }

    /// <summary>Completed successful work does not provoke unrelated cancellation callbacks.</summary>
    [Fact]
    public async Task CompletedSuccessDoesNotCancelOrDisposeTheSource()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(static () => throw new InvalidOperationException());

        // Act
        await OwnedTestOperationCleanup.CompleteAsync(null, Task.CompletedTask, null, cancellation);
        var retainedToken = cancellation.Token;

        // Assert
        Assert.True(retainedToken.CanBeCanceled);
        Assert.False(retainedToken.IsCancellationRequested);
    }

    /// <summary>Release happens before cancellation and the actual work reaches completion.</summary>
    [Fact]
    public async Task ReleaseThenCancellationUnblocksAndDrainsTheActualWork()
    {
        // Arrange
        var order = new List<string>();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() =>
        {
            order.Add("cancel");
            pending.SetResult();
        });

        // Act
        await OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending.Task,
            () =>
            {
                order.Add("release");

                return Task.CompletedTask;
            },
            cancellation);

        // Assert
        Assert.Equal(["release", "cancel"], order);
        Assert.True(pending.Task.IsCompletedSuccessfully);
    }

    /// <summary>Cancellation cannot stand in for completion of work that still borrows an owner.</summary>
    [Fact]
    public async Task DelayedWorkRemainsOwnedUntilItActuallyCompletes()
    {
        // Arrange
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new BorrowedOwner();
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() => cancellationObserved.SetResult());

        // Act
        var cleanup = OwnedTestOperationCleanup.CompleteAsync(null, pending.Task, null, cancellation);
        await cancellationObserved.Task;
        var returnedWhileWorkWasPending = cleanup.IsCompleted;
        var ownerWasDisposedWhilePending = owner.IsDisposed;
        pending.SetResult();
        await cleanup;
        owner.Dispose();

        // Assert
        Assert.False(returnedWhileWorkWasPending);
        Assert.False(ownerWasDisposedWhilePending);
        Assert.True(owner.IsDisposed);
        Assert.True(pending.Task.IsCompletedSuccessfully);
    }

    /// <summary>A synchronous or asynchronous release failure cannot bypass cancellation and draining.</summary>
    /// <param name="synchronous">Whether the unblocker throws before returning its task.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseFailureStillCancelsAndDrains(
        bool synchronous
    )
    {
        // Arrange
        var failure = new InvalidOperationException("release failure");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() => pending.SetResult());
        Func<Task> release = synchronous
            ? () => throw failure
            : () => Task.FromException(failure);

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending.Task,
            release,
            cancellation));

        // Assert
        Assert.Same(failure, observed);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(pending.Task.IsCompletedSuccessfully);
    }

    /// <summary>Throwing cancellation callbacks do not release owners while borrowed work is pending.</summary>
    [Fact]
    public async Task CancellationCallbackFailureStillDrainsDelayedWork()
    {
        // Arrange
        var failure = new InvalidOperationException("cancellation callback failure");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() =>
        {
            callbackEntered.SetResult();

            throw failure;
        });

        // Act
        var cleanup = OwnedTestOperationCleanup.CompleteAsync(null, pending.Task, null, cancellation);
        await callbackEntered.Task;
        var returnedWhilePending = cleanup.IsCompleted;
        pending.SetResult();
        var observed = await Record.ExceptionAsync(() => cleanup);

        // Assert
        Assert.False(returnedWhilePending);
        Assert.Same(failure, observed);
        Assert.True(pending.Task.IsCompletedSuccessfully);
    }

    /// <summary>Cancellation exceptions thrown by cleanup itself are never mistaken for canceled work.</summary>
    /// <param name="releaseFails">Whether release, rather than a cancellation callback, throws.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupStageCancellationFailureRemainsObservable(
        bool releaseFails
    )
    {
        // Arrange
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var failure = new OperationCanceledException(cancellation.Token);
        using var registration = cancellation.Token.Register(() =>
        {
            pending.SetCanceled(cancellation.Token);
            if (!releaseFails)
            {
                throw failure;
            }
        });

        Func<Task>? release = releaseFails
            ? () => throw failure
            : null;

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending.Task,
            release,
            cancellation));

        // Assert
        Assert.Same(failure, observed);
        Assert.True(pending.Task.IsCanceled);
    }

    /// <summary>An unavailable cancellation source cannot prevent pending work from being observed.</summary>
    [Fact]
    public async Task DisposedCancellationSourceStillDrainsDelayedWork()
    {
        // Arrange
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        cancellation.Dispose();

        // Act
        var cleanup = OwnedTestOperationCleanup.CompleteAsync(null, pending.Task, null, cancellation);
        var returnedWhilePending = cleanup.IsCompleted;
        pending.SetResult();
        var observed = await Record.ExceptionAsync(() => cleanup);

        // Assert
        Assert.False(returnedWhilePending);
        Assert.IsType<ObjectDisposedException>(observed);
        Assert.True(pending.Task.IsCompletedSuccessfully);
    }

    /// <summary>A failed token acquisition does not trigger repeated access when pending work is canceled.</summary>
    [Fact]
    public async Task UnavailableTokenCannotCertifyPendingCancellation()
    {
        // Arrange
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        cancellation.Dispose();

        // Act
        var cleanup = OwnedTestOperationCleanup.CompleteAsync(null, pending.Task, null, cancellation);
        pending.SetCanceled(CancellationToken.None);
        var observed = await Record.ExceptionAsync(() => cleanup);

        // Assert
        var aggregate = Assert.IsType<AggregateException>(observed);

        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.IsType<ObjectDisposedException>(aggregate.InnerExceptions[0]);
        Assert.IsType<TaskCanceledException>(aggregate.InnerExceptions[1]);
    }

    /// <summary>Reobserving the caller's original failure does not throw it a second time.</summary>
    [Fact]
    public async Task PrimaryFailureAloneRemainsTheCallersResponsibility()
    {
        // Arrange
        var primary = new InvalidOperationException("primary failure");
        var pending = Task.FromException(primary);

        // Act
        var cleanupFailure = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            primary,
            pending,
            null,
            null));

        // Assert
        Assert.Null(cleanupFailure);
        Assert.Same(primary, Assert.Single(pending.Exception!.InnerExceptions));
    }

    /// <summary>All stages retain their failures after the original primary object.</summary>
    [Fact]
    public async Task PrimaryReleaseCancellationAndEveryWorkFaultRemainObservable()
    {
        // Arrange
        var primary = new InvalidOperationException("primary");
        var releaseFailure = new InvalidOperationException("release");
        var firstCallbackFailure = new InvalidOperationException("first callback");
        var secondCallbackFailure = new InvalidOperationException("second callback");
        var firstWorkFailure = new InvalidOperationException("first work");
        var secondWorkFailure = new InvalidOperationException("second work");
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Task.WhenAll(first.Task, second.Task);
        using var cancellation = new CancellationTokenSource();
        using var firstRegistration = cancellation.Token.Register(() => throw firstCallbackFailure);
        using var secondRegistration = cancellation.Token.Register(() => throw secondCallbackFailure);

        // Act
        var cleanup = OwnedTestOperationCleanup.CompleteAsync(
            primary,
            pending,
            () => throw releaseFailure,
            cancellation);

        var returnedWhilePending = cleanup.IsCompleted;
        first.SetException(firstWorkFailure);
        second.SetException(secondWorkFailure);
        var observed = await Record.ExceptionAsync(() => cleanup);

        // Assert
        var aggregate = Assert.IsType<AggregateException>(observed);

        Assert.False(returnedWhilePending);
        Assert.Equal(6, aggregate.InnerExceptions.Count);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Same(releaseFailure, aggregate.InnerExceptions[1]);
        Assert.Contains(firstCallbackFailure,
            aggregate
                .InnerExceptions
                .Skip(2)
                .Take(2));

        Assert.Contains(secondCallbackFailure,
            aggregate
                .InnerExceptions
                .Skip(2)
                .Take(2));

        Assert.Contains(firstWorkFailure, aggregate.InnerExceptions.Skip(4));
        Assert.Contains(secondWorkFailure, aggregate.InnerExceptions.Skip(4));
        Assert.True(pending.IsCompleted);
    }

    /// <summary>A primary aggregate already owns its fault leaves, so cleanup does not add them again.</summary>
    [Fact]
    public async Task PrimaryAggregateLeavesAreNotDuplicated()
    {
        // Arrange
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second");
        var primary = new AggregateException(first, new AggregateException(second));
        var pending = Task.WhenAll(Task.FromException(first), Task.FromException(second));

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            primary,
            pending,
            () => Task.FromException(primary),
            null));

        // Assert
        Assert.Null(observed);
        Assert.Same(first, primary.InnerExceptions[0]);
    }

    /// <summary>The same exception crossing cleanup stages is retained once by reference.</summary>
    [Fact]
    public async Task RepeatedFailureAcrossCleanupStagesIsRetainedOnce()
    {
        // Arrange
        var failure = new InvalidOperationException("same failure");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() =>
        {
            pending.SetException(failure);

            throw failure;
        });

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending.Task,
            () => Task.FromException(failure),
            cancellation,
            afterDrain: () => Task.FromException(failure)));

        // Assert
        Assert.Same(failure, observed);
        Assert.True(pending.Task.IsFaulted);
    }

    /// <summary>Only the actual canceled owned token certifies an expected pending-work cancellation.</summary>
    /// <param name="completedBeforeCleanup">Whether owned cancellation completed before cleanup began.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledOwnedWorkDoesNotAddACleanupFailure(
        bool completedBeforeCleanup
    )
    {
        // Arrange
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() => pending.SetCanceled(cancellation.Token));
        if (completedBeforeCleanup)
        {
            cancellation.Cancel();
        }

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending.Task,
            null,
            cancellation));

        // Assert
        Assert.Null(observed);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(pending.Task.IsCanceled);
    }

    /// <summary>Unrelated and default-token exceptions are not cleanup-owned cancellation.</summary>
    /// <param name="defaultToken">Whether the fault carries no cancellation token at all.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedCancellationRemainsObservable(
        bool defaultToken
    )
    {
        // Arrange
        using var owned = new CancellationTokenSource();
        using var unrelated = new CancellationTokenSource();
        owned.Cancel();
        unrelated.Cancel();
        var failure = new OperationCanceledException(defaultToken
            ? CancellationToken.None
            : unrelated.Token);

        var pending = Task.FromException(failure);

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending,
            null,
            owned));

        // Assert
        Assert.Same(failure, observed);
    }

    /// <summary>Already faulted work is still observed without activating irrelevant cancellation callbacks.</summary>
    [Fact]
    public async Task AlreadyFaultedWorkIsObservedWithoutRequestingCancellation()
    {
        // Arrange
        var failure = new InvalidOperationException("completed fault");
        var pending = Task.FromException(failure);
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(static () => throw new InvalidOperationException());

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending,
            null,
            cancellation));

        // Assert
        Assert.Same(failure, observed);
        Assert.False(cancellation.IsCancellationRequested);
    }

    /// <summary>A composite task contributes every fault, not only the exception exposed by await.</summary>
    [Fact]
    public async Task EveryAlreadyCompletedWorkFaultIsRetained()
    {
        // Arrange
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second");
        var pending = Task.WhenAll(Task.FromException(first), Task.FromException(second));

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending,
            null,
            null));

        // Assert
        var aggregate = Assert.IsType<AggregateException>(observed);

        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains(first, aggregate.InnerExceptions);
        Assert.Contains(second, aggregate.InnerExceptions);
    }

    /// <summary>A canceled task can recreate its await exception without creating a distinct cleanup failure.</summary>
    [Fact]
    public async Task PrimaryTaskCancellationIsNotDuplicatedByReobservation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pending = Task.FromCanceled(cancellation.Token);
        var primary = await Record.ExceptionAsync(() => pending);

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            primary,
            pending,
            null,
            null));

        // Assert
        Assert.IsType<TaskCanceledException>(primary);
        Assert.Null(observed);
    }

    /// <summary>A canceled task retained inside the primary aggregate is not a new cleanup failure.</summary>
    [Fact]
    public async Task PrimaryAggregateTaskCancellationIsNotDuplicatedByReobservation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pending = Task.FromCanceled(cancellation.Token);
        var canceled = await Record.ExceptionAsync(() => pending);
        var primary = new AggregateException(new InvalidOperationException("primary"), canceled!);

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            primary,
            pending,
            null,
            null));

        // Assert
        Assert.IsType<TaskCanceledException>(canceled);
        Assert.Null(observed);
    }

    /// <summary>A release failure is observable even when there is no asynchronous work left to drain.</summary>
    [Fact]
    public async Task ReleaseFailureWithoutWorkKeepsItsIdentity()
    {
        // Arrange
        var failure = new InvalidOperationException("release without work");

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            null,
            () => throw failure,
            null));

        // Assert
        Assert.Same(failure, observed);
    }

    /// <summary>Same-session final cleanup cannot begin while its actual command is still pending.</summary>
    [Fact]
    public async Task AfterDrainWaitsForTheActualOperationToFinish()
    {
        // Arrange
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalCleanupRan = false;
        var workWasCompletedAtFinalCleanup = false;
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() => cancellationObserved.SetResult());

        // Act
        var cleanup = OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending.Task,
            null,
            cancellation,
            afterDrain: () =>
            {
                finalCleanupRan = true;
                workWasCompletedAtFinalCleanup = pending.Task.IsCompleted;

                return Task.CompletedTask;
            });

        await cancellationObserved.Task;
        var finalCleanupRanBeforeWorkCompletion = finalCleanupRan;
        pending.SetResult();
        await cleanup;

        // Assert
        Assert.False(finalCleanupRanBeforeWorkCompletion);
        Assert.True(finalCleanupRan);
        Assert.True(workWasCompletedAtFinalCleanup);
    }

    /// <summary>No earlier cleanup phase can skip final session cleanup.</summary>
    /// <param name="failingStage">The independently failing owner or cleanup stage.</param>
    [Theory]
    [InlineData("primary")]
    [InlineData("release")]
    [InlineData("cancellation")]
    [InlineData("work")]
    public async Task EarlierFailuresCannotSkipAfterDrain(
        string failingStage
    )
    {
        // Arrange
        var failure = new InvalidOperationException(failingStage);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalCleanupRan = false;
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() =>
        {
            if (failingStage == "work")
            {
                pending.SetException(failure);
            }
            else
            {
                pending.SetResult();
            }

            if (failingStage == "cancellation")
            {
                throw failure;
            }
        });

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            failingStage == "primary"
                ? failure
                : null,
            pending.Task,
            () => failingStage == "release"
                ? throw failure
                : Task.CompletedTask,
            cancellation,
            afterDrain: () =>
            {
                finalCleanupRan = true;

                return Task.CompletedTask;
            }));

        // Assert
        Assert.True(finalCleanupRan);
        Assert.True(pending.Task.IsCompleted);
        if (failingStage == "primary")
        {
            Assert.Null(observed);
        }
        else
        {
            Assert.Same(failure, observed);
        }
    }

    /// <summary>Final cleanup retains its own exception when every earlier stage succeeded.</summary>
    [Fact]
    public async Task AfterDrainFailureKeepsItsIdentity()
    {
        // Arrange
        var failure = new InvalidOperationException("final cleanup");

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            Task.CompletedTask,
            null,
            null,
            afterDrain: () => Task.FromException(failure)));

        // Assert
        Assert.Same(failure, observed);
    }

    /// <summary>Final cleanup still runs when no work was started.</summary>
    [Fact]
    public async Task AfterDrainRunsWithoutPendingWork()
    {
        // Arrange
        var finalCleanupRan = false;

        // Act
        await OwnedTestOperationCleanup.CompleteAsync(
            null,
            null,
            null,
            null,
            afterDrain: () =>
            {
                finalCleanupRan = true;

                return Task.CompletedTask;
            });

        // Assert
        Assert.True(finalCleanupRan);
    }

    /// <summary>Every final cleanup fault follows the retained primary, release and operation errors.</summary>
    [Fact]
    public async Task AfterDrainFaultsFollowAllEarlierFailures()
    {
        // Arrange
        var primary = new InvalidOperationException("primary");
        var releaseFailure = new InvalidOperationException("release");
        var firstWorkFailure = new InvalidOperationException("first work");
        var secondWorkFailure = new InvalidOperationException("second work");
        var firstFinalFailure = new InvalidOperationException("first final cleanup");
        var secondFinalFailure = new InvalidOperationException("second final cleanup");
        var pending = Task.WhenAll(Task.FromException(firstWorkFailure), Task.FromException(secondWorkFailure));

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            primary,
            pending,
            () => throw releaseFailure,
            null,
            afterDrain: () => Task.WhenAll(
                Task.FromException(firstFinalFailure),
                Task.FromException(secondFinalFailure))));

        // Assert
        var aggregate = Assert.IsType<AggregateException>(observed);

        Assert.Equal(6, aggregate.InnerExceptions.Count);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Same(releaseFailure, aggregate.InnerExceptions[1]);
        Assert.Contains(firstWorkFailure,
            aggregate
                .InnerExceptions
                .Skip(2)
                .Take(2));

        Assert.Contains(secondWorkFailure,
            aggregate
                .InnerExceptions
                .Skip(2)
                .Take(2));

        Assert.Contains(firstFinalFailure, aggregate.InnerExceptions.Skip(4));
        Assert.Contains(secondFinalFailure, aggregate.InnerExceptions.Skip(4));
    }

    /// <summary>Expected canceled work does not hide a cancellation exception thrown by final cleanup.</summary>
    [Fact]
    public async Task AfterDrainCancellationExceptionRemainsObservable()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = new OperationCanceledException(cancellation.Token);
        var pending = Task.FromCanceled(cancellation.Token);

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending,
            null,
            cancellation,
            afterDrain: () => throw failure));

        // Assert
        Assert.Same(failure, observed);
    }

    /// <summary>An empty aggregate is an actual stage failure, not the absence of a fault.</summary>
    /// <param name="stage">The independently failing cleanup or pending-work stage.</param>
    /// <param name="synchronous">Whether the callback throws before returning a task.</param>
    [Theory]
    [InlineData("release", false)]
    [InlineData("release", true)]
    [InlineData("pending", false)]
    [InlineData("afterDrain", false)]
    [InlineData("afterDrain", true)]
    public async Task EmptyAggregateStageFailureKeepsItsIdentity(
        string stage,
        bool synchronous
    )
    {
        // Arrange
        var failure = new AggregateException("stage failed");
        Func<Task> fail = synchronous
            ? () => throw failure
            : () => Task.FromException(failure);

        var pending = stage == "pending"
            ? Task.FromException(failure)
            : Task.CompletedTask;

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending,
            stage == "release"
                ? fail
                : null,
            null,
            afterDrain: stage == "afterDrain"
                ? fail
                : null));

        // Assert
        Assert.Same(failure, observed);
        Assert.Empty(failure.InnerExceptions);
    }

    /// <summary>Nested empty aggregates retain identity and their depth-first authored sibling position.</summary>
    /// <param name="stage">The independently failing cleanup or pending-work stage.</param>
    /// <param name="synchronous">Whether the callback throws before returning a task.</param>
    [Theory]
    [InlineData("release", false)]
    [InlineData("release", true)]
    [InlineData("pending", false)]
    [InlineData("afterDrain", false)]
    [InlineData("afterDrain", true)]
    public async Task MixedAggregateStageFailurePreservesAuthoredLeafOrderAndIdentity(
        string stage,
        bool synchronous
    )
    {
        // Arrange
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second");
        var empty = new AggregateException("empty but real");
        var third = new InvalidOperationException("third");
        var failure = new AggregateException(
            "authored tree",
            first,
            new AggregateException("nested", second, empty),
            empty,
            third);

        Func<Task> fail = synchronous
            ? () => throw failure
            : () => Task.FromException(failure);

        var pending = stage == "pending"
            ? Task.FromException(failure)
            : Task.CompletedTask;

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending,
            stage == "release"
                ? fail
                : null,
            null,
            afterDrain: stage == "afterDrain"
                ? fail
                : null));

        // Assert
        var aggregate = Assert.IsType<AggregateException>(observed);

        Assert.Equal(4, aggregate.InnerExceptions.Count);
        Assert.Same(first, aggregate.InnerExceptions[0]);
        Assert.Same(second, aggregate.InnerExceptions[1]);
        Assert.Same(empty, aggregate.InnerExceptions[2]);
        Assert.Same(third, aggregate.InnerExceptions[3]);
    }

    /// <summary>Empty primary leaves are deduplicated without swallowing a genuinely additional fault.</summary>
    /// <param name="nestedPrimary">Whether the primary exception owns a nested empty aggregate.</param>
    /// <param name="additionalFault">Whether pending work also contributes a new failure.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EmptyAggregatePrimaryReobservationDoesNotChangeTheFailureSet(
        bool nestedPrimary,
        bool additionalFault
    )
    {
        // Arrange
        var empty = new AggregateException("primary empty leaf");
        var ordinary = new InvalidOperationException("primary ordinary leaf");
        var additional = new InvalidOperationException("additional failure");
        var primary = nestedPrimary
            ? new AggregateException("primary", ordinary, new AggregateException("nested", empty))
            : empty;

        Exception[] repeatedLeaves = nestedPrimary
            ? [empty, new AggregateException("repeated nested", ordinary, empty)]
            : [empty, empty];

        var repeated = new AggregateException("repeated", repeatedLeaves);
        var pending = Task.FromException(additionalFault
            ? new AggregateException("pending", repeated, additional)
            : repeated);

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            primary,
            pending,
            () => Task.FromException(empty),
            null,
            afterDrain: () => Task.FromException(repeated)));

        // Assert
        if (additionalFault)
        {
            var aggregate = Assert.IsType<AggregateException>(observed);

            Assert.Equal(2, aggregate.InnerExceptions.Count);
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            Assert.Same(additional, aggregate.InnerExceptions[1]);
        }
        else
        {
            Assert.Null(observed);
        }
    }

    /// <summary>Empty siblings do not obscure an already retained pending-task cancellation.</summary>
    [Fact]
    public async Task EmptyPrimarySiblingPreservesTaskCancellationReobservation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pending = Task.FromCanceled(cancellation.Token);
        var canceled = await Record.ExceptionAsync(() => pending);
        var empty = new AggregateException("primary empty sibling");
        var primary = new AggregateException("primary", empty, new AggregateException("nested", canceled!));

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            primary,
            pending,
            null,
            null,
            afterDrain: () => Task.FromException(empty)));

        // Assert
        Assert.IsType<TaskCanceledException>(canceled);
        Assert.Null(observed);
    }

    /// <summary>Cancellation callback aggregation does not erase an empty callback exception.</summary>
    [Fact]
    public async Task EmptyCancellationCallbackAggregateRemainsObservable()
    {
        // Arrange
        var failure = new AggregateException("cancellation callback failed");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var registration = cancellation.Token.Register(() =>
        {
            pending.SetResult();

            throw failure;
        });

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending.Task,
            null,
            cancellation));

        // Assert
        Assert.Same(failure, observed);
        Assert.True(pending.Task.IsCompletedSuccessfully);
    }

    /// <summary>Deep aggregate structure is traversed without recursion or a depth-based loss of failures.</summary>
    [Fact]
    public async Task DeepAggregateStructurePreservesAllActualLeaves()
    {
        // Arrange
        var empty = new AggregateException("empty leaf");
        var ordinary = new InvalidOperationException("ordinary leaf");
        Exception failure = new AggregateException("inner", empty, ordinary);

        // WHY: This is structural depth coverage, not an execution budget or an accepted-input limit.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var depth = 0; depth < 10_000; depth++)
        {
            failure = new AggregateException("nested", failure);
        }

        var pending = Task.FromException(failure);

        // Act
        var observed = await Record.ExceptionAsync(() => OwnedTestOperationCleanup.CompleteAsync(
            null,
            pending,
            null,
            null));

        // Assert
        var aggregate = Assert.IsType<AggregateException>(observed);

        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Same(empty, aggregate.InnerExceptions[0]);
        Assert.Same(ordinary, aggregate.InnerExceptions[1]);
    }

    /// <summary>A caller-owned resource remains outside the helper's disposal authority.</summary>
    private sealed class BorrowedOwner : IDisposable
    {
        /// <summary>Creates an undisposed lifetime observer.</summary>
        public BorrowedOwner() { }

        /// <summary>Gets whether the owning caller has disposed this observer.</summary>
        public bool IsDisposed { get; private set; }

        /// <inheritdoc />
        public void Dispose() => IsDisposed = true;
    }
}
