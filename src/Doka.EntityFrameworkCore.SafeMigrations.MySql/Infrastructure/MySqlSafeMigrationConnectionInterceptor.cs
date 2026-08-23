namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed class MySqlSafeMigrationConnectionInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result
    )
    {
        ValidateIfSafeMigrationCommand(command);

        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        ValidateIfSafeMigrationCommand(command);

        return ValueTask.FromResult(result);
    }

    private static void ValidateIfSafeMigrationCommand(
        DbCommand command
    )
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.CommandText.Contains("__doka_sm_assert", StringComparison.Ordinal)
            || command.CommandText.Contains("@doka_sm_", StringComparison.Ordinal))
        {
            MySqlSafeMigrationConnectionValidator.Validate(
                command.Connection
                ?? throw new InvalidOperationException("The MySQL safe migration command has no connection."));
        }
    }
}
