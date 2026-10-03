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

            return TestConnectionString(database);
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    /// <summary>
    /// Drops one test database immediately and releases its connection pool.
    /// </summary>
    /// <remarks>
    /// WHY: Eager cleanup bounds the number of isolated databases retained during the
    /// session and releases each database's connection pool. Failed or cancelled drops
    /// remain owned for a later retry or final fixture cleanup.
    /// </remarks>
    /// <param name="connectionString">A connection string returned by <see cref="CreateDatabaseAsync" />.</param>
    /// <param name="cancellationToken">The token cancelling the drop.</param>
    public async Task ReleaseDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (RuntimeInformation.OSArchitecture != Architecture.X64 || _disposed)
        {
            return;
        }

        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

        // WHY: Idle pooled sessions can outlive the test and keep its database in use.
        // Clearing the pool prevents those sessions from being reused after cleanup.
        using (var pooled = new SqlConnection(connectionString))
        {
            SqlConnection.ClearPool(pooled);
        }

        await _databaseLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            await ReleaseOwnedDatabaseAsync(_databases, database, async (ownedDatabase, token) =>
            {
                await using var connection = new SqlConnection(RootConnectionString);
                await connection.OpenAsync(token);

                await DropDatabaseAsync(connection, ownedDatabase, token);
            }, cancellationToken);
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    /// <summary>
    /// Builds a pooled connection string scoped to one test database.
    /// </summary>
    /// <remarks>
    /// WHY: The root string disables pooling so master sessions never linger. Inheriting
    /// that setting would prevent reuse of database-scoped analysis connections. Pooling
    /// is enabled only for test databases and each pool is cleared when its database is released.
    /// </remarks>
    /// <param name="database">The isolated database name.</param>
    /// <returns>The pooled connection string for that database.</returns>
    private string TestConnectionString(
        string database
    ) => new SqlConnectionStringBuilder(_container.GetConnectionString())
    {
        InitialCatalog = database,
        Pooling = true,
        TrustServerCertificate = true,
    }.ConnectionString;

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

        await DropOwnedDatabasesAsync(_databases,
            (database, token) => DropDatabaseAsync(connection, database, token), cancellationToken);
    }

    /// <summary>
    /// Releases ownership only after the supplied open-and-drop operation succeeds.
    /// </summary>
    /// <remarks>The caller serializes access to the owned database list.</remarks>
    /// <param name="databases">The fixture's outstanding owned databases.</param>
    /// <param name="database">The owned database to release.</param>
    /// <param name="dropDatabaseAsync">The operation confirming the database is absent.</param>
    /// <param name="cancellationToken">The token cancelling cleanup.</param>
    internal static async Task ReleaseOwnedDatabaseAsync(
        List<string> databases,
        string database,
        Func<string, CancellationToken, Task> dropDatabaseAsync,
        CancellationToken cancellationToken
    )
    {
        if (!databases.Contains(database))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // WHY: A failed open, drop, or acknowledgement must leave the database available
        // to both eager retry and final fixture cleanup.
        await dropDatabaseAsync(database, cancellationToken);
        databases.Remove(database);
    }

    /// <summary>
    /// Removes each successful cleanup while retaining failed and unattempted databases for retry.
    /// </summary>
    /// <remarks>The caller serializes access to the owned database list.</remarks>
    /// <param name="databases">The fixture's outstanding owned databases.</param>
    /// <param name="dropDatabaseAsync">The operation confirming each database is absent.</param>
    /// <param name="cancellationToken">The token cancelling cleanup.</param>
    internal static async Task DropOwnedDatabasesAsync(
        List<string> databases,
        Func<string, CancellationToken, Task> dropDatabaseAsync,
        CancellationToken cancellationToken
    )
    {
        // WHY: Each success removes its list entry, so iteration uses a snapshot rather
        // than clearing all ownership only after the entire batch succeeds.
        foreach (var database in databases.ToArray())
        {
            await ReleaseOwnedDatabaseAsync(databases, database, dropDatabaseAsync, cancellationToken);
        }
    }

    private static async Task DropDatabaseAsync(
        SqlConnection connection,
        string database,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();

        command.CommandText = DropDatabaseCommandText(database);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Builds retry-safe cleanup for a fixture-generated owned database name.
    /// </summary>
    /// <param name="database">The fixture-generated database name.</param>
    /// <returns>The guarded single-user and drop statements.</returns>
    internal static string DropDatabaseCommandText(
        string database
    )
    {
        // WHY: The server can finish DROP before the client observes cancellation or a
        // lost acknowledgement. A retry must also succeed when the database is already absent.

        return $"IF DB_ID(N'{database}') IS NOT NULL BEGIN "
            + $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
            + $"DROP DATABASE [{database}]; END;";
    }
}
