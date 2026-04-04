namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb.Tests.Integration;

public abstract class MariaDbIntegrationTestBase : IClassFixture<MariaDbContainerFixture>
{
    protected MariaDbContainerFixture Fixture { get; }

    protected MariaDbIntegrationTestBase(
        MariaDbContainerFixture fixture
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
        var connection = context.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        foreach (var command in commands)
        {
            await using var dbCommand = connection.CreateCommand();
            dbCommand.CommandText = command.CommandText;
            await dbCommand.ExecuteNonQueryAsync();
        }
    }

    protected static async Task ExecuteNonQueryAsync(
        string connectionString,
        string sql
    )
    {
        await using var connection = new MySqlConnection(connectionString);
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
