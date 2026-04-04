namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.Integration;

public abstract class PostgreSqlIntegrationTestBase : IClassFixture<PostgreSqlContainerFixture>
{
    protected PostgreSqlContainerFixture Fixture { get; }

    protected PostgreSqlIntegrationTestBase(
        PostgreSqlContainerFixture fixture
    )
    {
        Fixture = fixture;
    }

    protected static async Task ExecuteOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var commands = generator.Generate(operations, context.Model);

        await using var connection = new NpgsqlConnection(context.Database.GetConnectionString());
        await connection.OpenAsync();

        foreach (var migrationCommand in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = migrationCommand.CommandText;
            await command.ExecuteNonQueryAsync();
        }
    }

    protected static async Task ExecuteNonQueryAsync(
        string connectionString,
        string sql
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    protected static async Task<int> ExecuteScalarAsInt32Async(
        System.Data.Common.DbCommand command
    ) => Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

    protected static async Task<string?> ExecuteScalarAsStringAsync(
        System.Data.Common.DbCommand command
    ) => Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
}
