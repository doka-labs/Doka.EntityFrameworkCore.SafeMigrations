using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Exercises the actual xUnit runner chain with fake test cases and container-free fixtures.</summary>
public sealed class SharedContainerRunnerTests
{
    /// <summary>An owned bridge cannot accept a token different from the framework source it will invoke.</summary>
    /// <param name="matching">Whether the supplied token belongs to the actual framework cancellation source.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeCollectionRunnerRequiresTheActualFrameworkToken(
        bool matching
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var unrelated = new CancellationTokenSource();
        var observed = CancellationToken.None;
        var assembly = CreateAssembly();
        var sink = new RecordingSink();
        var testCase = CreateCase<UnitConsumer>(
            assembly,
            observeArguments: (_, token) =>
            {
                observed = token;

                return Task.FromResult(new RunSummary { Total = 1 });
            });

        using var runner = new SharedContainerTestAssemblyRunner<RunnerFixture>(
            assembly,
            [testCase],
            sink,
            sink,
            new RunnerOptions());

        using var messageBus = new SynchronousMessageBus(sink, stopOnFail: false);
        await InvokeStartingHookAsync(runner, CancellationToken.None);
        var token = matching
            ? cancellation.Token
            : unrelated.Token;

        // Act
        var failure = await Record.ExceptionAsync(() =>
            InvokeCollectionRunner(runner, testCase, messageBus, cancellation, token));

        await runner.EnsureFixtureDisposedAsync();

        // Assert
        if (matching)
        {
            Assert.Null(failure);
            Assert.Equal(cancellation.Token, observed);
        }
        else
        {
            var mismatch = Assert.IsType<ArgumentException>(failure);
            Assert.Equal("cancellationToken", mismatch.ParamName);
            Assert.Equal(CancellationToken.None, observed);
        }
    }

    /// <summary>Parallel classes borrow one initialized instance and retain ordinary class-fixture ownership.</summary>
    [Fact]
    public async Task RunAsync_InjectsSharedFixtureWithoutClassOwnership()
    {
        // Arrange
        var fixture = new RunnerFixture();
        var lifecycle = new SharedContainerFixtureLifecycle<RunnerFixture>(() => fixture);
        var assembly = CreateAssembly();
        var sink = new RecordingSink();
        var observedOrdinaryFixtures = new ConcurrentQueue<OrdinaryFixture>();

        Task<RunSummary> ObserveArgumentsAsync(
            object[] arguments,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            Assert.Same(fixture, arguments[0]);
            Assert.Equal(1, fixture.InitializeCount);
            Assert.Equal(0, fixture.DisposeCount);
            var ordinary = Assert.IsType<OrdinaryFixture>(arguments[1]);

            Assert.Equal(1, ordinary.InitializeCount);
            Assert.Equal(0, ordinary.DisposeCount);

            observedOrdinaryFixtures.Enqueue(ordinary);

            return Task.FromResult(new RunSummary { Total = 1 });
        }

        IXunitTestCase[] cases =
        [
            CreateCase<FirstConsumer>(assembly, observeArguments: ObserveArgumentsAsync),
            CreateCase<SecondConsumer>(assembly, observeArguments: ObserveArgumentsAsync),
        ];

        using var runner = new SharedContainerTestAssemblyRunner<RunnerFixture>(
            assembly,
            cases,
            sink,
            sink,
            new RunnerOptions(maximum: 2),
            lifecycle);

        // Act
        var summary = await runner.RunAsync();

        await runner.EnsureFixtureDisposedAsync();

        // Assert
        Assert.Equal(2, summary.Total);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(1, fixture.InitializeCount);
        Assert.Equal(1, fixture.DisposeCount);
        Assert.Equal(2, observedOrdinaryFixtures.Count);
        Assert.All(observedOrdinaryFixtures, ordinary => Assert.Equal(1, ordinary.DisposeCount));
        Assert.NotSame(observedOrdinaryFixtures.First(), observedOrdinaryFixtures.Last());
        Assert.Empty(runner.FixtureFailures);
        Assert.DoesNotContain(sink.Messages, message => message is ITestAssemblyCleanupFailure);
    }

    /// <summary>
    /// Real xUnit cases invoke both constructors and methods with one fully initialized borrowed fixture.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithRealCases_InvokesConstructorsWithSameInitializedFixture()
    {
        // Arrange
        var fixture = new RunnerFixture();
        var lifecycle = new SharedContainerFixtureLifecycle<RunnerFixture>(() => fixture);
        var assembly = CreateAssembly();
        var sink = new RecordingSink();
        IXunitTestCase[] cases =
        [
            CreateRealCase<FirstConsumer>(assembly, sink),
            CreateRealCase<SecondConsumer>(assembly, sink),
        ];

        using var runner = new SharedContainerTestAssemblyRunner<RunnerFixture>(
            assembly,
            cases,
            sink,
            sink,
            new RunnerOptions(),
            lifecycle);

        // Act
        var summary = await runner.RunAsync();

        // Assert
        Assert.Equal(2, summary.Total);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(2,
            sink
                .Messages
                .OfType<ITestPassed>()
                .Count());

        Assert.Equal(2, fixture.ConsumerFixtures.Count);
        Assert.All(fixture.ConsumerFixtures, ordinary => Assert.Equal(1, ordinary.DisposeCount));
        Assert.Equal(1, fixture.InitializeCount);
        Assert.Equal(1, fixture.DisposeCount);
    }

    /// <summary>Admitted ordinary collections remain owned even when their admission observer fails.</summary>
    /// <param name="observerFails">Whether observation fails before the admitted collections are released.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WithOrdinaryCollections_AdmitsBothBeforeEitherCompletes(
        bool observerFails
    )
    {
        // Arrange
        var fixture = new RunnerFixture();
        var lifecycle = new SharedContainerFixtureLifecycle<RunnerFixture>(() => fixture);
        var assembly = CreateAssembly();
        var sink = new RecordingSink();
        var bothAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerFailure = new InvalidOperationException("admission observer failed");
        var active = 0;
        var admitted = 0;

        async Task<RunSummary> HoldCollectionAsync(
            object[] arguments,
            CancellationToken cancellationToken
        )
        {
            try
            {
                Assert.Same(fixture, arguments[0]);

                Interlocked.Increment(ref active);

                try
                {
                    if (Interlocked.Increment(ref admitted) == 2)
                    {
                        if (observerFails)
                        {
                            bothAdmitted.TrySetException(observerFailure);
                        }
                        else
                        {
                            bothAdmitted.TrySetResult();
                        }
                    }

                    await release.Task.WaitAsync(cancellationToken);

                    return new RunSummary { Total = 1 };
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
            catch (Exception exception)
            {
                bothAdmitted.TrySetException(exception);

                throw;
            }
        }

        IXunitTestCase[] cases =
        [
            CreateCase<FirstConsumer>(assembly, observeArguments: HoldCollectionAsync),
            CreateCase<SecondConsumer>(assembly, observeArguments: HoldCollectionAsync),
        ];

        using var runner = new SharedContainerTestAssemblyRunner<RunnerFixture>(
            assembly,
            cases,
            sink,
            sink,
            new RunnerOptions(maximum: 2),
            lifecycle);

        var activeBeforeRelease = 0;
        var completedBeforeRelease = false;

        // Act
        var assemblyRun = runner.RunAsync();

        var observedFailure = await Record.ExceptionAsync(async () =>
        {
            Exception? primaryFailure = null;
            try
            {
                await AwaitAdmissionAsync(bothAdmitted.Task, assemblyRun, CancellationToken.None);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;

                throw;
            }
            finally
            {
                activeBeforeRelease = Volatile.Read(ref active);
                completedBeforeRelease = assemblyRun.IsCompleted;

                // WHY: A failed observer must release and join every borrower
                // before the runner or fixture can leave its owner scope.
                await OwnedTestOperationCleanup.CompleteAsync(
                    primaryFailure,
                    assemblyRun,
                    () =>
                    {
                        release.TrySetResult();

                        return Task.CompletedTask;
                    },
                    null);
            }
        });

        var summary = await assemblyRun;

        // Assert
        Assert.Equal(2, activeBeforeRelease);
        Assert.Same(observerFails
                ? observerFailure
                : null,
            observedFailure);

        Assert.False(completedBeforeRelease);
        Assert.Equal(2, summary.Total);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(1, fixture.InitializeCount);
        Assert.Equal(1, fixture.DisposeCount);
    }

    /// <summary>The exclusive definition prevents overlap; the same cases without it demonstrate overlap.</summary>
    /// <param name="exclusive">Whether the control case carries the real nonparallel collection definition.</param>
    /// <param name="observerFails">Whether observation fails while the normal collections remain gated.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task RunAsync_RespectsDefinitionBackedExclusiveCollectionWithOverlappingControl(
        bool exclusive,
        bool observerFails
    )
    {
        // Arrange
        var fixture = new RunnerFixture();
        var lifecycle = new SharedContainerFixtureLifecycle<RunnerFixture>(() => fixture);
        var assembly = CreateAssembly();
        var sink = new RecordingSink();
        var normalsAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controlAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNormals = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseControl = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerFailure = new InvalidOperationException("collection overlap observer failed");
        var normalActive = 0;
        var normalAdmitted = 0;
        var normalCompleted = 0;
        var normalActiveAtControlAdmission = -1;
        var normalCompletedAtControlAdmission = -1;
        var controlActiveBeforeNormalRelease = false;

        async Task<RunSummary> HoldNormalAsync(
            object[] arguments,
            CancellationToken cancellationToken
        )
        {
            try
            {
                Assert.Same(fixture, arguments[0]);

                Interlocked.Increment(ref normalActive);

                try
                {
                    if (Interlocked.Increment(ref normalAdmitted) == 2)
                    {
                        normalsAdmitted.TrySetResult();
                    }

                    await releaseNormals.Task.WaitAsync(cancellationToken);

                    return new RunSummary { Total = 1 };
                }
                finally
                {
                    Interlocked.Decrement(ref normalActive);
                    Interlocked.Increment(ref normalCompleted);
                }
            }
            catch (Exception exception)
            {
                normalsAdmitted.TrySetException(exception);
                controlAdmitted.TrySetException(exception);

                throw;
            }
        }

        async Task<RunSummary> HoldControlAsync(
            object[] arguments,
            CancellationToken cancellationToken
        )
        {
            try
            {
                Assert.Empty(arguments);

                await normalsAdmitted.Task.WaitAsync(cancellationToken);

                normalActiveAtControlAdmission = Volatile.Read(ref normalActive);
                normalCompletedAtControlAdmission = Volatile.Read(ref normalCompleted);
                controlAdmitted.TrySetResult();

                await releaseControl.Task.WaitAsync(cancellationToken);

                return new RunSummary { Total = 1 };
            }
            catch (Exception exception)
            {
                controlAdmitted.TrySetException(exception);

                throw;
            }
        }

        IXunitTestCase[] cases =
        [
            CreateCase<FirstConsumer>(assembly, observeArguments: HoldNormalAsync),
            CreateCase<SecondConsumer>(assembly, observeArguments: HoldNormalAsync),
            CreateCase<UnitConsumer>(
                assembly,
                observeArguments: HoldControlAsync,
                collectionDefinition: exclusive
                    ? Reflector.Wrap(typeof(ProcessWideTestGroup))
                    : null),
        ];

        using var runner = new SharedContainerTestAssemblyRunner<RunnerFixture>(
            assembly,
            cases,
            sink,
            sink,
            new RunnerOptions(maximum: 4),
            lifecycle);

        // Act
        var assemblyRun = runner.RunAsync();

        var observedFailure = await Record.ExceptionAsync(async () =>
        {
            Exception? primaryFailure = null;
            try
            {
                await AwaitAdmissionAsync(normalsAdmitted.Task, assemblyRun, CancellationToken.None);

                if (!exclusive)
                {
                    // WHY: The control must enter while both ordinary collections
                    // remain gated; serialization cannot satisfy this observation.
                    await AwaitAdmissionAsync(controlAdmitted.Task, assemblyRun, CancellationToken.None);
                }

                controlActiveBeforeNormalRelease = controlAdmitted.Task.IsCompleted;

                if (observerFails)
                {
                    throw observerFailure;
                }

                releaseNormals.TrySetResult();

                await AwaitAdmissionAsync(controlAdmitted.Task, assemblyRun, CancellationToken.None);
            }
            catch (Exception exception)
            {
                primaryFailure = exception;

                throw;
            }
            finally
            {
                // WHY: Both independent gates must open before the actual
                // assembly run is joined, including a failed observer path.
                await OwnedTestOperationCleanup.CompleteAsync(
                    primaryFailure,
                    assemblyRun,
                    () =>
                    {
                        releaseNormals.TrySetResult();
                        releaseControl.TrySetResult();

                        return Task.CompletedTask;
                    },
                    null);
            }
        });

        var summary = await assemblyRun;

        // Assert
        Assert.Same(observerFails
                ? observerFailure
                : null,
            observedFailure);

        Assert.Equal(!exclusive, controlActiveBeforeNormalRelease);
        Assert.Equal(
            exclusive
                ? 0
                : 2,
            normalActiveAtControlAdmission);

        Assert.Equal(
            exclusive
                ? 2
                : 0,
            normalCompletedAtControlAdmission);

        Assert.Equal(3, summary.Total);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(1, fixture.InitializeCount);
        Assert.Equal(1, fixture.DisposeCount);
    }

    /// <summary>Unit-only selected cases use the real runner without constructing the shared resource.</summary>
    [Fact]
    public async Task RunAsync_WhenOnlyUnitCasesAreSelected_DoesNotConstructFixture()
    {
        // Arrange
        var constructions = 0;
        var lifecycle = new SharedContainerFixtureLifecycle<RunnerFixture>(() =>
        {
            constructions++;

            return new RunnerFixture();
        });

        var assembly = CreateAssembly();
        var sink = new RecordingSink();
        IXunitTestCase[] cases =
        [
            CreateRealCase<UnitConsumer>(assembly, sink),
        ];

        using var runner = new SharedContainerTestAssemblyRunner<RunnerFixture>(
            assembly,
            cases,
            sink,
            sink,
            new RunnerOptions(),
            lifecycle);

        // Act
        var summary = await runner.RunAsync();

        // Assert
        Assert.Equal(1, summary.Total);
        Assert.Equal(0, summary.Failed);
        Assert.Single(sink.Messages.OfType<ITestPassed>());
        Assert.Equal(0, constructions);
        Assert.Null(lifecycle.Fixture);
    }

    /// <summary>Skipped fixture-dependent cases remain skipped even when no constructor mapping is present.</summary>
    [Fact]
    public async Task RunAsync_WhenEveryFixtureCaseIsSkipped_DoesNotConstructFixture()
    {
        // Arrange
        var constructions = 0;
        var lifecycle = new SharedContainerFixtureLifecycle<RunnerFixture>(() =>
        {
            constructions++;

            return new RunnerFixture();
        });

        var assembly = CreateAssembly();
        var sink = new RecordingSink();
        IXunitTestCase[] cases =
        [
            CreateRealCase<FirstConsumer>(assembly, sink, skipReason: "selection sentinel"),
            CreateRealCase<SecondConsumer>(assembly, sink, skipReason: "selection sentinel"),
        ];

        using var runner = new SharedContainerTestAssemblyRunner<RunnerFixture>(
            assembly,
            cases,
            sink,
            sink,
            new RunnerOptions(),
            lifecycle);

        // Act
        var summary = await runner.RunAsync();

        // Assert
        Assert.Equal(2, summary.Total);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, constructions);
        Assert.Null(lifecycle.Fixture);
        Assert.Empty(runner.FixtureFailures);
        Assert.Equal(2,
            sink
                .Messages
                .OfType<ITestSkipped>()
                .Count());
    }

    /// <summary>
    /// Startup errors fail selected tests and cleanup errors produce the normal xUnit cleanup message.
    /// </summary>
    [Fact]
    public async Task RunAsync_WhenStartupAndCleanupFail_ReportsBothFailuresWithoutThrowingHooks()
    {
        // Arrange
        var startupFailure = new InvalidOperationException("runner startup sentinel");
        var cleanupFailure = new InvalidOperationException("runner cleanup sentinel");
        var fixture = new RunnerFixture
        {
            Initialize = () => Task.FromException(startupFailure),
            Cleanup = () => Task.FromException(cleanupFailure),
        };

        var lifecycle = new SharedContainerFixtureLifecycle<RunnerFixture>(() => fixture);
        var assembly = CreateAssembly();
        var sink = new RecordingSink();
        var testCase = CreateCase<FirstConsumer>(assembly);
        using var runner = new SharedContainerTestAssemblyRunner<RunnerFixture>(
            assembly,
            [testCase],
            sink,
            sink,
            new RunnerOptions(),
            lifecycle);

        // Act
        var summary = await runner.RunAsync();

        await runner.EnsureFixtureDisposedAsync();

        // Assert
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Failed);
        Assert.Same(startupFailure, testCase.ObservedFailure);
        Assert.Equal(1, fixture.DisposeCount);
        Assert.Equal([startupFailure, cleanupFailure], runner.FixtureFailures);
        var cleanupMessage = Assert.Single(sink.Messages.OfType<ITestAssemblyCleanupFailure>());

        Assert.Contains("runner cleanup sentinel", cleanupMessage.Messages);
    }

    /// <summary>
    /// The fallback waits for an admitted collection and rejects late borrowers before final teardown.
    /// </summary>
    /// <param name="observerFails">Whether observation fails before the admitted collection is released.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnsureFixtureDisposedAsync_DrainsAdmittedCollectionsAndRejectsLateArrivals(
        bool observerFails
    )
    {
        // Arrange
        var fixture = new RunnerFixture();
        var lifecycle = new SharedContainerFixtureLifecycle<RunnerFixture>(() => fixture);
        var assembly = CreateAssembly();
        var sink = new RecordingSink();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerFailure = new InvalidOperationException("fixture teardown observer failed");
        var executed = 0;
        var firstCase = CreateCase<FirstConsumer>(
            assembly,
            observeArguments: async (arguments, cancellationToken) =>
            {
                Assert.Same(fixture, arguments[0]);

                Interlocked.Increment(ref executed);
                admitted.SetResult();

                await release.Task.WaitAsync(cancellationToken);

                return new RunSummary { Total = 1 };
            });

        var lateCase = CreateCase<SecondConsumer>(
            assembly,
            observeArguments: (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                Interlocked.Increment(ref executed);

                return Task.FromResult(new RunSummary { Total = 1 });
            });

        using var runner = new SharedContainerTestAssemblyRunner<RunnerFixture>(
            assembly,
            [firstCase, lateCase],
            sink,
            sink,
            new RunnerOptions(),
            lifecycle);

        using var cancellation = new CancellationTokenSource();
        using var messageBus = new SynchronousMessageBus(sink, stopOnFail: false);

        await InvokeStartingHookAsync(runner, cancellation.Token);
        var disposedWhileCollectionWasActive = -1;
        var cleanupCompletedBeforeRelease = false;
        var cleanup = Task.CompletedTask;
        Task<RunSummary>? lateRun = null;

        // Act
        var firstRun = InvokeCollectionRunner(runner, firstCase, messageBus, cancellation, cancellation.Token);

        var observedFailure = await Record.ExceptionAsync(async () =>
        {
            Exception? primaryFailure = null;
            try
            {
                await AwaitAdmissionAsync(admitted.Task, firstRun, cancellation.Token);

                cleanup = runner.EnsureFixtureDisposedAsync();
                disposedWhileCollectionWasActive = fixture.DisposeCount;
                cleanupCompletedBeforeRelease = cleanup.IsCompleted;
                lateRun = InvokeCollectionRunner(runner, lateCase, messageBus, cancellation, cancellation.Token);

                if (observerFails)
                {
                    throw observerFailure;
                }
            }
            catch (Exception exception)
            {
                primaryFailure = exception;

                throw;
            }
            finally
            {
                // WHY: Observation can fail before teardown starts. Release and
                // join every known run, then complete the cached fixture teardown
                // before the enclosing runner, bus and token source are disposed.
                await OwnedTestOperationCleanup.CompleteAsync(
                    primaryFailure,
                    Task.WhenAll(firstRun, (Task?)lateRun ?? Task.CompletedTask, cleanup),
                    () =>
                    {
                        release.TrySetResult();

                        return Task.CompletedTask;
                    },
                    null,
                    runner.EnsureFixtureDisposedAsync);
            }
        });

        var lateSummary = lateRun is null
            ? null
            : await lateRun;

        // Assert
        Assert.Same(observerFails
                ? observerFailure
                : null,
            observedFailure);

        Assert.False(cleanupCompletedBeforeRelease);
        Assert.Equal(0, disposedWhileCollectionWasActive);
        Assert.Equal(1, executed);
        Assert.Equal(1, fixture.InitializeCount);
        Assert.Equal(1, fixture.DisposeCount);
        Assert.NotNull(lateSummary);
        Assert.Equal(0, lateSummary.Total);
    }

    /// <summary>Readiness cannot be inferred from a runner completing without its admission signal.</summary>
    /// <param name="runnerFails">Whether the runner faults instead of returning without admission.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmissionObservationRejectsRunnerCompletionWithoutAdmission(
        bool runnerFails
    )
    {
        // Arrange
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runnerFailure = new InvalidOperationException("runner failed before admission");
        var run = runnerFails
            ? Task.FromException(runnerFailure)
            : Task.CompletedTask;

        // Act
        var failure = await Record.ExceptionAsync(() =>
            AwaitAdmissionAsync(admission.Task, run, CancellationToken.None));

        // Assert
        if (runnerFails)
        {
            Assert.Same(runnerFailure, failure);
        }
        else
        {
            Assert.Equal(
                "The runner completed before the required collection admission was observed.",
                Assert.IsType<InvalidOperationException>(failure)
                    .Message);
        }

        Assert.False(admission.Task.IsCompleted);
    }

    /// <summary>A completed admission remains valid when its runner has already completed too.</summary>
    [Fact]
    public async Task AdmissionObservationAcceptsAnAlreadyCompletedAdmission()
    {
        // Arrange
        var admission = Task.CompletedTask;
        var run = Task.CompletedTask;

        // Act
        await AwaitAdmissionAsync(admission, run, CancellationToken.None);

        // Assert
        Assert.True(admission.IsCompletedSuccessfully);
        Assert.True(run.IsCompletedSuccessfully);
    }

    /// <summary>Readiness observation preserves its caller's cancellation without taking ownership of work.</summary>
    [Fact]
    public async Task AdmissionObservationRetainsTheCallerCancellationToken()
    {
        // Arrange
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            AwaitAdmissionAsync(admission.Task, run.Task, cancellation.Token));

        run.SetResult();
        admission.SetResult();
        await run.Task;

        // Assert
        Assert.Equal(cancellation.Token,
            Assert.IsType<OperationCanceledException>(failure)
                .CancellationToken);

        Assert.True(run.Task.IsCompletedSuccessfully);
    }

    /// <summary>
    /// Observes actual collection admission or an earlier runner completion without a speed threshold.
    /// </summary>
    /// <param name="admission">The signal set only after the expected collections enter.</param>
    /// <param name="run">The real runner operation, which remains owned by its caller.</param>
    /// <param name="cancellationToken">Cancels forward observation; the caller still protects cleanup.</param>
    /// <returns>Completion only after the actual admission signal was observed.</returns>
    private static async Task AwaitAdmissionAsync(
        Task admission,
        Task run,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        _ = await Task
            .WhenAny(admission, run)
            .WaitAsync(cancellationToken);

        if (!admission.IsCompleted)
        {
            // WHY: Startup or a collection may fail before signaling readiness.
            // Observe the actual runner error instead of leaving a gate wait orphaned.
            await run;

            throw new InvalidOperationException(
                "The runner completed before the required collection admission was observed.");
        }

        await admission.WaitAsync(cancellationToken);
    }

    private static TestAssembly CreateAssembly() => new(Reflector.Wrap(typeof(SharedContainerRunnerTests).Assembly));

    private static ProbeCase CreateCase<TConsumer>(
        ITestAssembly assembly,
        string? skipReason = null,
        Func<object[], CancellationToken, Task<RunSummary>>? observeArguments = null,
        ITypeInfo? collectionDefinition = null
    ) => new(CreateTestMethod<TConsumer>(assembly, collectionDefinition), skipReason, observeArguments);

    private static SelectedProbeCase CreateRealCase<TConsumer>(
        ITestAssembly assembly,
        IMessageSink diagnosticMessageSink,
        string? skipReason = null
    ) => new(diagnosticMessageSink, CreateTestMethod<TConsumer>(assembly), skipReason);

    private static TestMethod CreateTestMethod<TConsumer>(
        ITestAssembly assembly,
        ITypeInfo? collectionDefinition = null
    )
    {
        var classInfo = Reflector.Wrap(typeof(TConsumer));
        var collection = new TestCollection(assembly, collectionDefinition, typeof(TConsumer).FullName);
        var testClass = new TestClass(collection, classInfo);
        var method = typeof(TConsumer).GetMethod("TestBody")
            ?? throw new InvalidOperationException("The probe class has no public test-body method.");

        return new TestMethod(testClass, Reflector.Wrap(method));
    }

    /// <summary>Invokes the real startup hook without changing its framework-owned parameterless signature.</summary>
    /// <param name="runner">The production runner being exercised.</param>
    /// <param name="cancellationToken">Cancels before startup is invoked and resource ownership is committed.</param>
    /// <returns>The real framework startup task, which must not be abandoned during cleanup.</returns>
    private static Task InvokeStartingHookAsync(
        SharedContainerTestAssemblyRunner<RunnerFixture> runner,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hook = runner
            .GetType()
            .GetMethod(
                "AfterTestAssemblyStartingAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(hook);

        return Assert.IsAssignableFrom<Task>(hook.Invoke(runner, null));
    }

    /// <summary>Bridges the operation token to the actual cancellation source owned by xUnit.</summary>
    /// <param name="runner">The production collection runner.</param>
    /// <param name="testCase">The exact synthetic test case.</param>
    /// <param name="messageBus">The framework execution bus.</param>
    /// <param name="cancellation">The source passed to the framework's collection hook.</param>
    /// <param name="cancellationToken">The same source's token; a mismatch is rejected before dispatch.</param>
    /// <returns>The real collection execution task.</returns>
    private static Task<RunSummary> InvokeCollectionRunner(
        SharedContainerTestAssemblyRunner<RunnerFixture> runner,
        IXunitTestCase testCase,
        IMessageBus messageBus,
        CancellationTokenSource cancellation,
        CancellationToken cancellationToken
    )
    {
        if (cancellationToken != cancellation.Token)
        {
            throw new ArgumentException(
                "The token must belong to the runner's cancellation source.",
                nameof(cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var hook = runner
            .GetType()
            .GetMethod(
                "RunTestCollectionAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(hook);

        return Assert.IsAssignableFrom<Task<RunSummary>>(
            hook.Invoke(
                runner,
                [messageBus, testCase.TestMethod.TestClass.TestCollection, new[] { testCase }, cancellation]));
    }

    private sealed class SelectedProbeCase : XunitTestCase
    {
        private readonly string? _selectedSkipReason;

        public SelectedProbeCase()
            : this(new RecordingSink(), CreateTestMethod<UnitConsumer>(CreateAssembly()), null) { }

        public SelectedProbeCase(
            IMessageSink diagnosticMessageSink,
            ITestMethod testMethod,
            string? skipReason
        ) : base(diagnosticMessageSink, TestMethodDisplay.ClassAndMethod, TestMethodDisplayOptions.None, testMethod)
        {
            _selectedSkipReason = skipReason;
        }

        protected override string? GetSkipReason(
            IAttributeInfo factAttribute
        ) => _selectedSkipReason;
    }

    private sealed class ProbeCase : LongLivedMarshalByRefObject, IXunitTestCase
    {
        private readonly Func<object[], CancellationToken, Task<RunSummary>>? _observeArguments;

        public ProbeCase()
            : this(CreateTestMethod<UnitConsumer>(CreateAssembly()), null, null) { }

        public ProbeCase(
            ITestMethod testMethod,
            string? skipReason,
            Func<object[], CancellationToken, Task<RunSummary>>? observeArguments
        )
        {
            TestMethod = testMethod;
            SkipReason = skipReason;
            _observeArguments = observeArguments;
        }

        public Exception? ObservedFailure { get; private set; }

        public string DisplayName => TestMethod.TestClass.Class.Name;

        public string? SkipReason { get; }

        public ISourceInformation? SourceInformation { get; set; }

        public ITestMethod TestMethod { get; }

        public object[] TestMethodArguments { get; } = [];

        public Dictionary<string, List<string>> Traits { get; } = [];

        public string UniqueID { get; } = Guid
            .NewGuid()
            .ToString("N");

        public Exception? InitializationException => null;

        public IMethodInfo Method => TestMethod.Method;

        public int Timeout => 0;

        public Task<RunSummary> RunAsync(
            IMessageSink diagnosticMessageSink,
            IMessageBus messageBus,
            object[] constructorArguments,
            ExceptionAggregator aggregator,
            CancellationTokenSource cancellationTokenSource
        )
        {
            if (!string.IsNullOrEmpty(SkipReason))
            {
                return Task.FromResult(
                    new RunSummary
                    {
                        Total = 1,
                        Skipped = 1,
                    });
            }

            ObservedFailure = aggregator.ToException();

            if (ObservedFailure is not null)
            {
                return Task.FromResult(
                    new RunSummary
                    {
                        Total = 1,
                        Failed = 1,
                    });
            }

            return _observeArguments?.Invoke(constructorArguments, cancellationTokenSource.Token)
                ?? Task.FromResult(new RunSummary { Total = 1 });
        }

        public void Serialize(
            IXunitSerializationInfo info
        ) => throw new NotSupportedException("In-process probe cases are not serialized.");

        public void Deserialize(
            IXunitSerializationInfo info
        ) => throw new NotSupportedException("In-process probe cases are not deserialized.");
    }

    private sealed class RunnerOptions : ITestFrameworkExecutionOptions
    {
        private readonly Dictionary<string, object?> _values = [];

        public RunnerOptions(
            int maximum = 2
        )
        {
            SetValue("xunit.execution.MaxParallelThreads", maximum);
        }

        [return: MaybeNull]
        public TValue GetValue<TValue>(
            string name
        ) => _values.TryGetValue(name, out var value) && value is TValue typedValue
            ? typedValue
            : default;

        public void SetValue<TValue>(
            string name,
            TValue value
        ) => _values[name] = value;
    }

    private sealed class RecordingSink : LongLivedMarshalByRefObject, IMessageSink
    {
        public ConcurrentQueue<IMessageSinkMessage> Messages { get; } = new();

        public bool OnMessage(
            IMessageSinkMessage message
        )
        {
            Messages.Enqueue(message);

            return true;
        }
    }

    /// <summary>An assembly-owned fake fixture retaining independent startup and cleanup counts.</summary>
    public sealed class RunnerFixture : IAsyncLifetime
    {
        public Func<Task> Initialize { get; init; } = () => Task.CompletedTask;

        public Func<Task> Cleanup { get; init; } = () => Task.CompletedTask;

        public int InitializeCount { get; private set; }

        public int DisposeCount { get; private set; }

        /// <summary>Records real consumer constructor invocations without process-wide mutable counters.</summary>
        public ConcurrentQueue<OrdinaryFixture> ConsumerFixtures { get; } = new();

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

    /// <summary>An ordinary class-owned fixture that the shared framework must leave under xUnit ownership.</summary>
    public sealed class OrdinaryFixture : IAsyncLifetime
    {
        public int InitializeCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task InitializeAsync()
        {
            InitializeCount++;

            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            DisposeCount++;

            return Task.CompletedTask;
        }
    }

    /// <summary>Provides real Fact metadata solely for manually selected nested runner cases.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    [XunitTestCaseDiscoverer(
        "Doka.EntityFrameworkCore.SafeMigrations.Tests.SharedContainerRunnerTests+ProbeCaseDiscoverer",
        "Doka.EntityFrameworkCore.SafeMigrations.Tests")]
    public sealed class ProbeFactAttribute : FactAttribute { }

    /// <summary>Prevents probe metadata from producing test cases in the host assembly's discovery pass.</summary>
    public sealed class ProbeCaseDiscoverer : IXunitTestCaseDiscoverer
    {
        /// <inheritdoc />
        public IEnumerable<IXunitTestCase> Discover(
            ITestFrameworkDiscoveryOptions discoveryOptions,
            ITestMethod testMethod,
            IAttributeInfo factAttribute
        ) => [];
    }

    /// <summary>A first real class consumer retaining both analyzable fixture declarations.</summary>
    public sealed class FirstConsumer : IClassFixture<RunnerFixture>, IClassFixture<OrdinaryFixture>
    {
        private readonly RunnerFixture _fixture;
        private readonly OrdinaryFixture _ordinary;

        /// <summary>Records that constructor injection receives the shared initialized resource.</summary>
        /// <param name="fixture">The borrowed assembly-owned fixture.</param>
        /// <param name="ordinary">The independently owned class fixture.</param>
        public FirstConsumer(
            RunnerFixture fixture,
            OrdinaryFixture ordinary
        )
        {
            ArgumentNullException.ThrowIfNull(fixture);
            ArgumentNullException.ThrowIfNull(ordinary);

            _fixture = fixture;
            _ordinary = ordinary;

            fixture.ConsumerFixtures.Enqueue(ordinary);
        }

        /// <summary>Checks lifetime state through the real xUnit test method invocation.</summary>
        [ProbeFact]
        public void TestBody()
        {
            Assert.Equal(1, _fixture.InitializeCount);
            Assert.Equal(0, _fixture.DisposeCount);
            Assert.Equal(1, _ordinary.InitializeCount);
            Assert.Equal(0, _ordinary.DisposeCount);
        }
    }

    /// <summary>A second real class consumer in an independent xUnit collection.</summary>
    public sealed class SecondConsumer : IClassFixture<RunnerFixture>, IClassFixture<OrdinaryFixture>
    {
        private readonly RunnerFixture _fixture;
        private readonly OrdinaryFixture _ordinary;

        /// <summary>Records a second constructor receiving the same initialized resource.</summary>
        /// <param name="fixture">The borrowed assembly-owned fixture.</param>
        /// <param name="ordinary">The independently owned class fixture.</param>
        public SecondConsumer(
            RunnerFixture fixture,
            OrdinaryFixture ordinary
        )
        {
            ArgumentNullException.ThrowIfNull(fixture);
            ArgumentNullException.ThrowIfNull(ordinary);

            _fixture = fixture;
            _ordinary = ordinary;

            fixture.ConsumerFixtures.Enqueue(ordinary);
        }

        /// <summary>Checks shared lifetime without placing the instance in this class's teardown map.</summary>
        [ProbeFact]
        public void TestBody()
        {
            Assert.Equal(1, _fixture.InitializeCount);
            Assert.Equal(0, _fixture.DisposeCount);
            Assert.Equal(1, _ordinary.InitializeCount);
            Assert.Equal(0, _ordinary.DisposeCount);
        }
    }

    /// <summary>A real unit-only case whose constructor does not request a container fixture.</summary>
    public sealed class UnitConsumer
    {
        /// <summary>Executes only when manually selected, without any fixture mapping.</summary>
        [ProbeFact]
        public void TestBody() => GC.KeepAlive(this);
    }
}
