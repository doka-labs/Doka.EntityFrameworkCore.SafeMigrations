namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Checks option precedence, real overlapping capacity and cancellation without external services.</summary>
public sealed class SharedContainerSchedulingTests
{
    /// <summary>Runner options override attributes, while zero retains the CPU-count default.</summary>
    /// <param name="optionMaximum">The optional runner maximum.</param>
    /// <param name="optionDisable">The optional runner parallelization override.</param>
    /// <param name="attributeMaximum">The assembly's configured maximum.</param>
    /// <param name="attributeDisable">The assembly's configured disable flag.</param>
    /// <param name="expectedMaximum">The resolved thread maximum.</param>
    /// <param name="expectedDisable">The resolved disable flag.</param>
    [Theory]
    [InlineData(null, null, 0, false, 8, false)]
    [InlineData(null, null, 4, false, 4, false)]
    [InlineData(2, null, 4, false, 2, false)]
    [InlineData(0, null, 4, false, 8, false)]
    [InlineData(-1, null, 4, false, -1, false)]
    [InlineData(null, null, -1, false, -1, false)]
    [InlineData(null, true, 4, false, 4, true)]
    [InlineData(null, false, 4, true, 4, false)]
    [InlineData(null, null, 4, true, 4, true)]
    public void Resolve_PreservesXunitOptionPrecedenceAndDefaults(
        int? optionMaximum,
        bool? optionDisable,
        int attributeMaximum,
        bool attributeDisable,
        int expectedMaximum,
        bool expectedDisable
    )
    {
        // Arrange
        const int processorCount = 8;

        // Act
        var policy = SharedContainerExecutionPolicy.Resolve(
            optionMaximum, optionDisable, attributeMaximum, attributeDisable, processorCount);

        // Assert
        Assert.Equal(expectedMaximum, policy.MaximumParallelThreads);
        Assert.Equal(expectedDisable, policy.DisableParallelization);
    }

    /// <summary>The explicit gate admits two overlapping collections and holds every extra collection.</summary>
    [Fact]
    public async Task RunAsync_WhenCapacityIsTwo_BoundsOverlappingCollections()
    {
        // Arrange
        using var scheduler = new SharedContainerCollectionScheduler(new SharedContainerExecutionPolicy(2, false));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTwoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var started = 0;
        var maximumActive = 0;
        var activeBeforeRelease = 0;
        var startedBeforeRelease = 0;

        async Task<int> RunCollectionAsync()
        {
            var nowActive = Interlocked.Increment(ref active);

            RecordMaximum(ref maximumActive, nowActive);

            if (Interlocked.Increment(ref started) == 2)
            {
                firstTwoStarted.TrySetResult();
            }

            try
            {
                await release.Task;

                return nowActive;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        // Act
        var collections = Enumerable.Range(0, 8)
            .Select(_ => scheduler.RunAsync(RunCollectionAsync, CancellationToken.None))
            .ToArray();

        try
        {
            await firstTwoStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            activeBeforeRelease = Volatile.Read(ref active);
            startedBeforeRelease = Volatile.Read(ref started);
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(collections).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(2, activeBeforeRelease);
        Assert.Equal(2, startedBeforeRelease);
        Assert.Equal(2, maximumActive);
        Assert.Equal(8, started);
        Assert.Equal(0, active);
    }

    /// <summary>A failing collection always returns capacity for the next runnable collection.</summary>
    [Fact]
    public async Task RunAsync_WhenCollectionFails_ReleasesCapacity()
    {
        // Arrange
        using var scheduler = new SharedContainerCollectionScheduler(new SharedContainerExecutionPolicy(1, false));
        var failure = new InvalidOperationException("collection sentinel");

        // Act
        var observedFailure = await Record.ExceptionAsync(() => scheduler.RunAsync(
            () => Task.FromException<int>(failure), CancellationToken.None));
        var nextResult = await scheduler.RunAsync(() => Task.FromResult(42), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Same(failure, observedFailure);
        Assert.Equal(42, nextResult);
    }

    /// <summary>Canceled waiters never execute or steal the occupied slot from a later collection.</summary>
    [Fact]
    public async Task RunAsync_WhenQueuedCollectionIsCanceled_DoesNotExecuteOrLeakCapacity()
    {
        // Arrange
        using var scheduler = new SharedContainerCollectionScheduler(new SharedContainerExecutionPolicy(1, false));
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledCollectionExecuted = false;
        var nextCollectionExecuted = false;

        // Act
        var first = scheduler.RunAsync(() => release.Task, CancellationToken.None);
        var canceled = scheduler.RunAsync(() =>
        {
            canceledCollectionExecuted = true;

            return Task.FromResult(2);
        }, cancellation.Token);
        var next = scheduler.RunAsync(() =>
        {
            nextCollectionExecuted = true;

            return Task.FromResult(3);
        }, CancellationToken.None);

        cancellation.Cancel();

        var cancellationFailure = await Record.ExceptionAsync(() => canceled);
        var nextExecutedBeforeRelease = nextCollectionExecuted;

        release.SetResult(1);

        await Task.WhenAll(first, next).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(cancellationFailure);
        Assert.False(canceledCollectionExecuted);
        Assert.False(nextExecutedBeforeRelease);
        Assert.True(nextCollectionExecuted);
        Assert.Equal(3, await next);
    }

    /// <summary>Unlimited execution still honors cancellation before starting a collection.</summary>
    [Fact]
    public async Task RunAsync_WithUnlimitedCanceledSelection_DoesNotExecuteCollection()
    {
        // Arrange
        using var scheduler = new SharedContainerCollectionScheduler(new SharedContainerExecutionPolicy(-1, false));
        using var cancellation = new CancellationTokenSource();
        var executed = false;

        cancellation.Cancel();

        // Act
        var failure = await Record.ExceptionAsync(() => scheduler.RunAsync(() =>
        {
            executed = true;

            return Task.FromResult(1);
        }, cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.False(executed);
    }

    /// <summary>The copied runner configuration uses recognized keys without a machine-independent CPU cap.</summary>
    [Fact]
    public void RunnerConfiguration_EnablesBothParallelLayersAndConservativeScheduling()
    {
        // Arrange
        var configurationPath = Path.Combine(AppContext.BaseDirectory, "xunit.runner.json");

        // Act
        using var configuration = JsonDocument.Parse(File.ReadAllText(configurationPath));

        // Assert
        Assert.True(configuration.RootElement.GetProperty("parallelizeAssembly").GetBoolean());
        Assert.True(configuration.RootElement.GetProperty("parallelizeTestCollections").GetBoolean());
        Assert.Equal("conservative", configuration.RootElement.GetProperty("parallelAlgorithm").GetString());
        Assert.False(configuration.RootElement.TryGetProperty("parallelizeAssemblies", out _));
        Assert.False(configuration.RootElement.TryGetProperty("maxParallelThreads", out _));
    }

    /// <summary>The shared opt-out collection explicitly prevents overlap with other collections.</summary>
    [Fact]
    public void ProcessWideCollection_DisablesParallelization()
    {
        // Arrange
        var collectionType = typeof(ProcessWideTestGroup);

        // Act
        var definition = collectionType.GetCustomAttribute<CollectionDefinitionAttribute>();

        // Assert
        Assert.NotNull(definition);
        Assert.Equal("Process-wide test contracts", ProcessWideTestGroup.Name);
        Assert.True(definition.DisableParallelization);
    }

    private static void RecordMaximum(
        ref int maximum,
        int value
    )
    {
        var observed = Volatile.Read(ref maximum);

        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);

            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
