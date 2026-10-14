namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Exercises assembly-owned fixture lifetime without starting a real container.</summary>
public sealed class SharedContainerFixtureLifecycleTests
{
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
        await lifecycle.InitializeAsync(required: false);
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
        var initializations = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var fixture = new ProbeFixture { Initialize = () => releaseStartup.Task };
        var constructions = 0;
        var entered = 0;
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() =>
        {
            Interlocked.Increment(ref constructions);

            return fixture;
        });

        // Act
        var callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await releaseCallers.Task;

                var initialization = lifecycle.InitializeAsync(required: true);

                initializations.Add(initialization);

                if (Interlocked.Increment(ref entered) == 8)
                {
                    allCallersEntered.SetResult();
                }

                await initialization;
            }))
            .ToArray();

        releaseCallers.SetResult();

        try
        {
            await allCallersEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseStartup.TrySetResult();
        }

        await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));
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
        var disposals = new System.Collections.Concurrent.ConcurrentBag<Task>();
        var fixture = new ProbeFixture { Cleanup = () => releaseCleanup.Task };
        var lifecycle = new SharedContainerFixtureLifecycle<ProbeFixture>(() => fixture);
        var entered = 0;

        await lifecycle.InitializeAsync(required: true);

        // Act
        var callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await releaseCallers.Task;

                var disposal = lifecycle.DisposeAsync();

                disposals.Add(disposal);

                if (Interlocked.Increment(ref entered) == 8)
                {
                    allCallersEntered.SetResult();
                }

                await disposal;
            }))
            .ToArray();

        releaseCallers.SetResult();

        try
        {
            await allCallersEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseCleanup.TrySetResult();
        }

        await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));

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
        var observedFailure = await Record.ExceptionAsync(() => lifecycle.InitializeAsync(required: true));

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
        var observedStartup = await Record.ExceptionAsync(() => lifecycle.InitializeAsync(required: true));
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
        var observedFailure = await Record.ExceptionAsync(() => lifecycle.InitializeAsync(required: true));

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
        var initialization = lifecycle.InitializeAsync(required: true);
        var disposal = lifecycle.DisposeAsync();
        var disposedBeforeStartupFinished = fixture.DisposeCount;
        var cleanupFinishedBeforeStartup = disposal.IsCompleted;

        startupCompleted.SetResult();

        await Task.WhenAll(initialization, disposal).WaitAsync(TimeSpan.FromSeconds(10));

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
        var initialization = lifecycle.InitializeAsync(required: true);
        var disposal = lifecycle.DisposeAsync();

        startupCompleted.SetException(startupFailure);

        var observedFailure = await Record.ExceptionAsync(() => initialization);

        await disposal.WaitAsync(TimeSpan.FromSeconds(10));

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
        var exception = await Record.ExceptionAsync(() => lifecycle.InitializeAsync(required: true));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Null(lifecycle.Fixture);
    }

    /// <summary>A fake asynchronous fixture with independently controlled startup and cleanup.</summary>
    public sealed class ProbeFixture : IAsyncLifetime
    {
        public Func<Task> Initialize { get; init; } = () => Task.CompletedTask;

        public Func<Task> Cleanup { get; init; } = () => Task.CompletedTask;

        public int InitializeCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task InitializeAsync()
        {
            InitializeCount++;

            return Initialize();
        }

        public Task DisposeAsync()
        {
            DisposeCount++;

            return Cleanup();
        }
    }
}
