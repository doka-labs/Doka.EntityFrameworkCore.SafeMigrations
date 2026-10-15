namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Shares one lazily activated container fixture across independent xUnit class collections.</summary>
/// <typeparam name="TFixture">The provider's assembly-owned asynchronous container fixture.</typeparam>
public abstract class SharedContainerTestFramework<TFixture> : XunitTestFramework
    where TFixture : class, IAsyncLifetime, new()
{
    /// <summary>Creates the provider framework with xUnit's diagnostic message sink.</summary>
    /// <param name="messageSink">The runner's diagnostic sink.</param>
    protected SharedContainerTestFramework(
        IMessageSink messageSink
    ) : base(messageSink) { }

    /// <inheritdoc />
    protected override ITestFrameworkExecutor CreateExecutor(
        System.Reflection.AssemblyName assemblyName
    ) => new SharedContainerTestFrameworkExecutor<TFixture>(
        assemblyName,
        SourceInformationProvider,
        DiagnosticMessageSink);
}

internal sealed class SharedContainerTestFrameworkExecutor<TFixture> : XunitTestFrameworkExecutor
    where TFixture : class, IAsyncLifetime, new()
{
    public SharedContainerTestFrameworkExecutor(
        System.Reflection.AssemblyName assemblyName,
        ISourceInformationProvider sourceInformationProvider,
        IMessageSink diagnosticMessageSink
    ) : base(assemblyName, sourceInformationProvider, diagnosticMessageSink) { }

    protected override void RunTestCases(
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions
    )
    {
        using var runner = new SharedContainerTestAssemblyRunner<TFixture>(
            TestAssembly,
            testCases.ToArray(),
            DiagnosticMessageSink,
            executionMessageSink,
            executionOptions);
        Exception? runnerFailure = null;

        try
        {
            // WHY: The v2 executor hook is synchronous. Its assembly runner
            // must finish startup, all parallel collections and final cleanup.
            runner.RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            runnerFailure = exception;
        }
        finally
        {
            // WHY: A failing message sink or runner can bypass the normal
            // finished hook. Both paths await the same single cleanup task.
            runner.EnsureFixtureDisposedAsync().GetAwaiter().GetResult();
        }

        if (runnerFailure is not null)
        {
            throw new AggregateException(
                "The test assembly runner failed; retained fixture failures follow.",
                new[] { runnerFailure }.Concat(runner.FixtureFailures));
        }
    }
}

internal sealed class SharedContainerTestAssemblyRunner<TFixture> : XunitTestAssemblyRunner
    where TFixture : class, IAsyncLifetime, new()
{
    private readonly object _collectionGate = new();
    private readonly List<Task<RunSummary>> _collectionRuns = [];
    private readonly SharedContainerFixtureLifecycle<TFixture> _lifecycle;
    private readonly bool _requiresFixture;
    private readonly SharedContainerCollectionScheduler _scheduler;
    private Task? _finish;

    public SharedContainerTestAssemblyRunner(
        ITestAssembly testAssembly,
        IReadOnlyCollection<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageSink executionMessageSink,
        ITestFrameworkExecutionOptions executionOptions,
        SharedContainerFixtureLifecycle<TFixture>? lifecycle = null
    ) : base(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
    {
        _lifecycle = lifecycle ?? new SharedContainerFixtureLifecycle<TFixture>();
        _requiresFixture = SharedContainerFixtureSelection.RequiresFixture<TFixture>(testCases);
        var collectionBehavior = testAssembly.Assembly
            .GetCustomAttributes(typeof(CollectionBehaviorAttribute).AssemblyQualifiedName!)
            .SingleOrDefault();
        var policy = SharedContainerExecutionPolicy.Resolve(
            executionOptions.MaxParallelThreads(),
            executionOptions.DisableParallelization(),
            collectionBehavior?.GetNamedArgument<int>("MaxParallelThreads") ?? 0,
            collectionBehavior?.GetNamedArgument<bool>("DisableTestParallelization") ?? false,
            Environment.ProcessorCount);
        _scheduler = new SharedContainerCollectionScheduler(policy);
    }

    public IReadOnlyList<Exception> FixtureFailures => _lifecycle.Failures;

    public Task EnsureFixtureDisposedAsync()
    {
        lock (_collectionGate)
        {
            return _finish ??= FinishAsync(_collectionRuns.ToArray());
        }
    }

    public override void Dispose()
    {
        _scheduler.Dispose();

        base.Dispose();
    }

    protected override Task AfterTestAssemblyStartingAsync() => Aggregator.RunAsync(async () =>
    {
        await base.AfterTestAssemblyStartingAsync().ConfigureAwait(false);
        await _lifecycle.InitializeAsync(_requiresFixture).ConfigureAwait(false);
    });

    protected override async Task BeforeTestAssemblyFinishedAsync()
    {
        await EnsureFixtureDisposedAsync().ConfigureAwait(false);
        await Aggregator.RunAsync(() => base.BeforeTestAssemblyFinishedAsync()).ConfigureAwait(false);
    }

    protected override Task<RunSummary> RunTestCollectionAsync(
        IMessageBus messageBus,
        ITestCollection testCollection,
        IEnumerable<IXunitTestCase> testCases,
        CancellationTokenSource cancellationTokenSource
    )
    {
        lock (_collectionGate)
        {
            // WHY: Upstream scheduling can still have queued continuations
            // after an unexpected runner failure. Late arrivals cannot borrow
            // a fixture whose final cleanup has already begun.
            if (_finish is not null)
            {
                return Task.FromResult(new RunSummary());
            }

            var run = _scheduler.RunAsync(
                () => new SharedContainerTestCollectionRunner<TFixture>(
                    testCollection,
                    testCases,
                    DiagnosticMessageSink,
                    messageBus,
                    TestCaseOrderer,
                    new ExceptionAggregator(Aggregator),
                    cancellationTokenSource,
                    _lifecycle.Fixture).RunAsync(),
                cancellationTokenSource.Token);
            _collectionRuns.Add(run);

            return run;
        }
    }

    private async Task FinishAsync(
        Task<RunSummary>[] collectionRuns
    )
    {
        // WHY: A failed collection or message sink can bypass upstream's
        // remaining awaits. Every admitted borrower must finish before its
        // shared container is released, including borrowers waiting for capacity.
        await Aggregator.RunAsync(async () => await Task.WhenAll(collectionRuns).ConfigureAwait(false))
            .ConfigureAwait(false);
        await Aggregator.RunAsync(_lifecycle.DisposeAsync).ConfigureAwait(false);
    }
}

internal sealed class SharedContainerTestCollectionRunner<TFixture> : XunitTestCollectionRunner
    where TFixture : class, IAsyncLifetime, new()
{
    private readonly TFixture? _sharedFixture;

    public SharedContainerTestCollectionRunner(
        ITestCollection testCollection,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        ITestCaseOrderer testCaseOrderer,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        TFixture? sharedFixture
    ) : base(testCollection, testCases, diagnosticMessageSink, messageBus, testCaseOrderer,
        aggregator, cancellationTokenSource) => _sharedFixture = sharedFixture;

    protected override Task<RunSummary> RunTestClassAsync(
        ITestClass testClass,
        IReflectionTypeInfo @class,
        IEnumerable<IXunitTestCase> testCases
    ) => new SharedContainerTestClassRunner<TFixture>(
        testClass,
        @class,
        testCases,
        DiagnosticMessageSink,
        MessageBus,
        TestCaseOrderer,
        new ExceptionAggregator(Aggregator),
        CancellationTokenSource,
        SharedContainerFixtureMappings.Merge(CollectionFixtureMappings, _sharedFixture)).RunAsync();
}

internal sealed class SharedContainerTestClassRunner<TFixture> : XunitTestClassRunner
    where TFixture : class, IAsyncLifetime, new()
{
    public SharedContainerTestClassRunner(
        ITestClass testClass,
        IReflectionTypeInfo @class,
        IEnumerable<IXunitTestCase> testCases,
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        ITestCaseOrderer testCaseOrderer,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource,
        IDictionary<Type, object> fixtureMappings
    ) : base(testClass, @class, testCases, diagnosticMessageSink, messageBus, testCaseOrderer,
        aggregator, cancellationTokenSource, fixtureMappings) { }

    protected override void CreateClassFixture(
        Type fixtureType
    )
    {
        // WHY: IClassFixture retains xUnit's analyzable constructor contract,
        // but this instance belongs to the assembly. Class-owned mappings
        // would initialize and dispose it again for every parallel class.
        if (fixtureType != typeof(TFixture))
        {
            base.CreateClassFixture(fixtureType);
        }
    }
}
