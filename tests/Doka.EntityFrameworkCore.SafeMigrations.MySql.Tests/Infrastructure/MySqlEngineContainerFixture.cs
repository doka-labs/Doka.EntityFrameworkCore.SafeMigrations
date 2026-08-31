namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlEngineContainerFixture : IAsyncLifetime, IDisposable
{
    private const string BootstrapDatabase = "bootstrap";
    private const string RootPassword = "rootpw";
    private const string RootUser = "root";

    private static readonly TimeSpan s_startupTimeout = TimeSpan.FromMinutes(3);

    private readonly List<string> _createdDatabases = [];
    private readonly List<string> _createdUsers = [];
    private readonly SemaphoreSlim _databaseLifecycleLock = new(1, 1);
    private readonly string _engine;
    private readonly MySqlContainer _container;
    private readonly string _version;
    private bool _disposed;

    public MySqlEngineContainerFixture()
    {
        _engine = ReadEnvironment("SAFE_MIGRATIONS_MYSQL_ENGINE", "mariadb");
        _version = ReadEnvironment("SAFE_MIGRATIONS_MYSQL_VERSION", "11.8.8");

        var configuredImage = Environment
            .GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_IMAGE")
            ?.Trim();
        var image = string.IsNullOrWhiteSpace(configuredImage) ? $"{_engine}:{_version}" : configuredImage;

        var builder = new MySqlBuilder(image)
            .WithDatabase(BootstrapDatabase)
            .WithUsername(RootUser)
            .WithPassword(RootPassword);

        if (_engine == "mariadb")
        {
            builder = builder.WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilCommandIsCompleted(
                        "mariadb",
                        BootstrapDatabase,
                        "--wait",
                        "--silent",
                        "--execute=SELECT 1;"));
        }

        _container = builder.Build();
    }

    public MySqlServerVersion ServerVersion =>
        _engine switch
        {
            "mysql" => MySqlServerVersion.MySql(ParseVersion(_version)),
            "mariadb" => MySqlServerVersion.MariaDb(ParseVersion(_version)),
            _ => throw new InvalidOperationException($"Unsupported test engine '{_engine}'."),
        };

    public bool IsMariaDb => _engine == "mariadb";

    private string RootConnectionString => BuildConnectionString(BootstrapDatabase, RootUser, RootPassword);

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
            throw new InvalidOperationException("MySQL test-container cleanup failed.", cleanupFailure);
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
            var databaseName = $"sm_{Guid.NewGuid():N}";

            await using var connection = new MySqlConnection(RootConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE `{databaseName}`;";
            await command.ExecuteNonQueryAsync(cancellationToken);

            _createdDatabases.Add(databaseName);

            return BuildConnectionString(databaseName, RootUser, RootPassword);
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    public async Task<string> CreateLeastPrivilegeConnectionStringAsync(
        string rootConnectionString,
        CancellationToken cancellationToken
    )
    {
        var database = new MySqlConnectionStringBuilder(rootConnectionString).Database;
        var user = $"smu{Guid.NewGuid():N}"[..24];
        var password = $"smp{Guid.NewGuid():N}";

        await _databaseLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            await using var connection = new MySqlConnection(RootConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE USER `{user}`@'%' IDENTIFIED BY '{password}'; "
                + "GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES, "
                + $"DROP, CREATE TEMPORARY TABLES ON `{database}`.* TO `{user}`@'%';";
            await command.ExecuteNonQueryAsync(cancellationToken);

            _createdUsers.Add(user);

            return BuildConnectionString(database, user, password);
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    private string BuildConnectionString(
        string database,
        string user,
        string password
    ) => new MySqlConnectionStringBuilder
    {
        Server = _container.Hostname,
        Port = _container.GetMappedPublicPort(MySqlBuilder.MySqlPort),
        UserID = user,
        Password = password,
        Database = database,
        AllowUserVariables = true,
        GuidFormat = MySqlConnector.MySqlGuidFormat.Binary16,
        Pooling = false,
    }.ConnectionString;

    private async Task DropCreatedDatabasesAsync(
        CancellationToken cancellationToken
    )
    {
        if (_createdDatabases.Count == 0)
        {
            return;
        }

        await using var connection = new MySqlConnection(RootConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var database in _createdDatabases)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS `{database}`;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _createdDatabases.Clear();

        foreach (var user in _createdUsers)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP USER IF EXISTS `{user}`@'%';";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _createdUsers.Clear();
    }

    private static string ReadEnvironment(
        string name,
        string fallback
    ) => Environment
        .GetEnvironmentVariable(name)
        ?.Trim()
        .ToLowerInvariant() is { Length: > 0 } value
        ? value
        : fallback;

    private static Version ParseVersion(
        string value
    )
    {
        var numeric = value
            .Split('-', StringSplitOptions.RemoveEmptyEntries)[0];

        return Version.Parse(numeric);
    }
}
