namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Exercises the exact blocked-operation coordinator without native timing or a database.</summary>
public sealed class PostgreSqlOwnedConcurrentAttemptTests
{
    /// <summary>
    /// Observer and commit failures cannot finish cleanup while their native producer remains pending.
    /// </summary>
    /// <param name="stage">The forward coordination stage that fails.</param>
    /// <param name="releaseFails">Whether releasing the independent blocker also fails.</param>
    /// <param name="cancelFails">Whether an owned cancellation callback also fails.</param>
    [Theory]
    [InlineData("observe", false, false)]
    [InlineData("observe", true, false)]
    [InlineData("observe", false, true)]
    [InlineData("observe", true, true)]
    [InlineData("commit", false, false)]
    [InlineData("commit", true, false)]
    [InlineData("commit", false, true)]
    [InlineData("commit", true, true)]
    public async Task CoordinationFailureRetainsOwnersUntilTheProducerCompletes(
        string stage,
        bool releaseFails,
        bool cancelFails
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var producer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new InvalidOperationException("coordination failed");
        var releaseFailure = new InvalidOperationException("independent blocker release failed");
        var cancelFailure = new InvalidOperationException("cancellation callback failed");
        var receivedTokens = new List<CancellationToken>();
        var released = false;
        using var registration = cancellation.Token.Register(() =>
        {
            cancellationObserved.SetResult();
            if (cancelFails)
            {
                throw cancelFailure;
            }
        });

        // Act
        var attempt = PostgreSqlOwnedConcurrentAttempt.RunAsync(
            cancellationToken =>
            {
                receivedTokens.Add(cancellationToken);

                return producer.Task;
            },
            (_, cancellationToken) =>
            {
                receivedTokens.Add(cancellationToken);

                return stage == "observe"
                    ? Task.FromException(primary)
                    : Task.CompletedTask;
            },
            cancellationToken =>
            {
                receivedTokens.Add(cancellationToken);

                return Task.FromException(primary);
            },
            () =>
            {
                released = true;

                return releaseFails
                    ? Task.FromException(releaseFailure)
                    : Task.CompletedTask;
            },
            cancellation,
            cancellation.Token);

        var pendingUntilProducerFinished = !attempt.IsCompleted;
        producer.SetResult();
        var failure = await Record.ExceptionAsync(() => attempt);

        // Assert
        Assert.True(pendingUntilProducerFinished);
        Assert.True(released);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(cancellationObserved.Task.IsCompletedSuccessfully);
        Assert.All(receivedTokens, actual => Assert.Equal(cancellation.Token, actual));
        if (!releaseFails
            && !cancelFails)
        {
            Assert.Same(primary, failure);
        }
        else
        {
            var aggregate = Assert.IsType<AggregateException>(failure);
            Assert.Same(primary, aggregate.InnerExceptions[0]);
            var causes = aggregate.Flatten()
                .InnerExceptions;

            Assert.Equal(1, causes.Count(cause => ReferenceEquals(cause, primary)));
            Assert.Equal(releaseFails, causes.Any(cause => ReferenceEquals(cause, releaseFailure)));
            Assert.Equal(cancelFails, causes.Any(cause => ReferenceEquals(cause, cancelFailure)));
        }
    }

    /// <summary>A later native fault is retained alongside the original observer failure.</summary>
    [Fact]
    public async Task ObserverFailureRetainsTheLaterNativeProducerFailure()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var producer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new InvalidOperationException("observer failed");
        var nativeFailure = new InvalidOperationException("native work failed after observation");
        using var registration = cancellation.Token.Register(() => cancellationObserved.SetResult());

        // Act
        var attempt = PostgreSqlOwnedConcurrentAttempt.RunAsync(
            _ => producer.Task,
            (_, _) => Task.FromException(primary),
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            cancellation,
            cancellation.Token);

        producer.SetException(nativeFailure);
        var failure = await Record.ExceptionAsync(() => attempt);

        // Assert
        var aggregate = Assert.IsType<AggregateException>(failure);
        Assert.Same(primary, aggregate.InnerExceptions[0]);
        Assert.Contains(aggregate.InnerExceptions, cause => ReferenceEquals(cause, nativeFailure));
        Assert.True(cancellationObserved.Task.IsCompletedSuccessfully);
    }

    /// <summary>Ordinary completion releases the blocker without canceling or rolling it back.</summary>
    [Fact]
    public async Task SuccessfulObservationAndCommitPreserveNormalCompletion()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var producer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? observedProducer = null;
        var rolledBack = false;

        // Act
        var failure = await PostgreSqlOwnedConcurrentAttempt.RunAsync(
            _ => producer.Task,
            (pending, _) =>
            {
                observedProducer = pending;

                return Task.CompletedTask;
            },
            _ =>
            {
                producer.SetResult();

                return Task.CompletedTask;
            },
            () =>
            {
                rolledBack = true;

                return Task.CompletedTask;
            },
            cancellation,
            cancellation.Token);

        // Assert
        Assert.Null(failure);
        Assert.Same(producer.Task, observedProducer);
        Assert.False(rolledBack);
        Assert.False(cancellation.IsCancellationRequested);
    }

    /// <summary>A native rejection after commit remains captured for the original SQL-state assertions.</summary>
    [Fact]
    public async Task CommittedNativeFailureIsReturnedWithoutChangingItsIdentity()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var producer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeFailure = new InvalidOperationException("expected native rejection");

        // Act
        var failure = await PostgreSqlOwnedConcurrentAttempt.RunAsync(
            _ => producer.Task,
            (_, _) => Task.CompletedTask,
            _ =>
            {
                producer.SetException(nativeFailure);

                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            cancellation,
            cancellation.Token);

        // Assert
        Assert.Same(nativeFailure, failure);
        Assert.False(cancellation.IsCancellationRequested);
    }

    /// <summary>Pre-cancellation still releases a blocker already owned by the calling test.</summary>
    [Fact]
    public async Task PreCanceledForwardWorkStillReleasesItsExistingBlocker()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var started = false;
        var released = false;

        // Act
        var failure = await Record.ExceptionAsync(() => PostgreSqlOwnedConcurrentAttempt.RunAsync(
            _ =>
            {
                started = true;

                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            () =>
            {
                released = true;

                return Task.CompletedTask;
            },
            cancellation,
            cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.False(started);
        Assert.True(released);
    }

    /// <summary>An observer cancellation cannot dispose the producer's owners before actual completion.</summary>
    [Fact]
    public async Task CanceledObserverStillDrainsThePendingProducer()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var producer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = false;

        // Act
        var attempt = PostgreSqlOwnedConcurrentAttempt.RunAsync(
            _ => producer.Task,
            (_, cancellationToken) =>
            {
                cancellation.Cancel();

                return Task.FromCanceled(cancellationToken);
            },
            _ => Task.CompletedTask,
            () =>
            {
                released = true;

                return Task.CompletedTask;
            },
            cancellation,
            cancellation.Token);

        var pendingBeforeProducerCompletion = !attempt.IsCompleted;
        producer.SetCanceled(cancellation.Token);
        var failure = await Record.ExceptionAsync(() => attempt);

        // Assert
        Assert.True(pendingBeforeProducerCompletion);
        Assert.True(released);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
    }

    /// <summary>A foreign or empty forward token cannot start work that the owned cleanup source cannot stop.</summary>
    /// <param name="emptyToken">Whether the foreign token is explicitly empty rather than another owned source.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignForwardTokenIsRejectedBeforeStartingWork(
        bool emptyToken
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var foreignCancellation = new CancellationTokenSource();
        var forwardCancellationToken = emptyToken
            ? CancellationToken.None
            : foreignCancellation.Token;

        var started = false;
        var released = false;

        // Act
        var failure = await Record.ExceptionAsync(() => PostgreSqlOwnedConcurrentAttempt.RunAsync(
            _ =>
            {
                started = true;

                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            () =>
            {
                released = true;

                return Task.CompletedTask;
            },
            cancellation,
            forwardCancellationToken));

        // Assert
        Assert.Equal("cancellationToken",
            Assert.IsType<ArgumentException>(failure)
                .ParamName);

        Assert.False(started);
        Assert.True(released);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.False(foreignCancellation.IsCancellationRequested);
    }
}
