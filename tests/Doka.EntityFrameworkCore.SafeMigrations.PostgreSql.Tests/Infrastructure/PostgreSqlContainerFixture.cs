namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Owns one assembly-wide PostgreSQL container and the isolated databases requested by its tests.</summary>
public sealed class PostgreSqlContainerFixture : IAsyncLifetime, IDisposable
{
    private const string BootstrapDatabase = "bootstrap";
    private const string Password = "postgrespw";
    private const string Username = "postgres";

    private static readonly TimeSpan s_startupTimeout = TimeSpan.FromMinutes(3);

    private readonly Dictionary<string, string> _createdDatabases = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _databaseLifecycleLock = new(1, 1);
    private readonly PostgreSqlContainer? _container;
    private readonly Func<string, CancellationToken, Task> _createDatabase;
    private readonly Func<string, CancellationToken, Task> _dropDatabase;
    private readonly Func<Task> _disposeContainer;
    private readonly Func<string, string> _testConnectionString;
    private readonly string _version;
    private bool _disposed;

    /// <summary>Configures the native container from the existing PostgreSQL image and version inputs.</summary>
    public PostgreSqlContainerFixture()
    {
        _version = Environment
            .GetEnvironmentVariable("SAFE_MIGRATIONS_POSTGRES_VERSION")
            ?.Trim() is { Length: > 0 } value
            ? value
            : "18.6";

        var configuredImage = Environment
            .GetEnvironmentVariable("SAFE_MIGRATIONS_POSTGRES_IMAGE")
            ?.Trim();
        var image = string.IsNullOrWhiteSpace(configuredImage) ? $"postgres:{_version}" : configuredImage;

        _container = new PostgreSqlBuilder(image)
            .WithDatabase(BootstrapDatabase)
            .WithUsername(Username)
            .WithPassword(Password)
            .Build();

        _createDatabase = CreateNativeDatabaseAsync;
        _dropDatabase = DropNativeDatabaseAsync;
        _disposeContainer = DisposeNativeContainerAsync;
        _testConnectionString = TestConnectionString;
    }

    /// <summary>Injects resource-free lifecycle callbacks for database ownership and cleanup contract tests.</summary>
    /// <param name="createDatabase">The physical database creation callback.</param>
    /// <param name="dropDatabase">The idempotent physical database cleanup callback.</param>
    /// <param name="testConnectionString">The connection-string factory evaluated before physical creation.</param>
    /// <param name="disposeContainer">The final container cleanup callback, or no cleanup when omitted.</param>
    internal PostgreSqlContainerFixture(
        Func<string, CancellationToken, Task> createDatabase,
        Func<string, CancellationToken, Task> dropDatabase,
        Func<string, string> testConnectionString,
        Func<Task>? disposeContainer = null
    )
    {
        _version = "18.6";
        _createDatabase = createDatabase;
        _dropDatabase = dropDatabase;
        _disposeContainer = disposeContainer ?? (static () => Task.CompletedTask);
        _testConnectionString = testConnectionString;
    }

    /// <summary>Gets the configured PostgreSQL engine version used by fixture evidence.</summary>
    public Version ServerVersion => Version.Parse(_version);

    private string RootConnectionString
    {
        get
        {
            var container = _container ?? throw new InvalidOperationException("This fixture has no native container.");
            var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                IncludeErrorDetail = true,
                Pooling = false,
            };

            return connectionString.ConnectionString;
        }
    }

    /// <summary>
    /// Builds a connection string scoped to one test database.
    /// </summary>
    /// <remarks>
    /// WHY: Pooling stays off although every analysis opens its own connection. Each test
    /// owns a distinct database, so Npgsql would keep one pool per test instead of reusing
    /// sessions, and the retained backends exhaust the server's connection limit long
    /// before the suite ends. A local PostgreSQL login is cheap enough to pay per open.
    /// </remarks>
    /// <param name="database">The isolated database name.</param>
    /// <returns>The connection string for that database.</returns>
    private string TestConnectionString(
        string database
    ) => new NpgsqlConnectionStringBuilder(
        (_container ?? throw new InvalidOperationException("This fixture has no native container."))
        .GetConnectionString())
    {
        IncludeErrorDetail = true,
        Pooling = false,
        Database = database,
    }.ConnectionString;

    /// <summary>Starts the native container when the assembly runner selects live fixture consumers.</summary>
    /// <returns>A task completing at native readiness, or immediately for callback-only fixtures.</returns>
    public async Task InitializeAsync()
    {
        using var startupCancellation = new CancellationTokenSource(s_startupTimeout);

        if (_container is not null)
        {
            await _container.StartAsync(startupCancellation.Token);
        }
    }

    /// <summary>Attempts remaining database and container cleanup while retaining every failure.</summary>
    /// <returns>A task completing after assembly-owned resources have been released.</returns>
    /// <exception cref="InvalidOperationException">Database or container cleanup failed.</exception>
    public async Task DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        var cleanupFailures = new List<Exception>();

        try
        {
            await _databaseLifecycleLock.WaitAsync(CancellationToken.None);

            try
            {
                await DropCreatedDatabasesAsync(CancellationToken.None);
            }
            finally
            {
                _databaseLifecycleLock.Release();
            }
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }

        try
        {
            await _disposeContainer();
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }
        finally
        {
            Dispose();
        }

        if (cleanupFailures.Count != 0)
        {
            throw new InvalidOperationException("PostgreSQL test-container cleanup failed.",
                new AggregateException("Retained PostgreSQL fixture cleanup failures.", cleanupFailures));
        }
    }

    /// <summary>Disposes the fixture's synchronization resource after asynchronous native cleanup.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _databaseLifecycleLock.Dispose();
        _disposed = true;
    }

    /// <summary>Creates a GUID-named database with provisional ownership for uncertain acknowledgements.</summary>
    /// <param name="cancellationToken">The token for lifecycle acquisition and physical creation.</param>
    /// <returns>The original connection string required to release this database.</returns>
    public async Task<string> CreateDatabaseAsync(
        CancellationToken cancellationToken
    )
    {
        await _databaseLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var database = $"sm_{Guid.NewGuid():N}";
            var connectionString = _testConnectionString(database);

            // WHY: Cancellation or a lost acknowledgement can follow a successful server CREATE.
            // A provisional lease preserves that name for final DROP IF EXISTS cleanup.
            _createdDatabases.Add(database, connectionString);
            await _createDatabase(database, cancellationToken);

            return connectionString;
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    private async Task DisposeNativeContainerAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Releases only a database created by this assembly fixture.</summary>
    /// <remarks>
    /// WHY: Removing ownership only after a successful drop leaves failed cleanup available for final retry.
    /// </remarks>
    /// <param name="connectionString">The original connection string returned when the database was created.</param>
    /// <param name="cancellationToken">The token for the database lifecycle operation.</param>
    /// <returns>A task completing when the owned database has been released.</returns>
    public async Task ReleaseDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken
    )
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await _databaseLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            if (database is null
                || !_createdDatabases.TryGetValue(database, out var ownedConnectionString)
                || !string.Equals(connectionString, ownedConnectionString, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The database is not owned by this PostgreSQL fixture.");
            }

            await _dropDatabase(database, cancellationToken);
            _createdDatabases.Remove(database);
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    private async Task CreateNativeDatabaseAsync(
        string database,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(RootConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{database}\";";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DropNativeDatabaseAsync(
        string database,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new NpgsqlConnection(RootConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var terminate = connection.CreateCommand();
        terminate.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
            + "WHERE datname = @database AND pid <> pg_backend_pid();";
        terminate.Parameters.AddWithValue("database", database);
        await terminate.ExecuteNonQueryAsync(cancellationToken);

        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\";";
        await drop.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DropCreatedDatabasesAsync(
        CancellationToken cancellationToken
    )
    {
        if (_createdDatabases.Count == 0)
        {
            return;
        }

        var failures = new List<Exception>();
        foreach (var database in _createdDatabases.Keys.ToArray())
        {
            try
            {
                await _dropDatabase(database, cancellationToken);
                _createdDatabases.Remove(database);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException("Owned PostgreSQL databases could not be released.", failures);
        }
    }
}
