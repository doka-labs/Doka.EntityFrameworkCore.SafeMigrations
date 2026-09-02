namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public abstract class PostgreSqlIntegrationTestBase : IClassFixture<PostgreSqlContainerFixture>
{
    protected PostgreSqlIntegrationTestBase(
        PostgreSqlContainerFixture fixture
    ) => Fixture = fixture;

    protected PostgreSqlContainerFixture Fixture { get; }

    protected static SafeMigrationDbContext CreateContext(
        string connectionString,
        bool registerSafeMigrations = true
    ) => new(connectionString, registerSafeMigrations);

    protected static async Task ExecuteOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var commands = generator.Generate(operations, context.Model);
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        foreach (var command in commands)
        {
            await using var dbCommand = connection.CreateCommand();
            dbCommand.CommandText = command.CommandText;
            await dbCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    protected static async Task ExecuteSqlAsync(
        string connectionString,
        string sql
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    protected static async Task<int> ScalarIntAsync(
        string connectionString,
        string sql
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }

    protected static async Task<string> ScalarStringAsync(
        string connectionString,
        string sql
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToString(
                await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture)
            ?? "<null>";
    }

    protected static async Task<string> ReadCheckExpressionAsync(
        string connectionString,
        string constraintName
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.pg_get_expr(co.conbin, co.conrelid) "
            + "FROM pg_catalog.pg_constraint co WHERE co.conname = @name;";
        command.Parameters.AddWithValue("name", constraintName);

        return Convert.ToString(
                await command.ExecuteScalarAsync(CancellationToken.None),
                CultureInfo.InvariantCulture)
            ?? "<null>";
    }
}
