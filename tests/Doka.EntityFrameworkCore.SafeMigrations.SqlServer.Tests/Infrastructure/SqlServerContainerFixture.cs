namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Provides an isolated SQL Server database for each live contract test.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime, IDisposable
{
    private const string DefaultImage =
        "mcr.microsoft.com/mssql/server@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090";
    private const string Password = "SafeMigrations_Test_2026!";

    private readonly List<string> _databases = [];
    private readonly SemaphoreSlim _databaseLifecycleLock = new(1, 1);
    private readonly MsSqlContainer _container;
    private bool _disposed;

    /// <summary>
    /// Creates the pinned SQL Server test container, unless CI supplies another pinned image.
    /// </summary>
    public SqlServerContainerFixture()
    {
        var configuredImage = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_IMAGE");
        var image = string.IsNullOrWhiteSpace(configuredImage) ? DefaultImage : configuredImage.Trim();

        _container = new MsSqlBuilder(image)
            .WithPassword(Password)
            .Build();
    }

    /// <summary>
    /// Starts the SQL Server instance before the first live test.
    /// </summary>
    public async Task InitializeAsync()
    {
        // WHY: xUnit may construct a class fixture even when every live test is skipped.
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            return;
        }

        using var startupCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        await _container.StartAsync(startupCancellation.Token);
    }

    /// <summary>
    /// Drops all test databases and then disposes the container.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Dispose();

            return;
        }

        Exception? cleanupFailure = null;

        try
        {
            await _databaseLifecycleLock.WaitAsync(CancellationToken.None);

            try
            {
                await DropDatabasesAsync(CancellationToken.None);
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
            throw new InvalidOperationException("SQL Server test-container cleanup failed.", cleanupFailure);
        }
    }

    /// <summary>
    /// Releases synchronization resources after asynchronous disposal.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _databaseLifecycleLock.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Creates a unique database and returns a connection string scoped to it.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("SQL Server Linux containers require an x86-64 host.");
        }

        await _databaseLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            var database = $"sm_{Guid.NewGuid():N}";

            await using var connection = new SqlConnection(RootConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{database}];";
            await command.ExecuteNonQueryAsync(cancellationToken);

            _databases.Add(database);

            return new SqlConnectionStringBuilder(RootConnectionString)
            {
                InitialCatalog = database,
            }.ConnectionString;
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    private string RootConnectionString => new SqlConnectionStringBuilder(_container.GetConnectionString())
    {
        InitialCatalog = "master",
        Pooling = false,
        TrustServerCertificate = true,
    }.ConnectionString;

    private async Task DropDatabasesAsync(
        CancellationToken cancellationToken
    )
    {
        if (_databases.Count == 0)
        {
            return;
        }

        await using var connection = new SqlConnection(RootConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var database in _databases)
        {
            await using var command = connection.CreateCommand();

            // WHY: A failed migration may leave pooled sessions; SINGLE_USER makes fixture cleanup deterministic.
            command.CommandText = $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
                + $"DROP DATABASE [{database}];";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        _databases.Clear();
    }
}
