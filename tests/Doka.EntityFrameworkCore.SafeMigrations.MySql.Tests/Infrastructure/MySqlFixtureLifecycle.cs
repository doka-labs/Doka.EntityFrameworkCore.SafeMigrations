namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Orders native fixture ownership and retains independent cleanup failures.</summary>
internal static class MySqlFixtureLifecycle
{
    /// <summary>Records provisional ownership before attempting native database creation.</summary>
    /// <param name="registerOwnership">Records the exact database lease and its initially empty login ownership.</param>
    /// <param name="createDatabase">Executes CREATE after ownership is recorded, without discarding ownership on failure.</param>
    /// <param name="cancellationToken">Cancels before registration or during native creation.</param>
    /// <returns>The creation task; its completion does not transfer fixture cleanup ownership.</returns>
    internal static async Task CreateDatabaseAsync(
        Action registerOwnership,
        Func<CancellationToken, Task> createDatabase,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // WHY: The server can create the database even when cancellation or transport failure loses its acknowledgement.
        registerOwnership();
        await createDatabase(cancellationToken);
    }

    /// <summary>Attempts every cleanup stage and preserves all failures in the reported exception.</summary>
    /// <param name="cleanupDatabases">Releases owned databases and logins before container teardown.</param>
    /// <param name="cleanupContainer">Tears down the container even if database cleanup fails.</param>
    /// <param name="cleanupSynchronization">Disposes lifecycle synchronization after both asynchronous stages.</param>
    /// <returns>The cleanup task, failing only after every cleanup callback has been attempted.</returns>
    internal static async Task DisposeAsync(
        Func<Task> cleanupDatabases,
        Func<Task> cleanupContainer,
        Action cleanupSynchronization
    )
    {
        List<Exception>? failures = null;

        try
        {
            await cleanupDatabases();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            await cleanupContainer();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        try
        {
            cleanupSynchronization();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is not null)
        {
            // WHY: Container teardown must still execute after a database drop failure, and neither cause may disappear.
            throw new InvalidOperationException("MySQL test-container cleanup failed.",
                failures.Count == 1 ? failures[0] : new AggregateException(failures));
        }
    }
}
