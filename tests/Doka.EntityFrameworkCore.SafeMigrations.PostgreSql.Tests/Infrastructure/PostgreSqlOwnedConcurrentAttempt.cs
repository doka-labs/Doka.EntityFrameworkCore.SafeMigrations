namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Owns the observed blocked-operation sequence until its borrowed resources are no longer in use.</summary>
internal static class PostgreSqlOwnedConcurrentAttempt
{
    /// <summary>Observes a native blocker, releases it and drains the operation on every failure path.</summary>
    /// <param name="execute">Starts native work using the actual operation token.</param>
    /// <param name="observe">Proves the blocker while observing premature completion of that same task.</param>
    /// <param name="release">Commits or releases the independent blocker after its proof.</param>
    /// <param name="releaseOnFailure">Releases the independent blocker when observation or commit fails.</param>
    /// <param name="cancellationSource">The test-owned cancellation source retained until work has drained.</param>
    /// <param name="cancellationToken">Cancels forward work without interrupting protected cleanup.</param>
    /// <returns>The completed native operation's captured exception, or null on success.</returns>
    /// <exception cref="ArgumentException">The forward token does not belong to the owned source.</exception>
    internal static async Task<Exception?> RunAsync(
        Func<CancellationToken, Task> execute,
        Func<Task, CancellationToken, Task> observe,
        Func<CancellationToken, Task> release,
        Func<Task> releaseOnFailure,
        CancellationTokenSource cancellationSource,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(observe);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(releaseOnFailure);
        ArgumentNullException.ThrowIfNull(cancellationSource);

        Task? pending = null;
        Exception? primaryFailure = null;
        Exception? handledNativeFailure = null;
        var released = false;
        try
        {
            // WHY: Cleanup can stop only this owned source. A different token
            // could leave forward work pending forever after releasing its gate.
            if (cancellationToken != cancellationSource.Token)
            {
                throw new ArgumentException(
                    "The forward token must belong to the owned cancellation source.",
                    nameof(cancellationToken));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var execution = execute(cancellationToken);
            pending = execution;
            await observe(pending, cancellationToken);
            await release(cancellationToken);
            released = true;

            handledNativeFailure = await Record.ExceptionAsync(() => execution);

            return handledNativeFailure;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            // WHY: Observer, commit and cancellation callback failures cannot
            // release a DbContext while the native command still borrows it.
            var cleanupFailure = primaryFailure;
            if (cleanupFailure is null)
            {
                cleanupFailure = handledNativeFailure;
            }

            await OwnedTestOperationCleanup.CompleteAsync(
                cleanupFailure,
                pending,
                released
                    ? null
                    : releaseOnFailure,
                cancellationSource,
                afterDrain: null);
        }
    }
}
