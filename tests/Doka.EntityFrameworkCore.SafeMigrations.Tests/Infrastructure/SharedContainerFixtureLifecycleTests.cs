namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Exercises assembly-owned fixture lifetime without starting a real container.</summary>
public sealed class SharedContainerFixtureLifecycleTests
{
    /// <summary>A canceled startup request must not commit fixture ownership or allocate resources.</summary>
    [Fact]
    public async Task InitializeAsync_PreCanceledRequestDoesNotConstructFixture()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var constructions = 0;
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() =>
        {
            constructions++;

            return new ProbeFixture();
        });

        // Act
        var failure = await Record.ExceptionAsync(() => lifecycle.InitializeAsync(true, cancellation.Token));
        await lifecycle.DisposeAsync();

        // Assert
        var canceled = Assert.IsType<OperationCanceledException>(failure, exactMatch: false);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(0, constructions);
        Assert.Null(lifecycle.Fixture);
        Assert.Empty(lifecycle.Failures);
    }

    /// <summary>Cancellation after startup commits cannot let cleanup race the framework-owned startup task.</summary>
    [Fact]
    public async Task InitializeAsync_CancellationAfterCommitRetainsStartupBeforeCleanup()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ProbeFixture { Initialize = () => releaseStartup.Task };
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() => fixture);

        // Act
        var initialization = lifecycle.InitializeAsync(true, cancellation.Token);

        // WHY: Keep cancellation and cleanup initiation synchronous while the startup release remains held.
        // ReSharper disable once MethodHasAsyncOverload
        cancellation.Cancel();
        var cleanup = lifecycle.DisposeAsync();
        var cleanupFinishedEarly = cleanup.IsCompleted;
        var disposedBeforeStartup = fixture.DisposeCount;
        releaseStartup.SetResult();
        await Task.WhenAll(initialization, cleanup);

        // Assert
        Assert.False(cleanupFinishedEarly);
        Assert.Equal(0, disposedBeforeStartup);
        Assert.Equal(1, fixture.InitializeCount);
        Assert.Equal(1, fixture.DisposeCount);
        Assert.Empty(lifecycle.Failures);
    }

    /// <summary>A unit-only selection must not even call the resource constructor.</summary>
    [Fact]
    public async Task InitializeAsync_WhenNotRequired_NeverConstructsFixture()
    {
        // Arrange
        var constructions = 0;
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() =>
        {
            constructions++;

            return new ProbeFixture();
        });

        // Act
        await lifecycle.InitializeAsync(required: false, cancellationToken: CancellationToken.None);
        await lifecycle.DisposeAsync();

        // Assert
        Assert.Equal(0, constructions);
        Assert.Null(lifecycle.Fixture);
        Assert.Empty(lifecycle.Failures);
    }

    /// <summary>Concurrent startup callers share one initialized fixture and the same task.</summary>
    [Fact]
    public async Task InitializeAsync_WhenCalledConcurrently_InitializesOnce()
    {
        // Arrange
        var releaseCallers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allCallersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initializations = new ConcurrentBag<Task>();
        var fixture = new ProbeFixture { Initialize = () => releaseStartup.Task };
        var constructions = 0;
        var entered = 0;
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() =>
        {
            Interlocked.Increment(ref constructions);

            return fixture;
        });

        // Act
        var callers = Enumerable
            .Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await releaseCallers.Task;

                Task initialization;

                try
                {
                    initialization = lifecycle.InitializeAsync(
                        required: true,
                        cancellationToken: CancellationToken.None);

                    initializations.Add(initialization);
                }
                finally
                {
                    // WHY: A synchronous subject failure must still release the readiness observer.
                    // The owning WhenAll below retains that failure after startup is unblocked.
                    if (Interlocked.Increment(ref entered) == 8)
                    {
                        allCallersEntered.TrySetResult();
                    }
                }

                await initialization;
            }))
            .ToArray();

        releaseCallers.SetResult();

        try
        {
            await allCallersEntered.Task;
        }
        finally
        {
            releaseStartup.TrySetResult();
        }

        await Task.WhenAll(callers);

        await lifecycle.DisposeAsync();

        // Assert
        Assert.Equal(8, initializations.Count);
        Assert.All(initializations, initialization => Assert.Same(initializations.First(), initialization));
        Assert.Same(fixture, lifecycle.Fixture);
        Assert.Equal(1, constructions);
        Assert.Equal(1, fixture.InitializeCount);
        Assert.Equal(1, fixture.DisposeCount);
    }

    /// <summary>The normal finish hook and fallback cleanup cannot dispose twice.</summary>
    [Fact]
    public async Task DisposeAsync_WhenCalledConcurrently_DisposesOnce()
    {
        // Arrange
        var releaseCallers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allCallersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposals = new ConcurrentBag<Task>();
        var fixture = new ProbeFixture { Cleanup = () => releaseCleanup.Task };
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() => fixture);
        var entered = 0;

        await lifecycle.InitializeAsync(required: true, cancellationToken: CancellationToken.None);

        // Act
        var callers = Enumerable
            .Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await releaseCallers.Task;

                Task disposal;

                try
                {
                    disposal = lifecycle.DisposeAsync();
                    disposals.Add(disposal);
                }
                finally
                {
                    if (Interlocked.Increment(ref entered) == 8)
                    {
                        allCallersEntered.TrySetResult();
                    }
                }

                await disposal;
            }))
            .ToArray();

        releaseCallers.SetResult();

        try
        {
            await allCallersEntered.Task;
        }
        finally
        {
            releaseCleanup.TrySetResult();
        }

        await Task.WhenAll(callers);

        // Assert
        Assert.Equal(8, disposals.Count);
        Assert.All(disposals, disposal => Assert.Same(disposals.First(), disposal));
        Assert.Equal(1, fixture.InitializeCount);
        Assert.Equal(1, fixture.DisposeCount);
        Assert.Empty(lifecycle.Failures);
    }

    /// <summary>Failed startup still leaves the partially allocated fixture available for final cleanup.</summary>
    [Fact]
    public async Task InitializeAsync_WhenStartupFails_RetainsOwnershipAndCleansUp()
    {
        // Arrange
        var startupFailure = new InvalidOperationException("startup sentinel");
        var fixture = new ProbeFixture { Initialize = () => Task.FromException(startupFailure) };
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() => fixture);

        // Act
        var observedFailure = await Record.ExceptionAsync(() => lifecycle.InitializeAsync(
            required: true,
            cancellationToken: CancellationToken.None));

        await lifecycle.DisposeAsync();

        // Assert
        Assert.Same(startupFailure, observedFailure);
        Assert.Same(fixture, lifecycle.Fixture);
        Assert.Equal(1, fixture.InitializeCount);
        Assert.Equal(1, fixture.DisposeCount);
        Assert.Same(startupFailure, Assert.Single(lifecycle.Failures));
    }

    /// <summary>Cleanup failure must not replace the original startup error.</summary>
    [Fact]
    public async Task DisposeAsync_WhenStartupAndCleanupFail_RetainsBothFailures()
    {
        // Arrange
        var startupFailure = new InvalidOperationException("startup sentinel");
        var cleanupFailure = new InvalidOperationException("cleanup sentinel");
        var fixture = new ProbeFixture
        {
            Initialize = () => Task.FromException(startupFailure),
            Cleanup = () => Task.FromException(cleanupFailure),
        };

        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() => fixture);

        // Act
        var observedStartup = await Record.ExceptionAsync(() => lifecycle.InitializeAsync(
            required: true,
            cancellationToken: CancellationToken.None));

        var observedCleanup = await Record.ExceptionAsync(lifecycle.DisposeAsync);
        var repeatedCleanup = await Record.ExceptionAsync(lifecycle.DisposeAsync);

        // Assert
        Assert.Same(startupFailure, observedStartup);
        Assert.Same(cleanupFailure, observedCleanup);
        Assert.Same(cleanupFailure, repeatedCleanup);
        Assert.Equal([startupFailure, cleanupFailure], lifecycle.Failures);
        Assert.Equal(1, fixture.DisposeCount);
    }

    /// <summary>A constructor failure is retained without inventing a fixture to dispose.</summary>
    [Fact]
    public async Task InitializeAsync_WhenConstructionFails_RecordsFailureWithoutCleanup()
    {
        // Arrange
        var constructionFailure = new InvalidOperationException("construction sentinel");
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() => throw constructionFailure);

        // Act
        var observedFailure = await Record.ExceptionAsync(() => lifecycle.InitializeAsync(
            required: true,
            cancellationToken: CancellationToken.None));

        await lifecycle.DisposeAsync();

        // Assert
        Assert.Same(constructionFailure, observedFailure);
        Assert.Null(lifecycle.Fixture);
        Assert.Same(constructionFailure, Assert.Single(lifecycle.Failures));
    }

    /// <summary>Final cleanup cannot race a startup that is still allocating its resource.</summary>
    [Fact]
    public async Task DisposeAsync_WhileStartupIsOngoing_WaitsBeforeDisposing()
    {
        // Arrange
        var startupCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new ProbeFixture { Initialize = () => startupCompleted.Task };
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() => fixture);

        // Act
        var initialization = lifecycle.InitializeAsync(required: true, cancellationToken: CancellationToken.None);
        var disposal = lifecycle.DisposeAsync();
        var disposedBeforeStartupFinished = fixture.DisposeCount;
        var cleanupFinishedBeforeStartup = disposal.IsCompleted;

        startupCompleted.SetResult();

        await Task.WhenAll(initialization, disposal);

        // Assert
        Assert.False(cleanupFinishedBeforeStartup);
        Assert.Equal(0, disposedBeforeStartupFinished);
        Assert.Equal(1, fixture.DisposeCount);
        Assert.Empty(lifecycle.Failures);
    }

    /// <summary>Failed ongoing startup is awaited and then cleaned without suppressing either error.</summary>
    [Fact]
    public async Task DisposeAsync_WhileStartupLaterFails_StillDisposesOwnedFixture()
    {
        // Arrange
        var startupCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupFailure = new InvalidOperationException("deferred startup sentinel");
        var fixture = new ProbeFixture { Initialize = () => startupCompleted.Task };
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() => fixture);

        // Act
        var initialization = lifecycle.InitializeAsync(required: true, cancellationToken: CancellationToken.None);
        var disposal = lifecycle.DisposeAsync();

        startupCompleted.SetException(startupFailure);

        var observedFailure = await Record.ExceptionAsync(() => initialization);

        await disposal;

        // Assert
        Assert.Same(startupFailure, observedFailure);
        Assert.Equal(1, fixture.DisposeCount);
        Assert.Same(startupFailure, Assert.Single(lifecycle.Failures));
    }

    /// <summary>A finalized lifecycle cannot allocate a resource after its cleanup task was cached.</summary>
    [Fact]
    public async Task InitializeAsync_AfterFinalCleanup_RejectsResourceConstruction()
    {
        // Arrange
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>();

        await lifecycle.DisposeAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => lifecycle.InitializeAsync(
            required: true,
            cancellationToken: CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Null(lifecycle.Fixture);
    }

    /// <summary>A fake asynchronous fixture with independently controlled startup and cleanup.</summary>
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    public sealed class ProbeFixture : IAsyncLifetime
    {
        /// <summary>Gets the framework startup callback, which completes immediately unless configured.</summary>
        public Func<Task> Initialize { get; init; } = () => Task.CompletedTask;

        /// <summary>Gets the framework cleanup callback, which completes immediately unless configured.</summary>
        public Func<Task> Cleanup { get; init; } = () => Task.CompletedTask;

        /// <summary>Gets the number of startup attempts, including callbacks that fail.</summary>
        public int InitializeCount { get; private set; }

        /// <summary>Gets the number of cleanup attempts, including callbacks that fail.</summary>
        public int DisposeCount { get; private set; }

        /// <inheritdoc />
        public Task InitializeAsync()
        {
            InitializeCount++;

            return Initialize();
        }

        /// <inheritdoc />
        public Task DisposeAsync()
        {
            DisposeCount++;

            return Cleanup();
        }
    }
}
