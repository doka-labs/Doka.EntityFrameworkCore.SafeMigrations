namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Exercises the actual native attention coordinator with controlled, server-independent work.</summary>
public sealed class SqlServerAttentionTestOperationTests
{
    /// <summary>A failed readiness observer cannot end the scope while its command still runs.</summary>
    /// <param name="throwSynchronously">Whether readiness throws before returning its task.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadinessFailureRetainsPendingCommandUntilActualCompletion(
        bool throwSynchronously
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readinessFailure = new InvalidOperationException("readiness failed");

        // Act
        var observation = SqlServerAttentionTestOperation.ObserveAsync(
            execution.Task,
            _ => throwSynchronously
                ? throw readinessFailure
                : Task.FromException(readinessFailure),
            cancellation,
            CancellationToken.None);

        var retainedBeforeCompletion = !observation.IsCompleted;
        execution.SetResult();
        var result = await observation;

        // Assert
        Assert.True(retainedBeforeCompletion);
        Assert.Same(readinessFailure, result.ReadinessFailure);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Null(result.CancellationFailure);
        Assert.Null(result.ExecutionFailure);
    }

    /// <summary>Readiness cancellation is observed independently and still cancels/drains the command.</summary>
    [Fact]
    public async Task PreCancelledObservationRetainsTheCommandAndSkipsTheObserver()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var observerCancellation = new CancellationTokenSource();
        observerCancellation.Cancel();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerCalls = 0;

        // Act
        var observation = SqlServerAttentionTestOperation.ObserveAsync(
            execution.Task,
            _ =>
            {
                observerCalls++;

                return Task.CompletedTask;
            },
            cancellation,
            observerCancellation.Token);

        var retainedBeforeCompletion = !observation.IsCompleted;
        execution.SetResult();
        var result = await observation;

        // Assert
        Assert.True(retainedBeforeCompletion);
        Assert.Equal(0, observerCalls);
        Assert.Equal(observerCancellation.Token,
            Assert.IsType<OperationCanceledException>(result.ReadinessFailure)
                .CancellationToken);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Null(result.CancellationFailure);
        Assert.Null(result.ExecutionFailure);
    }

    /// <summary>A cancellation-callback error and native attention remain separate observations.</summary>
    [Fact]
    public async Task CancellationCallbackFailureDoesNotReplaceNativeFailure()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var callbackFailure = new InvalidOperationException("cancel callback failed");
        using var registration = cancellation.Token.Register(() => throw callbackFailure);
        var executionFailure = new InvalidOperationException("native command failed");
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var observation = SqlServerAttentionTestOperation.ObserveAsync(
            execution.Task,
            _ => Task.CompletedTask,
            cancellation,
            CancellationToken.None);

        execution.SetException(executionFailure);
        var result = await observation;

        // Assert
        Assert.Null(result.ReadinessFailure);
        Assert.Same(executionFailure, result.ExecutionFailure);
        Assert.Same(callbackFailure,
            Assert.Single(Assert.IsType<AggregateException>(result.CancellationFailure)
                .InnerExceptions));
    }

    /// <summary>The actual cancellation request must complete even when command completion came first.</summary>
    [Fact]
    public async Task PendingCancellationCallbackIsJoinedAfterTheCommandCompletes()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Token.Register(() =>
        {
            callbackEntered.SetResult();

            // WHY: Cancellation callbacks are synchronous framework hooks.
            // This explicit gate, not a sleep or deadline, controls their lifetime.
            releaseCallback.Wait();
        });

        Task<SqlServerAttentionObservation>? observation = null;
        Exception? primaryFailure = null;
        try
        {
            // Act
            observation = SqlServerAttentionTestOperation.ObserveAsync(
                Task.CompletedTask,
                _ => Task.CompletedTask,
                cancellation,
                CancellationToken.None);

            await callbackEntered.Task;
            var retainedWhileCancellationPending = !observation.IsCompleted;
            releaseCallback.Set();
            var result = await observation;

            // Assert
            Assert.True(retainedWhileCancellationPending);
            Assert.Null(result.ReadinessFailure);
            Assert.Null(result.CancellationFailure);
            Assert.Null(result.ExecutionFailure);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            releaseCallback.Set();
            await OwnedTestOperationCleanup.CompleteAsync(primaryFailure, observation, null, cancellation);
        }
    }

    /// <summary>Already-completed command errors retain their original object, not a waiting wrapper.</summary>
    [Fact]
    public async Task CompletedCommandFailurePreservesItsIdentity()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var observationCancellation = new CancellationTokenSource();
        var commandFailure = new InvalidOperationException("already failed command");
        var observedToken = CancellationToken.None;

        // Act
        var result = await SqlServerAttentionTestOperation.ObserveAsync(
            Task.FromException(commandFailure),
            cancellationToken =>
            {
                observedToken = cancellationToken;

                return Task.CompletedTask;
            },
            cancellation,
            observationCancellation.Token);

        // Assert
        Assert.Equal(observationCancellation.Token, observedToken);
        Assert.Same(commandFailure, result.ExecutionFailure);
        Assert.Null(result.ReadinessFailure);
        Assert.Null(result.CancellationFailure);
    }

    /// <summary>Invalid readiness wiring is rejected only after the already-started command has been drained.</summary>
    [Fact]
    public async Task InvalidObserverStillCancelsAndDrainsTheOwnedCommand()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Token.Register(() => execution.SetCanceled(cancellation.Token));

        // Act
        var failure = await Record.ExceptionAsync(() => SqlServerAttentionTestOperation.ObserveAsync(
            execution.Task,
            null!,
            cancellation,
            CancellationToken.None));

        // Assert
        Assert.Equal("waitUntilReady",
            Assert.IsType<ArgumentNullException>(failure)
                .ParamName);

        Assert.True(execution.Task.IsCanceled);
        Assert.True(cancellation.IsCancellationRequested);
    }

    /// <summary>Invalid observer wiring cannot hide an independent raw command fault during cleanup.</summary>
    /// <param name="faultAfterValidation">Whether the native fault arrives only after validation has failed.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidObserverRetainsIndependentNativeFailure(
        bool faultAfterValidation
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var delayedExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeFailure = new InvalidOperationException("independent native failure");
        if (!faultAfterValidation)
        {
            delayedExecution.SetException(nativeFailure);
        }

        var execution = delayedExecution.Task;

        // Act
        var observation = SqlServerAttentionTestOperation.ObserveAsync(
            execution,
            null!,
            cancellation,
            CancellationToken.None);

        var retainedBeforeNativeFault = !observation.IsCompleted;
        if (faultAfterValidation)
        {
            delayedExecution.SetException(nativeFailure);
        }

        var failure = await Record.ExceptionAsync(() => observation);

        // Assert
        Assert.Equal(faultAfterValidation, retainedBeforeNativeFault);
        var failures = Assert.IsType<AggregateException>(failure)
            .InnerExceptions;

        Assert.Equal(2, failures.Count);
        Assert.Equal("waitUntilReady",
            Assert.IsType<ArgumentNullException>(failures[0])
                .ParamName);

        Assert.Same(nativeFailure, failures[1]);
        Assert.True(execution.IsCompleted);
    }

    /// <summary>The actual native readiness consumer rejects an ended producer before any database query.</summary>
    /// <param name="completion">The command's terminal state before readiness is observed.</param>
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public async Task RealReadinessConsumerObservesEarlyProducerCompletion(
        string completion
    )
    {
        // Arrange
        await using var observer = new SqlConnection();
        using var cancellation = new CancellationTokenSource();
        using var producerCancellation = new CancellationTokenSource();
        producerCancellation.Cancel();
        var commandFailure = new InvalidOperationException("producer failed before readiness");
        var execution = completion switch
        {
            "success" => Task.CompletedTask,
            "failure" => Task.FromException(commandFailure),
            "cancellation" => Task.FromCanceled(producerCancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(completion)),
        };

        var pause = new SqlServerIdentityInsertPauseInterceptor("offline_early_completion");

        // Act
        var result = await SqlServerAttentionTestOperation.ObserveAsync(
            execution,
            cancellationToken => pause.WaitUntilReadyAsync(observer, execution, cancellationToken),
            cancellation,
            CancellationToken.None);

        // Assert
        Assert.Equal(ConnectionState.Closed, observer.State);
        Assert.Null(result.CancellationFailure);
        if (completion == "failure")
        {
            Assert.Same(commandFailure, result.ReadinessFailure);
            Assert.Same(commandFailure, result.ExecutionFailure);
        }
        else if (completion == "cancellation")
        {
            Assert.Equal(producerCancellation.Token,
                Assert.IsAssignableFrom<OperationCanceledException>(result.ReadinessFailure)
                    .CancellationToken);

            Assert.Equal(producerCancellation.Token,
                Assert.IsAssignableFrom<OperationCanceledException>(result.ExecutionFailure)
                    .CancellationToken);
        }
        else
        {
            Assert.Contains("completed before the readiness marker",
                Assert.IsType<InvalidOperationException>(result.ReadinessFailure)
                    .Message,
                StringComparison.Ordinal);

            Assert.Null(result.ExecutionFailure);
        }
    }
}
