namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public abstract class SqliteIntegrationTestBase
{
    internal static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(CancellationToken.None);

        return connection;
    }

    protected static SqliteSafeMigrationTestContext CreateContext(
        DbConnection connection,
        bool registerSafeMigrations = true
    ) => new(connection, registerSafeMigrations);

    protected static async Task ExecuteOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
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

    protected static async Task ExecuteSqlAsync(
        DbConnection connection,
        string sql
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    protected static async Task<int> ScalarIntAsync(
        DbConnection connection,
        string sql
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);
    }
}
