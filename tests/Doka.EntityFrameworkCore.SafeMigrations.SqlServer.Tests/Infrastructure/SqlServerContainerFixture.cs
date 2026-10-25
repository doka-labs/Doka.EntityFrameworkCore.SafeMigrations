namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Provides an isolated SQL Server database for each live contract test.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime, IDisposable
{
    private const string DefaultImage =
        "mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718";
    private const string Password = "SafeMigrations_Test_2026!";

    private readonly List<string> _databases = [];
    private readonly SemaphoreSlim _databaseLifecycleLock = new(1, 1);
    private readonly MsSqlContainer? _container;
    private readonly string? _rootConnectionString;
    private readonly Func<string, CancellationToken, Task>? _createDatabaseAsync;
    private readonly Func<string, CancellationToken, Task>? _dropDatabaseAsync;
    private readonly Func<Task>? _disposeContainerAsync;
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

    /// <summary>Exercises actual fixture ownership and cleanup with resource-free server callbacks.</summary>
    /// <param name="rootConnectionString">The server connection settings validated before database creation.</param>
    /// <param name="createDatabaseAsync">The callback simulating database creation and its acknowledgement.</param>
    /// <param name="dropDatabaseAsync">The callback confirming an owned database is absent.</param>
    /// <param name="disposeContainerAsync">The callback disposing the simulated container.</param>
    internal SqlServerContainerFixture(
        string rootConnectionString,
        Func<string, CancellationToken, Task> createDatabaseAsync,
        Func<string, CancellationToken, Task> dropDatabaseAsync,
        Func<Task> disposeContainerAsync
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootConnectionString);
        ArgumentNullException.ThrowIfNull(createDatabaseAsync);
        ArgumentNullException.ThrowIfNull(dropDatabaseAsync);
        ArgumentNullException.ThrowIfNull(disposeContainerAsync);

        _rootConnectionString = rootConnectionString;
        _createDatabaseAsync = createDatabaseAsync;
        _dropDatabaseAsync = dropDatabaseAsync;
        _disposeContainerAsync = disposeContainerAsync;
    }

    /// <summary>
    /// Starts the SQL Server instance before the first live test.
    /// </summary>
    public async Task InitializeAsync()
    {
        // WHY: xUnit may construct a class fixture even when every live test is skipped.
        if (_container is null || RuntimeInformation.OSArchitecture != Architecture.X64)
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
        if (_container is not null && RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            Dispose();

            return;
        }

        var cleanupFailures = new List<Exception>();

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
            cleanupFailures.Add(exception);
        }

        try
        {
            if (_disposeContainerAsync is { } disposeContainerAsync)
            {
                await disposeContainerAsync();
            }
            else if (_container is { } container)
            {
                await container.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            // WHY: A database cleanup failure must not hide an independent
            // container disposal failure encountered while completing teardown.
            cleanupFailures.Add(exception);
        }
        finally
        {
            Dispose();
        }

        if (cleanupFailures.Count != 0)
        {
            var cleanupFailure = cleanupFailures.Count == 1
                ? cleanupFailures[0]
                : new AggregateException(cleanupFailures);

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
        if (_container is not null && RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("SQL Server Linux containers require an x86-64 host.");
        }

        await _databaseLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            var database = $"sm_{Guid.NewGuid():N}";
            var connectionString = BuildTestConnectionString(RootConnectionString, database);

            // WHY: CREATE can commit even when its acknowledgement is lost or
            // cancelled. Provisional ownership permits guarded cleanup either way.
            _databases.Add(database);

            if (_createDatabaseAsync is { } createDatabaseAsync)
            {
                await createDatabaseAsync(database, cancellationToken);
            }
            else
            {
                await using var connection = new SqlConnection(RootConnectionString);
                await connection.OpenAsync(cancellationToken);

                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{database}];";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            return connectionString;
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    /// <summary>
    /// Drops one test database immediately.
    /// </summary>
    /// <remarks>
    /// WHY: Eager cleanup bounds the number of isolated databases retained during the
    /// session. Failed or cancelled drops remain owned for a later retry or final
    /// fixture cleanup.
    /// </remarks>
    /// <param name="connectionString">A connection string returned by <see cref="CreateDatabaseAsync" />.</param>
    /// <param name="cancellationToken">The token cancelling the drop.</param>
    public async Task ReleaseDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if ((_container is not null && RuntimeInformation.OSArchitecture != Architecture.X64) || _disposed)
        {
            return;
        }

        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

        await _databaseLifecycleLock.WaitAsync(cancellationToken);

        try
        {
            await ReleaseOwnedDatabaseAsync(_databases, database, _dropDatabaseAsync ?? DropDatabaseCoreAsync,
                cancellationToken);
        }
        finally
        {
            _databaseLifecycleLock.Release();
        }
    }

    /// <summary>
    /// Builds an unpooled connection string scoped to one test database.
    /// </summary>
    /// <remarks>
    /// WHY: Some tests change the database collation after login. A later logical open of that
    /// pooled session can fail its reset against the initial login state before test work starts.
    /// Unpooled connections establish a fresh login baseline for each open.
    /// </remarks>
    /// <param name="rootConnectionString">The container connection string before target database settings.</param>
    /// <param name="database">The isolated database name.</param>
    /// <returns>The unpooled connection string for that database.</returns>
    internal static string BuildTestConnectionString(
        string rootConnectionString,
        string database
    ) => new SqlConnectionStringBuilder(rootConnectionString)
    {
        InitialCatalog = database,
        Pooling = false,
        TrustServerCertificate = true,
    }.ConnectionString;

    private string RootConnectionString => new SqlConnectionStringBuilder(_rootConnectionString
        ?? _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The SQL Server fixture has no connection string."))
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

        if (_dropDatabaseAsync is { } dropDatabaseAsync)
        {
            await DropOwnedDatabasesAsync(_databases, dropDatabaseAsync, cancellationToken);

            return;
        }

        await using var connection = new SqlConnection(RootConnectionString);
        await connection.OpenAsync(cancellationToken);

        await DropOwnedDatabasesAsync(_databases,
            (database, token) => DropDatabaseAsync(connection, database, token), cancellationToken);
    }

    private async Task DropDatabaseCoreAsync(
        string database,
        CancellationToken cancellationToken
    )
    {
        await using var connection = new SqlConnection(RootConnectionString);
        await connection.OpenAsync(cancellationToken);

        await DropDatabaseAsync(connection, database, cancellationToken);
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
