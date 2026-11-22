namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks;

/// <summary>Owns only a generated PostgreSQL database, never the database supplied in native configuration.</summary>
internal sealed class PostgreSqlLiveBenchmarkDatabase : IAsyncDisposable
{
    private const string DatabasePrefix = "doka_sm_bdn_pg_";
    private readonly string _database;
    private NpgsqlConnection? _administration;
    private string _connectionString = string.Empty;
    private NativeContext? _context;
    private bool _mayOwnDatabase;

    /// <summary>Allocates an identity that cannot alias an externally configured database.</summary>
    private PostgreSqlLiveBenchmarkDatabase()
    {
        _database = DatabasePrefix
            + Guid
                .NewGuid()
                .ToString("N");
    }

    /// <summary>Gets the native provider services configured against the generated database.</summary>
    internal DbContext Context => _context ?? throw new InvalidOperationException("Native setup is incomplete.");

    /// <summary>Gets the actual native numeric server identity observed outside measured work.</summary>
    internal int ServerVersionNumber { get; private set; }

    /// <summary>Attempts every owned cleanup stage and preserves the first actual failure.</summary>
    /// <returns>Completion of context disposal, pool release, owned database removal and admin disposal.</returns>
    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        await DisposeResourceAsync(_context, "SafeMigrations.PostgreSqlLiveContextCleanupException");
        _context = null;

        try
        {
            if (_connectionString.Length != 0)
            {
                // WHY: Clear only this GUID database's pool; global clearing would affect caller-owned pools.
                using var poolIdentity = CreateConnection();
                NpgsqlConnection.ClearPool(poolIdentity);
            }
        }
        catch (Exception exception)
        {
            Retain(exception, "SafeMigrations.PostgreSqlLivePoolCleanupException");
        }

        try
        {
            if (_mayOwnDatabase && _administration is not null)
            {
                if (!_database.StartsWith(DatabasePrefix, StringComparison.Ordinal)
                    || !Guid.TryParseExact(_database.AsSpan(DatabasePrefix.Length), "N", out _))
                {
                    throw new InvalidOperationException("Refusing to remove an unowned native benchmark database.");
                }

                if (_administration.State != ConnectionState.Open)
                {
                    await _administration.OpenAsync(CancellationToken.None);
                }

                var command = _administration.CreateCommand();
                Exception? dropFailure = null;
                try
                {
                    command.CommandTimeout = 180;
                    command.CommandText = $"DROP DATABASE IF EXISTS \"{_database}\";";
                    await command.ExecuteNonQueryAsync(CancellationToken.None);
                    _mayOwnDatabase = false;
                }
                catch (Exception exception)
                {
                    dropFailure = exception;

                    throw;
                }
                finally
                {
                    await SafeMigrationAnalysisCleanup.DisposeAsync(
                        command,
                        dropFailure,
                        "SafeMigrations.PostgreSqlLiveDropCommandCleanupException");
                }
            }
        }
        catch (Exception exception)
        {
            Retain(exception, "SafeMigrations.PostgreSqlLiveDatabaseCleanupException");
        }

        await DisposeResourceAsync(_administration, "SafeMigrations.PostgreSqlLiveAdminCleanupException");
        _administration = null;
        if (failure is not null)
        {
            ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }

        return;

        // WHY: Protected cleanup deliberately has no request token; every acquired owner must finish disposal.
        async Task DisposeResourceAsync(
            IAsyncDisposable? resource,
            string evidenceKey
        )
        {
            try
            {
                await SafeMigrationAnalysisCleanup.DisposeAsync(resource, failure, evidenceKey);
            }
            catch (Exception exception)
            {
                Retain(exception, evidenceKey);
            }
        }

        void Retain(
            Exception exception,
            string evidenceKey
        )
        {
            if (failure is null)
            {
                failure = exception;
            }
            else
            {
                SafeMigrationAnalysisCleanup.RetainFailure(failure, exception, evidenceKey);
            }
        }
    }

    /// <summary>Creates and initializes one isolated native database with inherited server defaults.</summary>
    /// <param name="pooling">Whether the original pooled catalog workload is being selected.</param>
    /// <param name="cancellationToken">Cancels forward setup without abandoning acquired cleanup.</param>
    /// <returns>The owner after native database creation and provider-service initialization.</returns>
    internal static async Task<PostgreSqlLiveBenchmarkDatabase> CreateAsync(
        bool pooling,
        CancellationToken cancellationToken
    )
    {
        var database = new PostgreSqlLiveBenchmarkDatabase();
        try
        {
            await database.InitializeAsync(pooling, cancellationToken);

            return database;
        }
        catch (Exception exception)
        {
            var cleanupCompleted = await SafeMigrationAnalysisCleanup.DisposeAsync(
                database,
                exception,
                "SafeMigrations.PostgreSqlLiveSetupCleanupException");

            // WHY: BenchmarkDotNet logs setup failures itself. Native authentication/configuration errors
            // can contain caller identities; preserve failure and cleanup status, never their raw payloads.
            if (exception is OperationCanceledException)
            {
                ExceptionDispatchInfo
                    .Capture(exception)
                    .Throw();
            }

            throw AcquisitionFailure(exception, cleanupCompleted);
        }
    }

    /// <summary>Creates a caller-owned native connection without exposing its credentials as parameters.</summary>
    /// <returns>A closed connection targeting only the generated database.</returns>
    private NpgsqlConnection CreateConnection() => new(_connectionString);

    /// <summary>Acquires a caller-owned native connection without publishing authentication diagnostics.</summary>
    /// <param name="cancellationToken">Cancels actual forward connection acquisition.</param>
    /// <returns>The opened connection whose caller must await disposal.</returns>
    internal async Task<NpgsqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken
    )
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);

            return connection;
        }
        catch (Exception exception)
        {
            var cleanupCompleted = await SafeMigrationAnalysisCleanup.DisposeAsync(
                connection,
                exception,
                "SafeMigrations.PostgreSqlLiveConnectionAcquisitionCleanupException");

            if (exception is OperationCanceledException)
            {
                ExceptionDispatchInfo
                    .Capture(exception)
                    .Throw();
            }

            throw AcquisitionFailure(exception, cleanupCompleted);
        }
    }

    /// <summary>Executes fixture preparation outside the measured native workload.</summary>
    /// <param name="sql">Source-controlled SQL scoped to the generated database.</param>
    /// <param name="cancellationToken">Cancels forward fixture preparation.</param>
    /// <returns>The actual native command completion.</returns>
    internal async Task ExecuteAsync(
        string sql,
        CancellationToken cancellationToken
    )
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        NpgsqlCommand? command = null;
        Exception? failure = null;
        try
        {
            command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 180;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // WHY: Attempt both actual disposals before publishing any failure; a finally throw could mask work.
        try
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                command,
                failure,
                "SafeMigrations.PostgreSqlLiveFixtureCommandCleanupException");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                connection,
                failure,
                "SafeMigrations.PostgreSqlLiveFixtureConnectionCleanupException");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }
    }

    /// <summary>Parses private configuration and establishes only the generated database lifetime.</summary>
    /// <param name="pooling">Whether native catalog calls retain their original four-connection pool.</param>
    /// <param name="cancellationToken">Cancels forward acquisition and database creation.</param>
    private async Task InitializeAsync(
        bool pooling,
        CancellationToken cancellationToken
    )
    {
        var configured = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_POSTGRES_BENCHMARK_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("Explicit native PostgreSQL benchmark configuration is required.");
        }

        var settings = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres",
            Pooling = false,
            IncludeErrorDetail = false,
            PersistSecurityInfo = false,
        };

        _administration = new NpgsqlConnection(settings.ConnectionString);
        await _administration.OpenAsync(cancellationToken);
        var command = _administration.CreateCommand();
        Exception? creationFailure = null;
        try
        {
            command.CommandTimeout = 180;
            command.CommandText = "SELECT current_setting('server_version_num')::integer;";
            ServerVersionNumber = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            MaintenanceAttribution.Require(ServerVersionNumber > 0, "Native PostgreSQL server identity is invalid.");
            command.CommandText = $"CREATE DATABASE \"{_database}\";";

            // WHY: A transport failure after submission can leave the GUID database created; retain ownership.
            _mayOwnDatabase = true;
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase)
            {
                // WHY: Confirmed duplicate-database rejection proves creation did not acquire this identity.
                _mayOwnDatabase = false;

                throw;
            }
        }
        catch (Exception exception)
        {
            creationFailure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                command,
                creationFailure,
                "SafeMigrations.PostgreSqlLiveCreateCommandCleanupException");
        }

        settings.Database = _database;
        settings.Pooling = pooling;
        settings.NoResetOnClose = false;
        settings.MinPoolSize = 0;
        settings.MaxPoolSize = 4;
        _connectionString = settings.ConnectionString;
        _context = new NativeContext(_connectionString);
        _ = _context.Model;
        Console.WriteLine($"Native PostgreSQL server_version_num={ServerVersionNumber}.");
    }

    /// <summary>Retains categorical failure and cleanup status without credential-bearing inner diagnostics.</summary>
    /// <param name="exception">The actual failed acquisition after its cleanup has completed.</param>
    /// <param name="cleanupCompleted">Whether cleanup succeeded rather than merely being attempted.</param>
    /// <returns>A failed, payload-free diagnostic suitable for BenchmarkDotNet's own logging.</returns>
    private static InvalidOperationException AcquisitionFailure(
        Exception exception,
        bool cleanupCompleted
    ) => new(
        $"Native PostgreSQL configuration/acquisition failed ({exception.GetType().Name}); "
        + (cleanupCompleted
            ? "owned cleanup completed."
            : "owned cleanup failed."));

    /// <summary>Prevents BenchmarkDotNet from logging raw native authentication or provider payloads.</summary>
    /// <param name="exception">The native failure after its actual owned command or cleanup has completed.</param>
    /// <param name="operation">A fixed nonsecret source-controlled operation label.</param>
    /// <returns>A categorical failure with no raw provider message, inner exception or exception data.</returns>
    internal static InvalidOperationException NativeFailure(
        NpgsqlException exception,
        string operation
    ) => new($"Native PostgreSQL {operation} failed ({exception.GetType().Name}).");

    /// <summary>Configures real PostgreSQL generator and analyzer services against the isolated database.</summary>
    private sealed class NativeContext : DbContext
    {
        private readonly string _connectionString;

        /// <summary>Retains the private native connection without publishing it in benchmark parameters.</summary>
        /// <param name="connectionString">The owned GUID database connection.</param>
        internal NativeContext(
            string connectionString
        )
        {
            _connectionString = connectionString;
        }

        /// <inheritdoc />
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            // WHY: This workload also runs against the declared PostgreSQL 14 compatibility cell.
            optionsBuilder.UseNpgsql(_connectionString, provider => provider.SetPostgresVersion(14, 0));
            optionsBuilder.UsePostgreSqlSafeMigrations<NativeContext>();
        }
    }
}
