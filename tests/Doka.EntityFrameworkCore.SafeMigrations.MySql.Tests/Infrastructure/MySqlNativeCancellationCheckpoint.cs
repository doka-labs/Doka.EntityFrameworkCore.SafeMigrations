namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Cancels only after the owned server session reaches the intended native statement.</summary>
internal static class MySqlNativeCancellationCheckpoint
{
    /// <summary>
    /// Observes actual native dispatch and drains the operation before returning its captured result.
    /// </summary>
    /// <typeparam name="T">The operation's captured result, including its failure and recovery evidence.</typeparam>
    /// <param name="connectionString">The uniquely owned database used by the independent observer.</param>
    /// <param name="targetSession">The physical session executing the operation.</param>
    /// <param name="statementFragment">The case-sensitive target statement fragment, not an elapsed-time guess.</param>
    /// <param name="cancellation">The operation's caller-owned cancellation source.</param>
    /// <param name="operation">The already-started operation, which must capture its expected failure.</param>
    /// <param name="cancellationToken">Cancellation of native observation, independent of the target operation.</param>
    /// <returns>The result only after native dispatch was observed and the operation actually completed.</returns>
    internal static async Task<T> CancelWhenDispatchedAsync<T>(
        string connectionString,
        long targetSession,
        string statementFragment,
        CancellationTokenSource cancellation,
        Task<T> operation,
        CancellationToken cancellationToken
    )
    {
        Exception? observerFailure = null;
        try
        {
            RequirePendingOperation(operation);
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.PROCESSLIST
                WHERE ID = @session AND COMMAND = 'Query'
                    AND LOCATE(BINARY @statement, BINARY COALESCE(INFO, '')) > 0
            );
            """;

            command.Parameters.AddWithValue("@session", targetSession);
            command.Parameters.AddWithValue("@statement", statementFragment);

            while (true)
            {
                RequirePendingOperation(operation);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture)
                    == 1)
                {
                    await cancellation.CancelAsync();

                    return await operation;
                }

                // WHY: This delay only paces native-state observation. The statement itself,
                // not machine speed or a timer firing, establishes the cancellation checkpoint.
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
        catch (Exception exception)
        {
            observerFailure = exception;

            throw;
        }
        finally
        {
            // WHY: Even a failed observer must release and drain its owned operation; a test
            // failure must not abandon a live native command or race fixture disposal. A
            // canceled producer must not replace missing native proof with a passing OCE.
            try
            {
                await cancellation.CancelAsync();
            }
            catch (Exception exception) when (observerFailure is not null)
            {
                observerFailure.Data["MySqlNativeCheckpoint.CancellationFailure"] = exception;
            }
            finally
            {
                try
                {
                    await operation;
                }
                catch (Exception exception) when (observerFailure is not null)
                {
                    observerFailure.Data["MySqlNativeCheckpoint.OperationFailure"] = exception;
                }
            }
        }
    }

    /// <summary>Rejects a missing native checkpoint instead of accepting an unrelated captured failure.</summary>
    /// <param name="operation">The operation that must still be eligible for native observation.</param>
    private static void RequirePendingOperation(
        Task operation
    )
    {
        if (operation.IsCompleted)
        {
            throw new InvalidOperationException("The operation completed before its native cancellation checkpoint.");
        }
    }
}
