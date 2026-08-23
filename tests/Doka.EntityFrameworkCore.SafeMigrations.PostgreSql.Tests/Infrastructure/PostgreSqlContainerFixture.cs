namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlContainerFixture : IAsyncLifetime, IDisposable
{
    private const string BootstrapDatabase = "bootstrap";
    private const string Password = "postgrespw";
    private const string Username = "postgres";

    private static readonly TimeSpan s_startupTimeout = TimeSpan.FromMinutes(3);

    private readonly List<string> _createdDatabases = [];
    private readonly SemaphoreSlim _databaseLifecycleLock = new(1, 1);
    private readonly PostgreSqlContainer _container;
    private readonly string _version;
    private bool _disposed;

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
    }

    public Version ServerVersion => Version.Parse(_version);

    private string RootConnectionString
    {
        get
        {
            var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
            {
                IncludeErrorDetail = true,
                Pooling = false,
            };

            return connectionString.ConnectionString;
        }
    }

    public async Task InitializeAsync()
    {
        using var startupCancellation = new CancellationTokenSource(s_startupTimeout);

        await _container.StartAsync(startupCancellation.Token);
    }

    public async Task DisposeAsync()
    {
        Exception? cleanupFailure = null;

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
            cleanupFailure = exception;
        }

        try
        {
            await _container.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupFailure ??= exception;
        }
        finally
        {
            Dispose();
        }

        if (cleanupFailure is not null)
        {
            throw new InvalidOperationException("PostgreSQL test-container cleanup failed.", cleanupFailure);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _databaseLifecycleLock.Dispose();
        _disposed = true;
    }

    public async Task<string> CreateDatabaseAsync(
        CancellationToken cancellationToken
    )
    {
        await _databaseLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            var database = $"sm_{Guid.NewGuid():N}";

            await using var connection = new NpgsqlConnection(RootConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{database}\";";
            await command.ExecuteNonQueryAsync(cancellationToken);

            _createdDatabases.Add(database);

            return new NpgsqlConnectionStringBuilder(RootConnectionString)
            {
                Database = database,
            }.ConnectionString;
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    private async Task DropCreatedDatabasesAsync(
        CancellationToken cancellationToken
    )
    {
        if (_createdDatabases.Count == 0)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(RootConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var database in _createdDatabases)
        {
            await using var terminate = connection.CreateCommand();
            terminate.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
                + "WHERE datname = @database AND pid <> pg_backend_pid();";
            terminate.Parameters.AddWithValue("database", database);
            await terminate.ExecuteNonQueryAsync(cancellationToken);

            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\";";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        _createdDatabases.Clear();
    }
}
