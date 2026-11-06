namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Owns test-local databases and synchronization without owning the shared native container.</summary>
public abstract class PostgreSqlIntegrationTestBase : IClassFixture<PostgreSqlContainerFixture>, IAsyncLifetime,
    IDisposable
{
    private readonly SemaphoreSlim _ownedDatabaseLock = new(1, 1);
    private readonly List<string> _ownedDatabases = [];
    private bool _cleanupStarted;
    private int _synchronizationDisposed;

    /// <summary>Uses the assembly-injected fixture while keeping database ownership local to this test.</summary>
    /// <param name="fixture">The assembly-owned PostgreSQL container fixture.</param>
    protected PostgreSqlIntegrationTestBase(
        PostgreSqlContainerFixture fixture
    )
    {
        Fixture = fixture;
    }

    /// <summary>Gets the shared fixture without transferring its startup or disposal ownership.</summary>
    protected PostgreSqlContainerFixture Fixture { get; }

    /// <summary>Leaves container startup with the assembly runner.</summary>
    /// <returns>An already completed task.</returns>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Releases this test's databases without disposing the shared container.</summary>
    /// <returns>A task completing when all of this test's database releases have been attempted.</returns>
    public async Task DisposeAsync()
    {
        await _ownedDatabaseLock.WaitAsync(CancellationToken.None);
        var failures = new List<Exception>();

        try
        {
            _cleanupStarted = true;

            // WHY: Test-local ownership must never terminate another parallel class's sessions.
            foreach (var connectionString in _ownedDatabases.ToArray())
            {
                try
                {
                    await Fixture.ReleaseDatabaseAsync(connectionString, CancellationToken.None);
                    _ownedDatabases.Remove(connectionString);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }
        finally
        {
            _ownedDatabaseLock.Release();
        }

        if (failures.Count != 0)
        {
            throw new AggregateException("The test's PostgreSQL databases could not be released.", failures);
        }
    }

    /// <summary>Disposes only test-local synchronization after asynchronous database cleanup.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases this test's managed synchronization resource without transferring fixture ownership.</summary>
    /// <param name="disposing">Whether managed resources are being released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        // WHY: xUnit runs asynchronous teardown first; the shared container is never this test's disposable resource.
        if (disposing && Interlocked.Exchange(ref _synchronizationDisposed, 1) == 0)
        {
            _ownedDatabaseLock.Dispose();
        }
    }

    /// <summary>Creates an isolated database owned by this test instance.</summary>
    /// <param name="cancellationToken">The token for creating the database.</param>
    /// <returns>The connection string registered for test-local cleanup.</returns>
    protected async Task<string> CreateDatabaseAsync(
        CancellationToken cancellationToken
    )
    {
        await _ownedDatabaseLock.WaitAsync(cancellationToken);

        try
        {
            if (_cleanupStarted)
            {
                throw new InvalidOperationException("A completed test cannot create another PostgreSQL database.");
            }

            var connectionString = await Fixture.CreateDatabaseAsync(cancellationToken);
            _ownedDatabases.Add(connectionString);

            return connectionString;
        }
        finally
        {
            _ownedDatabaseLock.Release();
        }
    }

    protected static SafeMigrationDbContext CreateContext(
        string connectionString,
        bool registerSafeMigrations = true
    ) => new(connectionString, registerSafeMigrations);

    /// <summary>Executes generated migration commands with the caller's explicit cancellation scope.</summary>
    /// <param name="context">The context supplying SQL generation and the execution connection.</param>
    /// <param name="operations">The ordered migration operations to execute.</param>
    /// <param name="cancellationToken">The token forwarded to connection opening and every command.</param>
    /// <returns>A task completing when all generated commands have executed.</returns>
    protected static async Task ExecuteOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var commands = generator.Generate(operations, context.Model);
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
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

    /// <summary>Executes fixture SQL using the caller's cancellation scope.</summary>
    /// <param name="connectionString">The isolated fixture database connection string.</param>
    /// <param name="sql">The source-controlled fixture SQL.</param>
    /// <param name="cancellationToken">Cancels the owned operation before and during asynchronous work.</param>
    /// <returns>The asynchronous fixture operation or observation.</returns>
    protected static async Task ExecuteSqlAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads an integer scalar from the isolated fixture database.</summary>
    /// <param name="connectionString">The isolated fixture database connection string.</param>
    /// <param name="sql">The source-controlled fixture SQL.</param>
    /// <param name="cancellationToken">Cancels the owned operation before and during asynchronous work.</param>
    /// <returns>The asynchronous fixture operation or observation.</returns>
    protected static async Task<int> ScalarIntAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a text scalar from the isolated fixture database.</summary>
    /// <param name="connectionString">The isolated fixture database connection string.</param>
    /// <param name="sql">The source-controlled fixture SQL.</param>
    /// <param name="cancellationToken">Cancels the owned operation before and during asynchronous work.</param>
    /// <returns>The asynchronous fixture operation or observation.</returns>
    protected static async Task<string> ScalarStringAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture)
            ?? "<null>";
    }

    /// <summary>Reads the native CHECK expression from the fixture catalog.</summary>
    /// <param name="connectionString">The isolated fixture database connection string.</param>
    /// <param name="constraintName">The fixture constraint's authored name.</param>
    /// <param name="cancellationToken">Cancels the owned operation before and during asynchronous work.</param>
    /// <returns>The asynchronous fixture operation or observation.</returns>
    protected static async Task<string> ReadCheckExpressionAsync(
        string connectionString,
        string constraintName,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_catalog.pg_get_expr(co.conbin, co.conrelid) "
            + "FROM pg_catalog.pg_constraint co WHERE co.conname = @name;";

        command.Parameters.AddWithValue("name", constraintName);

        return Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture)
            ?? "<null>";
    }
}
