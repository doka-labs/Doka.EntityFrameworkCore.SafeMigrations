namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Owns one configured native engine and retains database/login cleanup ownership until release succeeds.</summary>
public sealed class MySqlEngineContainerFixture : IAsyncLifetime, IDisposable
{
    private const string BootstrapDatabase = "bootstrap";
    private const string RootPassword = "rootpw";
    private const string RootUser = "root";

    private static readonly TimeSpan s_startupTimeout = TimeSpan.FromMinutes(3);

    private readonly Dictionary<string, string> _createdDatabases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _createdUsers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _databaseLifecycleLock = new(1, 1);
    private readonly string _engine;
    private readonly MySqlContainer _container;
    private readonly string _version;
    private bool _disposed;

    /// <summary>Creates the configured engine without overriding its native table-name mode.</summary>
    public MySqlEngineContainerFixture() : this(lowerCaseTableNames: null) { }

    /// <summary>Creates a dedicated engine with an optional initialization-only table-name mode.</summary>
    /// <param name="lowerCaseTableNames">The native owner-name mode, or null to preserve the image default.</param>
    internal MySqlEngineContainerFixture(int? lowerCaseTableNames)
    {
        if (lowerCaseTableNames is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(lowerCaseTableNames));
        }

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

        if (lowerCaseTableNames is { } ownerMode)
        {
            // WHY: MySQL fixes this mode when its data directory is initialized.
            // A dedicated container avoids mutating or reusing the shared fixture.
            builder = builder.WithCommand("--lower-case-table-names="
                + ownerMode.ToString(CultureInfo.InvariantCulture));
        }

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

    /// <summary>Gets the provider version matching the configured native engine.</summary>
    public MySqlServerVersion ServerVersion =>
        _engine switch
        {
            "mysql" => MySqlServerVersion.MySql(ParseVersion(_version)),
            "mariadb" => MySqlServerVersion.MariaDb(ParseVersion(_version)),
            _ => throw new InvalidOperationException($"Unsupported test engine '{_engine}'."),
        };

    /// <summary>Gets whether the configured engine uses MariaDB native contracts.</summary>
    public bool IsMariaDb => _engine == "mariadb";

    private string RootConnectionString => BuildConnectionString(BootstrapDatabase, RootUser, RootPassword);

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        using var startupCancellation = new CancellationTokenSource(s_startupTimeout);

        await _container.StartAsync(startupCancellation.Token);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => MySqlFixtureLifecycle.DisposeAsync(
        DropOwnedDatabasesAsync,
        async () => await _container.DisposeAsync(),
        Dispose);

    private async Task DropOwnedDatabasesAsync()
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _databaseLifecycleLock.Dispose();
        _disposed = true;
    }

    /// <summary>Creates a unique database while retaining provisional ownership across native failures.</summary>
    /// <param name="cancellationToken">Cancels lifecycle admission or native database creation.</param>
    /// <returns>The exact root lease used by the test to request database release.</returns>
    public async Task<string> CreateDatabaseAsync(
        CancellationToken cancellationToken
    )
    {
        await _databaseLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            var databaseName = $"sm_{Guid.NewGuid():N}";
            var connectionString = BuildConnectionString(databaseName, RootUser, RootPassword);

            await using var connection = new MySqlConnection(RootConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE `{databaseName}`;";
            await MySqlFixtureLifecycle.CreateDatabaseAsync(
                () =>
                {
                    _createdDatabases.Add(databaseName, connectionString);
                    _createdUsers.Add(databaseName, new HashSet<string>(StringComparer.Ordinal));
                },
                async token =>
                {
                    await command.ExecuteNonQueryAsync(token);
                },
                cancellationToken);

            return connectionString;
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    /// <summary>Creates a unique login restricted to one fixture-owned database.</summary>
    /// <param name="rootConnectionString">The original root lease issued for the owned database.</param>
    /// <param name="cancellationToken">Cancels login creation and privilege assignment.</param>
    /// <returns>The database-scoped login connection string; the fixture retains login cleanup ownership.</returns>
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
            RequireOwnedDatabase(rootConnectionString);

            await using var connection = new MySqlConnection(RootConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            // WHY: Even a partial CREATE USER / GRANT failure must retain ownership for safe final cleanup.
            _createdUsers[database].Add(user);
            command.CommandText = $"CREATE USER `{user}`@'%' IDENTIFIED BY '{password}'; "
                + "GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES, "
                + $"DROP, CREATE TEMPORARY TABLES ON `{database}`.* TO `{user}`@'%';";
            await command.ExecuteNonQueryAsync(cancellationToken);

            return BuildConnectionString(database, user, password);
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    /// <summary>Releases only an exact database lease issued by this fixture, retaining failed cleanup for disposal.</summary>
    /// <param name="connectionString">The original root connection string returned during database creation.</param>
    /// <param name="cancellationToken">Cancels resource cleanup.</param>
    public async Task ReleaseDatabaseAsync(string connectionString, CancellationToken cancellationToken)
    {
        await _databaseLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            var database = RequireOwnedDatabase(connectionString);
            await using var connection = new MySqlConnection(RootConnectionString);
            await connection.OpenAsync(cancellationToken);
            await DropDatabaseAndUsersAsync(connection, database, cancellationToken);
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    private string RequireOwnedDatabase(string connectionString)
    {
        var database = new MySqlConnectionStringBuilder(connectionString).Database;

        if (!_createdDatabases.TryGetValue(database, out var ownedConnectionString)
            || !string.Equals(connectionString, ownedConnectionString, StringComparison.Ordinal))
        {
            throw new ArgumentException("The database lease was not issued by this MySQL fixture.",
                nameof(connectionString));
        }

        return database;
    }

    /// <summary>
    /// Builds a connection string for one database and login.
    /// </summary>
    /// <remarks>
    /// WHY: Pooling stays off although every analysis opens its own connection. Each test
    /// owns a distinct database, so the connector would keep one pool per test instead of
    /// reusing sessions, and the retained pools exhaust the server's connection limit long
    /// before the suite ends. A MySQL login is cheap enough to pay per open.
    /// </remarks>
    /// <param name="database">The target database.</param>
    /// <param name="user">The login used by the caller.</param>
    /// <param name="password">The login password.</param>
    /// <returns>The composed connection string.</returns>
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

        List<Exception>? failures = null;

        foreach (var database in _createdDatabases.Keys.ToArray())
        {
            try
            {
                await DropDatabaseAndUsersAsync(connection, database, cancellationToken);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("MySQL owned database cleanup failed.", failures);
        }
    }

    private async Task DropDatabaseAndUsersAsync(
        MySqlConnection connection,
        string database,
        CancellationToken cancellationToken
    )
    {
        await using var dropDatabase = connection.CreateCommand();
        dropDatabase.CommandText = $"DROP DATABASE IF EXISTS `{database}`;";
        await dropDatabase.ExecuteNonQueryAsync(cancellationToken);

        foreach (var user in _createdUsers[database].ToArray())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP USER IF EXISTS `{user}`@'%';";
            await command.ExecuteNonQueryAsync(cancellationToken);
            _createdUsers[database].Remove(user);
        }

        // WHY: Ownership is forgotten only after both database and login cleanup succeed, allowing a final retry.
        _createdUsers.Remove(database);
        _createdDatabases.Remove(database);
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
