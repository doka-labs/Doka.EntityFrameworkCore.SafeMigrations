namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Observes the exact native advisory-lock admission used by cancellation fixtures.</summary>
internal static class PostgreSqlNativeAdmission
{
    /// <summary>Completes advisory cleanup before releasing any resource borrowed by the actual producer.</summary>
    /// <param name="primaryFailure">The readiness failure already being rethrown by the owning test, if any.</param>
    /// <param name="execution">The actual producer, never a timeout wrapper or detached observation.</param>
    /// <param name="observer">The nonpooled connection owning the conflicting native advisory lock.</param>
    /// <param name="releaseCommand">The independent native unlock command for that same owner.</param>
    /// <param name="cancellation">The producer's owned cancellation source.</param>
    internal static Task CompleteAsync(
        Exception? primaryFailure,
        Task execution,
        DbConnection observer,
        DbCommand releaseCommand,
        CancellationTokenSource cancellation
    )
    {
        // WHY: Protected cleanup must not borrow a canceled forward-work token. Release faults
        // cannot skip either cancellation or draining the actual producer borrowing its session.
        return OwnedTestOperationCleanup.CompleteAsync(
            primaryFailure,
            execution,
            async () =>
            {
                var unlockFailure = await Record.ExceptionAsync(() =>
                    releaseCommand.ExecuteScalarAsync(CancellationToken.None));

                // WHY: Fixture owners disable pooling, so close physically releases a session
                // advisory lock even if the independent unlock SQL failed. Observe both stages.
                var closeFailure = await Record.ExceptionAsync(observer.CloseAsync);
                if (unlockFailure is not null
                    && closeFailure is not null)
                {
                    throw new AggregateException(unlockFailure, closeFailure);
                }

                if ((unlockFailure ?? closeFailure) is { } failure)
                {
                    ExceptionDispatchInfo
                        .Capture(failure)
                        .Throw();
                }
            },
            cancellation);
    }

    /// <summary>Requires the producer to wait on this observer's advisory lock before cancellation is requested.</summary>
    /// <param name="observer">The open test-owned connection holding the conflicting advisory lock.</param>
    /// <param name="producerProcessId">The exact backend executing the tested command.</param>
    /// <param name="pending">The actual operation, whose completion makes missing admission terminal.</param>
    /// <param name="cancellationToken">Cancels forward observation without owning producer cleanup.</param>
    internal static async Task WaitForAdvisoryLockAsync(
        NpgsqlConnection observer,
        int producerProcessId,
        Task pending,
        CancellationToken cancellationToken
    )
    {
        await using var command = observer.CreateCommand();
        command.CommandText = """
        SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks
        WHERE pid = @producer AND locktype = 'advisory' AND NOT granted)
        AND @observer = ANY(pg_catalog.pg_blocking_pids(@producer));
        """;

        command.Parameters.AddWithValue("producer", producerProcessId);
        command.Parameters.AddWithValue("observer", observer.ProcessID);
        while (!pending.IsCompleted)
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return;
            }

            // WHY: This is observation cadence, not a functional elapsed-time threshold.
            await Task.Delay(10, cancellationToken);
        }

        await pending;

        throw new InvalidOperationException("The PostgreSQL operation completed without native advisory admission.");
    }
}
