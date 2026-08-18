namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlEngineContainerFixture : IAsyncLifetime, IDisposable
{
    private readonly string _engine = ReadEnvironment("SAFE_MIGRATIONS_MYSQL_ENGINE", "mariadb");
    private readonly string _version = ReadEnvironment("SAFE_MIGRATIONS_MYSQL_VERSION", "11.8.8");

    private readonly string? _configuredImage = Environment
        .GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_IMAGE")
        ?.Trim();

    private readonly string _containerName = $"safe-migrations-mysql-{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _databaseLifecycleLock = new(1, 1);
    private readonly List<string> _createdDatabases = [];
    private readonly List<string> _createdUsers = [];
    private bool _disposed;
    private int _port;

    public MySqlServerVersion ServerVersion =>
        _engine switch
        {
            "mysql" => MySqlServerVersion.MySql(ParseVersion(_version)),
            "mariadb" => MySqlServerVersion.MariaDb(ParseVersion(_version)),
            _ => throw new InvalidOperationException($"Unsupported test engine '{_engine}'."),
        };

    public bool IsMariaDb => _engine == "mariadb";

    public string RootConnectionString => BuildConnectionString("bootstrap", "root", "rootpw");

    public async Task InitializeAsync()
    {
        var image = string.IsNullOrWhiteSpace(_configuredImage) ? $"{_engine}:{_version}" : _configuredImage;
        var databaseVariable = _engine == "mariadb" ? "MARIADB_DATABASE" : "MYSQL_DATABASE";
        var passwordVariable = _engine == "mariadb" ? "MARIADB_ROOT_PASSWORD" : "MYSQL_ROOT_PASSWORD";

        await RunDockerCommandAsync(
        [
            "run",
            "-d",
            "--name",
            _containerName,
            "-e",
            $"{passwordVariable}=rootpw",
            "-e",
            $"{databaseVariable}=bootstrap",
            "-p",
            "0:3306",
            image,
        ]);

        var portOutput = await RunDockerCommandAsync(
        [
            "port",
            _containerName,
            "3306/tcp"
        ]);

        _port = int.Parse(
            portOutput
                .Split(':')
                .Last(),
            CultureInfo.InvariantCulture);

        await WaitUntilAvailableAsync();
    }

    public async Task DisposeAsync()
    {
        Exception? cleanupFailure = null;
        try
        {
            await _databaseLifecycleLock.WaitAsync();
            try
            {
                await DropCreatedDatabasesAsync();
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
            await RunDockerCommandAsync(
            [
                "rm",
                "-f",
                _containerName
            ]);
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
        GC.SuppressFinalize(this);
    }

    public async Task<string> CreateDatabaseAsync()
    {
        await _databaseLifecycleLock.WaitAsync();
        try
        {
            var databaseName = $"sm_{Guid.NewGuid():N}";
            await using var connection = new MySqlConnection(RootConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE `{databaseName}`;";
            await command.ExecuteNonQueryAsync();
            _createdDatabases.Add(databaseName);

            return BuildConnectionString(databaseName, "root", "rootpw");
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    public async Task<string> CreateLeastPrivilegeConnectionStringAsync(
        string rootConnectionString
    )
    {
        var database = new MySqlConnectionStringBuilder(rootConnectionString).Database;
        var user = $"smu{Guid.NewGuid():N}"[..24];
        var password = $"smp{Guid.NewGuid():N}";
        await _databaseLifecycleLock.WaitAsync();

        try
        {
            await using var connection = new MySqlConnection(RootConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE USER `{user}`@'%' IDENTIFIED BY '{password}'; "
                + $"GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES, "
                + $"DROP, CREATE TEMPORARY TABLES ON `{database}`.* TO `{user}`@'%';";
            await command.ExecuteNonQueryAsync();
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
        Server = "127.0.0.1",
        Port = (uint)_port,
        UserID = user,
        Password = password,
        Database = database,
        AllowUserVariables = true,
        Pooling = true,
    }.ConnectionString;

    private async Task DropCreatedDatabasesAsync()
    {
        if (_createdDatabases.Count == 0
            || _port == 0)
        {
            return;
        }

        await using var connection = new MySqlConnection(RootConnectionString);
        await connection.OpenAsync();
        foreach (var database in _createdDatabases)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS `{database}`;";
            await command.ExecuteNonQueryAsync();
        }

        _createdDatabases.Clear();
        foreach (var user in _createdUsers)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP USER IF EXISTS `{user}`@'%';";
            await command.ExecuteNonQueryAsync();
        }

        _createdUsers.Clear();
    }

    private async Task WaitUntilAvailableAsync()
    {
        var timeoutAt = DateTime.UtcNow.AddMinutes(3);
        Exception? lastException = null;
        while (DateTime.UtcNow < timeoutAt)
        {
            try
            {
                var probe = new MySqlConnectionStringBuilder(RootConnectionString)
                {
                    ConnectionTimeout = 3,
                }.ConnectionString;
                await using var connection = new MySqlConnection(probe);
                await connection.OpenAsync();
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
                await Task.Delay(1000);
            }
        }

        var logs = await RunDockerCommandAsync(
        [
            "logs",
            "--tail",
            "40",
            _containerName
        ]);

        throw new TimeoutException(
            $"Container '{_containerName}' did not become ready. Logs:{Environment.NewLine}{logs}",
            lastException);
    }

    private static async Task<string> RunDockerCommandAsync(
        IReadOnlyList<string> arguments
    )
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Docker.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker exited with code {process.ExitCode}: {error}");
        }

        return output;
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
