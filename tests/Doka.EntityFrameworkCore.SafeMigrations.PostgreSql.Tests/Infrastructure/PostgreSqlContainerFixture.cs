namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlContainerFixture : IAsyncLifetime, IDisposable
{
    private readonly string _version = Environment
        .GetEnvironmentVariable("SAFE_MIGRATIONS_POSTGRES_VERSION")
        ?.Trim() is { Length: > 0 } value
        ? value
        : "18.6";

    private readonly string? _configuredImage = Environment
        .GetEnvironmentVariable("SAFE_MIGRATIONS_POSTGRES_IMAGE")
        ?.Trim();

    private readonly string _containerName = $"safe-migrations-postgres-{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _databaseLifecycleLock = new(1, 1);
    private readonly List<string> _createdDatabases = [];
    private bool _disposed;
    private int _port;

    public Version ServerVersion => Version.Parse(_version);

    public string RootConnectionString =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = _port,
            Username = "postgres",
            Password = "postgrespw",
            Database = "bootstrap",
            IncludeErrorDetail = true,
            Pooling = false,
        }.ConnectionString;

    public async Task InitializeAsync()
    {
        var image = string.IsNullOrWhiteSpace(_configuredImage) ? $"postgres:{_version}" : _configuredImage;

        await RunDockerCommandAsync(
        [
            "run",
            "-d",
            "--name",
            _containerName,
            "-e",
            "POSTGRES_PASSWORD=postgrespw",
            "-e",
            "POSTGRES_DB=bootstrap",
            "-p",
            "0:5432",
            image,
        ]);

        var output = await RunDockerCommandAsync(
        [
            "port",
            _containerName,
            "5432/tcp"
        ]);

        _port = int.Parse(
            output
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
        GC.SuppressFinalize(this);
    }

    public async Task<string> CreateDatabaseAsync()
    {
        await _databaseLifecycleLock.WaitAsync();
        try
        {
            var database = $"sm_{Guid.NewGuid():N}";

            await using var connection = new NpgsqlConnection(RootConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{database}\";";
            await command.ExecuteNonQueryAsync();

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

    private async Task DropCreatedDatabasesAsync()
    {
        if (_createdDatabases.Count == 0
            || _port == 0)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(RootConnectionString);
        await connection.OpenAsync();

        foreach (var database in _createdDatabases)
        {
            await using var terminate = connection.CreateCommand();
            terminate.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity "
                + "WHERE datname = @database AND pid <> pg_backend_pid();";
            terminate.Parameters.AddWithValue("database", database);
            await terminate.ExecuteNonQueryAsync();

            await using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\";";
            await drop.ExecuteNonQueryAsync();
        }

        _createdDatabases.Clear();
    }

    private async Task WaitUntilAvailableAsync()
    {
        var timeoutAt = DateTime.UtcNow.AddMinutes(3);
        Exception? lastException = null;
        while (DateTime.UtcNow < timeoutAt)
        {
            try
            {
                var probe = new NpgsqlConnectionStringBuilder(RootConnectionString)
                {
                    Timeout = 3,
                }.ConnectionString;

                await using var connection = new NpgsqlConnection(probe);
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
}
