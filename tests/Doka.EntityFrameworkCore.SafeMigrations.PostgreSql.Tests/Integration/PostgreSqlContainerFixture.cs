namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.Integration;

public sealed class PostgreSqlContainerFixture : IAsyncLifetime, IDisposable
{
    private readonly string _containerName = $"safe-migrations-postgres-{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _databaseLifecycleLock = new(1, 1);
    private readonly List<string> _createdDatabases = [];
    private bool _disposed;
    private int _port;

    public string RootConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = _port,
                Username = "postgres",
                Password = "postgrespw",
                Database = "bootstrap",
                IncludeErrorDetail = true
            };

            return builder.ConnectionString;
        }
    }

    public async Task InitializeAsync()
    {
        await RunDockerCommandAsync(
            $"run -d --name {_containerName} -e POSTGRES_PASSWORD=postgrespw -e POSTGRES_DB=bootstrap -p 0:5432 postgres:17");

        var portOutput = await RunDockerCommandAsync($"port {_containerName} 5432/tcp");
        _port = int.Parse(
            portOutput
                .Split(':')
                .Last(),
            System.Globalization.CultureInfo.InvariantCulture);

        await WaitUntilAvailableAsync();
    }

    public async Task DisposeAsync()
    {
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

            await RunDockerCommandAsync($"rm -f {_containerName}");
        }
        catch
        {
            // Ignore cleanup failures in test teardown.
        }
        finally
        {
            Dispose();
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
            await DropCreatedDatabasesAsync();

            var databaseName = $"sm_{Guid.NewGuid():N}";
            await using var connection = new NpgsqlConnection(RootConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)};";
            await command.ExecuteNonQueryAsync();

            _createdDatabases.Add(databaseName);

            var builder = new NpgsqlConnectionStringBuilder(RootConnectionString) { Database = databaseName };

            return builder.ConnectionString;
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    private async Task WaitUntilAvailableAsync()
    {
        var timeoutAt = DateTime.UtcNow.AddMinutes(2);

        // Use a short connection timeout so each probe fails fast on a slow-starting or
        // resource-constrained host, giving ~38 retries within the 2-minute budget instead
        // of the ~7 retries allowed by the default 15-second driver timeout.
        var probeConnectionString =
            new NpgsqlConnectionStringBuilder(RootConnectionString) { Timeout = 3 }.ConnectionString;

        Exception? lastException = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            try
            {
                await using var connection = new NpgsqlConnection(probeConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                await Task.Delay(1000);
            }
        }

        // Best-effort: capture the last 30 lines of container logs to aid diagnosis.
        string? containerLogs = null;
        try
        {
            containerLogs = await RunDockerCommandAsync($"logs --tail 30 {_containerName}");
        }
        catch
        {
            // Ignore — this is a diagnostic aid only.
        }

        var message = $"PostgreSQL container '{_containerName}' did not become ready within the timeout."
            + (lastException is not null ? $"\nLast connection error: {lastException.Message}" : string.Empty)
            + (containerLogs is not null ? $"\nContainer logs (last 30 lines):\n{containerLogs}" : string.Empty);

        throw new TimeoutException(message, lastException);
    }

    private static async Task<string> RunDockerCommandAsync(
        string arguments
    )
    {
        var startInfo = new ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start docker process.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdOut = (await stdOutTask).Trim();
        var stdErr = (await stdErrTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Docker command failed with exit code {process.ExitCode}: docker {arguments}\nSTDOUT: {stdOut}\nSTDERR: {stdErr}");
        }

        return stdOut;
    }

    private async Task DropCreatedDatabasesAsync()
    {
        if (_createdDatabases.Count == 0)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(RootConnectionString);
        await connection.OpenAsync();

        foreach (var databaseName in _createdDatabases)
        {
            await using var terminateCommand = connection.CreateCommand();
            terminateCommand.CommandText = $"""
                                            SELECT pg_terminate_backend(pid)
                                            FROM pg_stat_activity
                                            WHERE datname = {SqlLiteral(databaseName)}
                                              AND pid <> pg_backend_pid();
                                            """;
            await terminateCommand.ExecuteNonQueryAsync();

            await using var dropCommand = connection.CreateCommand();
            dropCommand.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)};";
            await dropCommand.ExecuteNonQueryAsync();
        }

        _createdDatabases.Clear();
    }

    private static string QuoteIdentifier(
        string identifier
    ) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string SqlLiteral(
        string value
    ) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
