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

    /// <summary>Creates a unique database owned by this test instance rather than its class or assembly.</summary>
    /// <param name="cancellationToken">Cancels database creation.</param>
    /// <returns>The new database's root connection string.</returns>
    protected async Task<string> CreateDatabaseAsync(
        CancellationToken cancellationToken
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(cancellationToken);
        _ownedDatabases.Add(connectionString);

        return connectionString;
    }

    /// <summary>Tracks an explicitly enabled pool belonging to one of this test's database leases.</summary>
    /// <param name="connectionString">The derived connection string with the test's pooling settings.</param>
    protected void TrackOwnedPool(
        string connectionString
    )
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
            throw new ArgumentException(
                "The pool does not belong to this test's database lease.",
                nameof(connectionString));
        }

        _ownedPools.Add(connectionString);
    }

    protected SafeMigrationDbContext CreateContext(
        string connectionString,
        bool registerSafeMigrations = true
    ) => new(connectionString, Fixture.ServerVersion, registerSafeMigrations);

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

    /// <summary>Performs the execute sql test operation with explicit cancellation.</summary>
    /// <param name="connectionString">The connection string for this operation.</param>
    /// <param name="sql">The sql for this operation.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    protected static async Task ExecuteSqlAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Performs the execute migration script test operation with explicit cancellation.</summary>
    /// <param name="connectionString">The connection string for this operation.</param>
    /// <param name="script">The script for this operation.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    protected static async Task ExecuteMigrationScriptAsync(
        string connectionString,
        string script,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var delimiter = ";";
        var statement = new StringBuilder();
        foreach (var line in script
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();

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
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(statement.ToString()))
        {
            throw new InvalidOperationException("A migration script ended with an unterminated statement.");
        }
    }

    /// <summary>Performs the scalar int test operation with explicit cancellation.</summary>
    /// <param name="connectionString">The connection string for this operation.</param>
    /// <param name="sql">The sql for this operation.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    protected static async Task<int> ScalarIntAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>Performs the context scalar int test operation with explicit cancellation.</summary>
    /// <param name="context">The context for this operation.</param>
    /// <param name="sql">The sql for this operation.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    protected static async Task<int> ContextScalarIntAsync(
        DbContext context,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>Performs the scalar string test operation with explicit cancellation.</summary>
    /// <param name="connectionString">The connection string for this operation.</param>
    /// <param name="sql">The sql for this operation.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    protected static async Task<string> ScalarStringAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture)
            ?? "<null>";
    }
}
