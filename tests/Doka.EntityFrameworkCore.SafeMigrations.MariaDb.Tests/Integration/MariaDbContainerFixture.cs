namespace Doka.EntityFrameworkCore.SafeMigrations.MariaDb.Tests.Integration;

public sealed class MariaDbContainerFixture : IAsyncLifetime, IDisposable
{
    private readonly string _containerName = $"safe-migrations-mariadb-{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _databaseLifecycleLock = new(1, 1);
    private readonly List<string> _createdDatabases = [];
    private bool _disposed;
    private int _port;

    public string RootConnectionString
    {
        get
        {
            var builder = new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = (uint)_port,
                UserID = "root",
                Password = "rootpw",
                Database = "bootstrap",
                AllowUserVariables = true
            };

            return builder.ConnectionString;
        }
    }

    public async Task InitializeAsync()
    {
        await RunDockerCommandAsync(
            $"run -d --name {_containerName} -e MARIADB_ROOT_PASSWORD=rootpw -e MARIADB_DATABASE=bootstrap -p 0:3306 mariadb:11.8");

        var portOutput = await RunDockerCommandAsync($"port {_containerName} 3306/tcp");
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
            await using var connection = new MySqlConnection(RootConnectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE `{databaseName}`;";
            await command.ExecuteNonQueryAsync();

            _createdDatabases.Add(databaseName);

            var builder = new MySqlConnectionStringBuilder(RootConnectionString) { Database = databaseName };

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
        var probeConnectionString = new MySqlConnectionStringBuilder(RootConnectionString) { ConnectionTimeout = 3 }
            .ConnectionString;

        Exception? lastException = null;

        while (DateTime.UtcNow < timeoutAt)
        {
            try
            {
                await using var connection = new MySqlConnection(probeConnectionString);
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

        var message = $"MariaDB container '{_containerName}' did not become ready within the timeout."
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

        await using var connection = new MySqlConnection(RootConnectionString);
        await connection.OpenAsync();

        foreach (var databaseName in _createdDatabases)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS `{databaseName}`;";
            await command.ExecuteNonQueryAsync();
        }

        _createdDatabases.Clear();
    }
}
