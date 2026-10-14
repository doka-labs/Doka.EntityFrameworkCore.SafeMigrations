namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Owns one fixture through startup and final cleanup, including partially failed startup.</summary>
/// <typeparam name="TFixture">The assembly-owned asynchronous fixture.</typeparam>
internal sealed class SharedContainerFixtureLifecycle<TFixture>
    where TFixture : class, IAsyncLifetime, new()
{
    private readonly Func<TFixture> _createFixture;
    private readonly List<Exception> _failures = [];
    private readonly object _gate = new();
    private Task? _disposal;
    private TFixture? _fixture;
    private Task? _initialization;

    /// <summary>Creates a lifecycle with an injectable factory for resource-free contract tests.</summary>
    /// <param name="createFixture">The factory, or the fixture's public constructor when omitted.</param>
    public SharedContainerFixtureLifecycle(
        Func<TFixture>? createFixture = null
    ) => _createFixture = createFixture ?? (() => new TFixture());

    /// <summary>Gets the owned instance; consumers must await initialization before using it.</summary>
    public TFixture? Fixture
    {
        get
        {
            lock (_gate)
            {
                return _fixture;
            }
        }
    }

    /// <summary>Gets both startup and cleanup failures for an unexpected runner failure.</summary>
    public IReadOnlyList<Exception> Failures
    {
        get
        {
            lock (_gate)
            {
                return _failures.ToArray();
            }
        }
    }

    /// <summary>Initializes once only when the selected tests need the fixture.</summary>
    /// <param name="required">Whether a runnable selected test requires constructor injection.</param>
    /// <returns>The shared initialization task, or a completed task without constructing a resource.</returns>
    public Task InitializeAsync(
        bool required
    )
    {
        lock (_gate)
        {
            if (!required)
            {
                return Task.CompletedTask;
            }

            if (_disposal is not null)
            {
                throw new InvalidOperationException("Fixture initialization cannot follow final cleanup.");
            }

            return _initialization ??= InitializeCoreAsync();
        }
    }

    /// <summary>Awaits any ongoing startup and disposes the owned instance exactly once.</summary>
    /// <returns>The shared cleanup task; cleanup errors remain observable on every await.</returns>
    public Task DisposeAsync()
    {
        lock (_gate)
        {
            return _disposal ??= DisposeCoreAsync(_initialization);
        }
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            // WHY: Startup can allocate a container before failing. Recording
            // ownership first lets the final cleanup release that partial state.
            _fixture = _createFixture();

            await _fixture.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordFailure(exception);

            throw;
        }
    }

    private async Task DisposeCoreAsync(
        Task? initialization
    )
    {
        if (initialization is not null)
        {
            try
            {
                // WHY: Cancellation of the runner must not abandon startup
                // while cleanup races the same resource's initialization.
                await initialization.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // WHY: The startup caller and retained failure list already
                // report this error; cleanup must still attempt disposal.
            }
        }

        if (_fixture is null)
        {
            return;
        }

        try
        {
            await _fixture.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordFailure(exception);

            throw;
        }
    }

    private void RecordFailure(
        Exception exception
    )
    {
        lock (_gate)
        {
            _failures.Add(exception);
        }
    }
}
