namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

public partial class SqlServerColumnAttribution
{
    /// <summary>Executes generated batches transactionally and accepts only the expected provider refusal.</summary>
    private static int ExecuteGenerated(
        SqlConnection connection,
        IReadOnlyList<MigrationCommand> commands,
        Scenario scenario
    )
    {
        // WHY: This benchmark deliberately measures EF's synchronous runtime command path.
        // Setup, analysis and teardown use native asynchronous APIs independently of this workload.
        using var transaction = connection.BeginTransaction();
        try
        {
            // WHY: Execute the immutable prepared command slots in their authored order.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < commands.Count; index++)
            {
                var migrationCommand = commands[index];
                using var command = connection.CreateCommand();
                command.CommandTimeout = CommandTimeoutSeconds;
                command.CommandText = migrationCommand.CommandText;
                command.Transaction = transaction;
                command.ExecuteNonQuery();
            }

            if (scenario.RejectRuntime)
            {
                throw new InvalidOperationException($"{scenario.Name}: unsafe runtime unexpectedly succeeded.");
            }

            transaction.Commit();

            return commands.Count;
        }
        catch (SqlException exception) when (scenario.RejectRuntime
            && exception.Number
            == (scenario.ExpectedState == SafeMigrationObservedState.DataBlocked
                ? 51003
                : 51001))
        {
            transaction.Rollback();

            return exception.Number;
        }
    }

    /// <summary>Runs unmeasured fixture and diagnostic SQL through the native asynchronous API.</summary>
    /// <param name="connection">The owned SQL Server connection.</param>
    /// <param name="sql">The exact fixture or diagnostic SQL.</param>
    /// <param name="cancellationToken">The token controlling the native command.</param>
    /// <returns>The actual native command completion.</returns>
    private static async Task ExecuteAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Runs fixture and diagnostic SQL with an explicit finite server budget.</summary>
    private static void Execute(
        SqlConnection connection,
        string sql
    )
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Counts repeated SQL structure without changing generated batches.</summary>
    internal static int CountOccurrences(
        string source,
        string token
    )
    {
        var count = 0;
        var position = 0;
        while ((position = source.IndexOf(token, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += token.Length;
        }

        return count;
    }
}
