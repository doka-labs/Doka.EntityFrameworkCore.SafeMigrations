namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Retains both the executing command and its cancellation request until their actual completion.</summary>
internal static class SqlServerAttentionTestOperation
{
    /// <summary>Observes readiness and attention without abandoning either owned asynchronous operation.</summary>
    /// <param name="execution">The already-started command borrowing the caller's retained session.</param>
    /// <param name="waitUntilReady">Observes the separate readiness connection without using the busy session.</param>
    /// <param name="cancellationSource">The caller-owned cancellation source attached to the command.</param>
    /// <param name="observationCancellationToken">
    /// Cancellation for readiness observation, not protected cleanup.
    /// </param>
    /// <returns>Independent readiness, cancellation-request and native-command observations.</returns>
    internal static async Task<SqlServerAttentionObservation> ObserveAsync(
        Task execution,
        Func<CancellationToken, Task> waitUntilReady,
        CancellationTokenSource cancellationSource,
        CancellationToken observationCancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(execution);

        // WHY: Record observes the actual task to completion; it does not apply
        // an observer timeout that could leave the command using disposed owners.
        var observedExecution = Record.ExceptionAsync(() => execution);
        Task<Exception?>? observedCancellation = null;
        Exception? primaryFailure = null;
        try
        {
            ArgumentNullException.ThrowIfNull(waitUntilReady);
            ArgumentNullException.ThrowIfNull(cancellationSource);

            var readinessFailure = await Record.ExceptionAsync(() =>
            {
                observationCancellationToken.ThrowIfCancellationRequested();

                return waitUntilReady(observationCancellationToken);
            });

            observedCancellation = Record.ExceptionAsync(cancellationSource.CancelAsync);
            var executionFailure = await observedExecution;
            var cancellationFailure = await observedCancellation;

            return new SqlServerAttentionObservation(readinessFailure, cancellationFailure, executionFailure);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            // WHY: CancelAsync can still be running after the command receives
            // attention. Both tasks borrow the CTS/session lifetime, so both
            // observations must finish before the caller can dispose either.
            Task pending = observedCancellation is null
                ? observedExecution
                : Task.WhenAll(observedExecution, observedCancellation);

            if (primaryFailure is not null)
            {
                // WHY: An invalid observer is a separate primary failure. The
                // successful Record wrapper must not hide a second native fault
                // while the owned raw command is still being completed.
                pending = Task.WhenAll(execution, pending);
            }

            await OwnedTestOperationCleanup.CompleteAsync(primaryFailure, pending, null, cancellationSource);
        }
    }
}

/// <summary>Separates expected native attention from readiness and cancellation-callback failures.</summary>
/// <param name="ReadinessFailure">Failure proving that the command reached the intended native stage.</param>
/// <param name="CancellationFailure">Failure completing the actual cancellation request.</param>
/// <param name="ExecutionFailure">The original native command's completion exception, if any.</param>
internal sealed record SqlServerAttentionObservation(
    Exception? ReadinessFailure,
    Exception? CancellationFailure,
    Exception? ExecutionFailure
);
