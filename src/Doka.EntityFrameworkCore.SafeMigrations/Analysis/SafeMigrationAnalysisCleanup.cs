namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Completes owned analysis cleanup without replacing an existing work failure.</summary>
internal static class SafeMigrationAnalysisCleanup
{
    /// <summary>Disposes the actual owned resource before its enclosing lifetime ends.</summary>
    /// <param name="resource">The acquired resource, or null when acquisition did not complete.</param>
    /// <param name="originalFailure">The primary failure to preserve, or null after successful work.</param>
    /// <param name="evidenceKey">The stage-specific key retaining a secondary cleanup failure.</param>
    /// <returns>
    /// True when disposal completes or no resource was acquired; false when a disposal failure is
    /// retained under the primary failure. Without a primary failure, disposal faults propagate.
    /// </returns>
    internal static async ValueTask<bool> DisposeAsync(
        IAsyncDisposable? resource,
        Exception? originalFailure,
        string evidenceKey
    )
    {
        if (resource is null)
        {
            return true;
        }

        try
        {
            // WHY: Cleanup owns the acquired resource until the native operation
            // finishes. Canceling only its observer would abandon that ownership.
            await resource.DisposeAsync();

            return true;
        }
        catch (Exception cleanupFailure) when (originalFailure is not null)
        {
            RetainFailure(originalFailure, cleanupFailure, evidenceKey);

            return false;
        }
    }

    /// <summary>Closes a runner-owned connection while retaining the original failure.</summary>
    /// <param name="connection">The connection opened by the current analysis invocation.</param>
    /// <param name="originalFailure">The primary failure to preserve, or null after successful work.</param>
    /// <returns>Completion of the actual native close operation.</returns>
    internal static async Task CloseAsync(
        DbConnection connection,
        Exception? originalFailure
    )
    {
        try
        {
            await connection.CloseAsync();
        }
        catch (Exception cleanupFailure) when (originalFailure is not null)
        {
            RetainFailure(originalFailure, cleanupFailure, "SafeMigrations.AnalysisConnectionCloseException");
        }
    }

    /// <summary>Retains all failures at one cleanup boundary without changing the primary exception.</summary>
    /// <param name="originalFailure">The original work or cancellation failure.</param>
    /// <param name="cleanupFailure">The newly observed cleanup failure.</param>
    /// <param name="evidenceKey">The stage-specific diagnostic key.</param>
    internal static void RetainFailure(
        Exception originalFailure,
        Exception cleanupFailure,
        string evidenceKey
    )
    {
        // WHY: A boundary may clean up more than one acquired resource. Retain
        // every secondary failure rather than overwriting the earlier evidence.
        try
        {
            var evidence = originalFailure.Data;
            evidence[evidenceKey] = evidence[evidenceKey] is Exception previous
                ? new AggregateException(previous, cleanupFailure)
                : cleanupFailure;
        }
        catch (Exception)
        {
            // WHY: Exception.Data is virtual and may expose a read-only or
            // throwing dictionary. Diagnostic attachment must never replace
            // the original work failure when that extension point rejects it.
        }
    }

    /// <summary>Attaches an observed recovery status without making diagnostics part of physical recovery.</summary>
    /// <param name="originalFailure">The primary failure whose identity and cancellation must be preserved.</param>
    /// <param name="evidenceKey">The established provider recovery-status key.</param>
    /// <param name="recoveryStatus">
    /// The observed physical result, never a claim that recovery was attempted successfully.
    /// </param>
    internal static void RetainRecoveryStatus(
        Exception originalFailure,
        string evidenceKey,
        string recoveryStatus
    )
    {
        try
        {
            originalFailure.Data[evidenceKey] = recoveryStatus;
        }
        catch (Exception)
        {
            // WHY: Exception.Data is an untrusted virtual extension point. Failure to store
            // a status must not replace the primary failure or interrupt native recovery.
        }
    }
}
