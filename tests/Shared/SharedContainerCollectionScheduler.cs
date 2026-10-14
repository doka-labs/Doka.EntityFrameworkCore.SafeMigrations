namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Resolves runner-option precedence while retaining xUnit's processor-count default.</summary>
/// <param name="MaximumParallelThreads">The resolved collection limit; negative values mean unlimited.</param>
/// <param name="DisableParallelization">Whether upstream scheduling must run collections sequentially.</param>
internal sealed record SharedContainerExecutionPolicy(
    int MaximumParallelThreads,
    bool DisableParallelization
)
{
    /// <summary>Combines explicit runner options, assembly attributes and the default CPU count.</summary>
    /// <param name="optionMaximum">The runner's optional maximum.</param>
    /// <param name="optionDisable">The runner's optional parallelization override.</param>
    /// <param name="attributeMaximum">The assembly's maximum; zero requests the CPU default.</param>
    /// <param name="attributeDisable">The assembly's parallelization flag.</param>
    /// <param name="processorCount">The current machine's CPU count.</param>
    /// <returns>The same limit and disable precedence used by xUnit v2.</returns>
    public static SharedContainerExecutionPolicy Resolve(
        int? optionMaximum,
        bool? optionDisable,
        int attributeMaximum,
        bool attributeDisable,
        int processorCount
    )
    {
        var maximum = optionMaximum ?? attributeMaximum;

        return new SharedContainerExecutionPolicy(
            maximum == 0 ? processorCount : maximum,
            optionDisable ?? attributeDisable);
    }
}

/// <summary>Bounds custom collection runners without relying on xUnit's private conservative semaphore.</summary>
internal sealed class SharedContainerCollectionScheduler : IDisposable
{
    private readonly SemaphoreSlim? _semaphore;

    /// <summary>Creates the explicit limit while leaving sequential scheduling to the assembly runner.</summary>
    /// <param name="policy">The resolved xUnit execution policy.</param>
    public SharedContainerCollectionScheduler(
        SharedContainerExecutionPolicy policy
    )
    {
        if (!policy.DisableParallelization && policy.MaximumParallelThreads > 0)
        {
            _semaphore = new SemaphoreSlim(policy.MaximumParallelThreads, policy.MaximumParallelThreads);
        }
    }

    /// <summary>Runs one collection after acquiring capacity, and always returns that capacity.</summary>
    /// <typeparam name="TResult">The collection runner's result type.</typeparam>
    /// <param name="runCollection">The asynchronous collection execution.</param>
    /// <param name="cancellationToken">The runner's cancellation while waiting for capacity.</param>
    /// <returns>The collection execution result.</returns>
    public async Task<TResult> RunAsync<TResult>(
        Func<Task<TResult>> runCollection,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_semaphore is null)
        {
            return await runCollection().ConfigureAwait(false);
        }

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await runCollection().ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _semaphore?.Dispose();
}
