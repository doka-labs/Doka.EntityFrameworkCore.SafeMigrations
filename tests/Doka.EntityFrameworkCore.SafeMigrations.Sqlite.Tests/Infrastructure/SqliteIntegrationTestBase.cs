namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public abstract class SqliteIntegrationTestBase
{
    /// <summary>Opens an owned in-memory connection and disposes it if startup fails.</summary>
    /// <param name="cancellationToken">The caller's cancellation for connection startup.</param>
    /// <returns>The open connection whose disposal belongs to the caller.</returns>
    internal static async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch
        {
            // WHY: A failed or canceled open does not transfer connection ownership to the caller.
            await connection.DisposeAsync();

            throw;
        }

        return connection;
    }

    protected static SqliteSafeMigrationTestContext CreateContext(
        DbConnection connection,
        bool registerSafeMigrations = true
    ) => new(connection, registerSafeMigrations);

    /// <summary>Executes generated migration commands with the caller's cancellation token.</summary>
    /// <param name="context">The context supplying the generator and relational connection.</param>
    /// <param name="operations">The migration operations to execute in order.</param>
    /// <param name="cancellationToken">The token cancelling command execution.</param>
    protected static async Task ExecuteOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var commands = generator.Generate(operations, context.Model);
        var connection = context.GetService<IRelationalConnection>();

        foreach (var command in commands)
        {
            _ = await command.ExecuteNonQueryAsync(connection, cancellationToken: cancellationToken);
        }
    }

    /// <summary>Executes fixture SQL on the retained caller-owned connection.</summary>
    /// <param name="connection">The open connection retained by the test.</param>
    /// <param name="sql">The synthetic SQL authored by the test.</param>
    /// <param name="cancellationToken">The caller's cancellation for command execution.</param>
    protected static async Task ExecuteSqlAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads one integer scalar from the retained caller-owned connection.</summary>
    /// <param name="connection">The open connection retained by the test.</param>
    /// <param name="sql">The synthetic scalar query authored by the test.</param>
    /// <param name="cancellationToken">The caller's cancellation for scalar execution.</param>
    /// <returns>The invariant integer result.</returns>
    protected static async Task<int> ScalarIntAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }
}
