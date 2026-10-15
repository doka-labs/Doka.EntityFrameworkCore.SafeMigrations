namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public abstract class MySqlIntegrationTestBase : IClassFixture<MySqlEngineContainerFixture>, IAsyncLifetime
{
    private readonly List<string> _ownedDatabases = [];
    private readonly HashSet<string> _ownedPools = new(StringComparer.Ordinal);

    protected MySqlIntegrationTestBase(
        MySqlEngineContainerFixture fixture
    )
    {
        Fixture = fixture;
    }

    protected MySqlEngineContainerFixture Fixture { get; }

    /// <summary>Creates a unique database owned by this test instance rather than its class or assembly.</summary>
    /// <param name="cancellationToken">Cancels database creation.</param>
    /// <returns>The new database's root connection string.</returns>
    protected async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        var connectionString = await Fixture.CreateDatabaseAsync(cancellationToken);
        _ownedDatabases.Add(connectionString);

        return connectionString;
    }

    /// <summary>Tracks an explicitly enabled pool belonging to one of this test's database leases.</summary>
    /// <param name="connectionString">The derived connection string with the test's pooling settings.</param>
    protected void TrackOwnedPool(string connectionString)
    {
        var settings = new MySqlConnectionStringBuilder(connectionString);
        if (!_ownedDatabases.Any(owned =>
            {
                var lease = new MySqlConnectionStringBuilder(owned);

                return settings.Database == lease.Database
                    && settings.Server == lease.Server
                    && settings.Port == lease.Port;
            }))
        {
            throw new ArgumentException("The pool does not belong to this test's database lease.",
                nameof(connectionString));
        }

        _ownedPools.Add(connectionString);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        List<Exception>? failures = null;

        foreach (var connectionString in _ownedPools)
        {
            try
            {
                await using var pool = new MySqlConnection(connectionString);
                await MySqlConnection.ClearPoolAsync(pool, CancellationToken.None);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        _ownedPools.Clear();

        foreach (var connectionString in _ownedDatabases)
        {
            try
            {
                await Fixture.ReleaseDatabaseAsync(connectionString, CancellationToken.None);
            }
            catch (Exception exception)
            {
                // WHY: Failed drops remain fixture-owned for final cleanup without leaking the other test's resources.
                (failures ??= []).Add(exception);
            }
        }

        _ownedDatabases.Clear();

        if (failures is not null)
        {
            throw new AggregateException("MySQL test database cleanup failed.", failures);
        }
    }

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
