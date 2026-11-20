namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Injects faults at the public runner's actual provider boundary without performing native SQL.</summary>
internal sealed class SqliteRunnerCleanupAnalyzer : ISafeMigrationProviderAnalyzer, IAsyncDisposable
{
    private readonly Exception? _analysisFailure;
    private readonly Action? _beforeAnalysis;
    private readonly CleanupScope _scope;
    private bool _scopeTransferred;

    /// <summary>Creates independently controlled work and scope-disposal boundaries.</summary>
    /// <param name="scopeCompletion">The actual scope-disposal task.</param>
    /// <param name="analysisFailure">
    /// The fault thrown from provider analysis, or null for successful empty analysis.
    /// </param>
    /// <param name="beforeAnalysis">An optional action performed when the real provider analysis is admitted.</param>
    internal SqliteRunnerCleanupAnalyzer(
        Task scopeCompletion,
        Exception? analysisFailure = null,
        Action? beforeAnalysis = null
    )
    {
        _scope = new CleanupScope(scopeCompletion);
        _analysisFailure = analysisFailure;
        _beforeAnalysis = beforeAnalysis;
    }

    /// <summary>Gets the signal that the actual provider analysis scope is being disposed.</summary>
    internal Task DisposeEntered => _scope.Entered.Task;

    /// <summary>Gets the number of acquired-scope disposal attempts.</summary>
    internal int DisposeCount => _scope.DisposeCount;

    /// <summary>Gets the number of actual provider analysis invocations.</summary>
    internal int AnalyzeCount { get; private set; }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // WHY: Acquisition transfers disposal to the runner. Only a scope that
        // was never handed off remains owned by this test provider.
        return _scopeTransferred
            ? ValueTask.CompletedTask
            : _scope.DisposeAsync();
    }

    /// <inheritdoc />
    public string ProviderId => "sqlite_cleanup_probe";

    /// <inheritdoc />
    public void ValidateContext(
        DbContext context
    ) => ArgumentNullException.ThrowIfNull(context);

    /// <inheritdoc />
    public Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
        DbContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new SafeMigrationProviderEnvironment(ProviderId, "sqlite", "3.46.0"));
    }

    /// <inheritdoc />
    public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
        DbContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        _scopeTransferred = true;

        return Task.FromResult<IAsyncDisposable>(_scope);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        AnalyzeCount++;
        _beforeAnalysis?.Invoke();
        if (_analysisFailure is not null)
        {
            // WHY: Throw at this real provider boundary so the regression can
            // distinguish preserved work stacks from replacement cleanup stacks.
            throw _analysisFailure;
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<SafeMigrationProviderAnalysis>>([]);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<SafeMigrationUnexpectedObject>>([]);
    }

    /// <summary>Exposes actual disposal admission and completion independently.</summary>
    private sealed class CleanupScope : IAsyncDisposable
    {
        private readonly Task _completion;

        /// <summary>Creates the scope with a test-controlled native disposal task.</summary>
        /// <param name="completion">The actual disposal completion.</param>
        internal CleanupScope(
            Task completion
        )
        {
            _completion = completion;
        }

        /// <summary>Gets the scope disposal admission signal.</summary>
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the exact number of actual disposal attempts.</summary>
        internal int DisposeCount { get; private set; }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Entered.TrySetResult();

            return new ValueTask(_completion);
        }
    }
}
