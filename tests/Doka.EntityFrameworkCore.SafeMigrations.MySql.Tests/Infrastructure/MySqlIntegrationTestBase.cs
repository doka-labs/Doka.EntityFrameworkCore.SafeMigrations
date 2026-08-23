namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public abstract class MySqlIntegrationTestBase : IClassFixture<MySqlEngineContainerFixture>
{
    protected MySqlIntegrationTestBase(
        MySqlEngineContainerFixture fixture
    )
    {
        Fixture = fixture;
    }

    protected MySqlEngineContainerFixture Fixture { get; }

    protected SafeMigrationDbContext CreateContext(
        string connectionString,
        bool registerSafeMigrations = true
    ) => new(connectionString, Fixture.ServerVersion, registerSafeMigrations);

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
        string connectionString,
        string sql
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    protected static async Task ExecuteMigrationScriptAsync(
        string connectionString,
        string script
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        var delimiter = ";";
        var statement = new StringBuilder();
        foreach (var line in script
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n'))
        {
            var trimmed = line.Trim();
            if (statement.Length == 0
                && trimmed.StartsWith("DELIMITER ", StringComparison.OrdinalIgnoreCase))
            {
                delimiter = trimmed["DELIMITER ".Length..]
                    .Trim();
                if (delimiter.Length == 0)
                {
                    throw new InvalidOperationException("A migration script declared an empty delimiter.");
                }

                continue;
            }

            statement.AppendLine(line);
            var statementText = statement
                .ToString()
                .TrimEnd();

            if (!statementText.EndsWith(delimiter, StringComparison.Ordinal))
            {
                continue;
            }

            statementText = statementText[..^delimiter.Length]
                .Trim();
            statement.Clear();
            if (statementText.Length == 0)
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = statementText;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        if (!string.IsNullOrWhiteSpace(statement.ToString()))
        {
            throw new InvalidOperationException("A migration script ended with an unterminated statement.");
        }
    }

    protected static async Task<int> ScalarIntAsync(
        string connectionString,
        string sql
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }

    protected static async Task<int> ContextScalarIntAsync(
        DbContext context,
        string sql
    )
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }

    protected static async Task<string> ScalarStringAsync(
        string connectionString,
        string sql
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture) ?? "<null>";
    }
}
