namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks;

/// <summary>Owns only one generated diagnostic database on an explicitly supplied MySQL or MariaDB server.</summary>
internal sealed class MySqlLiveBenchmarkDatabase : IAsyncDisposable
{
    private const string ConnectionVariable = "SAFE_MIGRATIONS_MYSQL_BENCHMARK_CONNECTION";
    private const int FixtureCommandTimeoutSeconds = 180;
    private readonly MySqlConnection _administration;
    private readonly string _database;
    private readonly string _fixtureConnectionString;
    private bool _databaseMayExist;
    private MySqlServerVersion? _serverVersion;

    private MySqlLiveBenchmarkDatabase(
        MySqlConnectionStringBuilder settings
    )
    {
        _database = MySqlLiveBenchmarkWorkload.CreateDatabaseName();
        settings.Database = string.Empty;
        settings.Pooling = false;
        settings.AutoEnlist = false;
        settings.AllowUserVariables = true;
        _administration = new MySqlConnection(settings.ConnectionString);

        settings.Database = _database;
        settings.Pooling = true;
        settings.ConnectionReset = true;
        settings.MaximumPoolSize = 4;
        ConnectionString = settings.ConnectionString;
        settings.DefaultCommandTimeout = FixtureCommandTimeoutSeconds;
        _fixtureConnectionString = settings.ConnectionString;
    }

    /// <summary>Gets the private owned-database configuration, never a benchmark parameter or export value.</summary>
    internal string ConnectionString { get; }

    /// <summary>Gets the provider profile read from the already-open native administration connection.</summary>
    internal MySqlServerVersion ServerVersion => _serverVersion
        ?? throw new InvalidOperationException("The live benchmark database has not initialized.");

    /// <summary>Completes uncancellable pool cleanup and removes only the generated database.</summary>
    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        try
        {
            // WHY: Pool cleanup and database cleanup are independent ownership obligations.
            // A failed pool release cannot skip the attempt to remove the generated database.
            try
            {
                await ClearOwnedPoolAsync(ConnectionString);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                await ClearOwnedPoolAsync(_fixtureConnectionString);
            }
            catch (Exception exception)
            {
                if (failure is null)
                {
                    failure = exception;
                }
                else
                {
                    SafeMigrationAnalysisCleanup.RetainFailure(
                        failure,
                        exception,
                        "SafeMigrations.MySqlLiveBenchmarkFixturePoolCleanupException");
                }
            }

            try
            {
                if (_databaseMayExist)
                {
                    MySqlLiveBenchmarkWorkload.RequireOwnedDatabaseName(_database);
                    if (_administration.State != ConnectionState.Open)
                    {
                        await _administration.OpenAsync(CancellationToken.None);
                    }

                    await ExecuteAdministrationAsync($"DROP DATABASE IF EXISTS `{_database}`;", CancellationToken.None);
                    _databaseMayExist = false;
                }
            }
            catch (Exception exception)
            {
                if (failure is null)
                {
                    failure = exception;
                }
                else
                {
                    SafeMigrationAnalysisCleanup.RetainFailure(
                        failure,
                        exception,
                        "SafeMigrations.MySqlLiveBenchmarkDatabaseCleanupException");
                }
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo
                    .Capture(failure)
                    .Throw();
            }
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                _administration,
                failure,
                "SafeMigrations.MySqlLiveBenchmarkAdministrationDisposeException");
        }
    }

    /// <summary>Creates the owned database without selecting, modifying or removing an inherited database.</summary>
    /// <param name="cancellationToken">The operation cancellation supplied at the explicit benchmark boundary.</param>
    internal static async Task<MySqlLiveBenchmarkDatabase> CreateAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("Live MySQL benchmarks require " + ConnectionVariable + ".");
        }

        MySqlLiveBenchmarkDatabase? owner = null;
        try
        {
            owner = new MySqlLiveBenchmarkDatabase(MySqlLiveBenchmarkWorkload.ParseConnectionSettings(configured));
            await owner._administration.OpenAsync(cancellationToken);
            owner._serverVersion = MySqlServerVersion.AutoDetect(owner._administration);
            await owner.WriteNativeSettingsAsync(cancellationToken);
            MySqlLiveBenchmarkWorkload.RequireOwnedDatabaseName(owner._database);

            // WHY: A lost CREATE acknowledgement can still leave the generated database behind.
            // An explicit preexisting-name rejection below revokes provisional cleanup ownership.
            owner._databaseMayExist = true;
            try
            {
                await owner.ExecuteAdministrationAsync(
                    $"CREATE DATABASE `{owner._database}`;",
                    cancellationToken);
            }
            catch (MySqlException exception) when (exception.Number == 1007)
            {
                owner._databaseMayExist = false;

                throw;
            }

            return owner;
        }
        catch (Exception exception)
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                owner,
                exception,
                "SafeMigrations.MySqlLiveBenchmarkSetupCleanupException");

            throw ProtectFailure(exception);
        }
    }

    /// <summary>Runs owned fixture DDL outside timing without changing native server defaults.</summary>
    /// <param name="sql">The deterministic workload fixture SQL.</param>
    /// <param name="cancellationToken">The setup cancellation.</param>
    internal async Task ExecuteFixtureAsync(
        string sql,
        CancellationToken cancellationToken
    )
    {
        var connection = new MySqlConnection(_fixtureConnectionString);
        Exception? failure = null;
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, sql, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                connection,
                failure,
                "SafeMigrations.MySqlLiveBenchmarkFixtureConnectionDisposeException");
        }
    }

    /// <summary>Restores only the owned runtime tables at BenchmarkDotNet's synchronous iteration boundary.</summary>
    /// <param name="initialState">The authored legacy state restored before one measured migration invocation.</param>
    internal void ResetRuntime(
        string initialState
    )
    {
        MySqlLiveBenchmarkWorkload.RequireInitialState(initialState);
        MySqlLiveBenchmarkWorkload.RequireOwnedDatabaseName(_database);

        // WHY: BDN 0.15.8 iteration callbacks are Action, not Task. Use genuine synchronous
        // native IO only at that framework boundary; never block on an asynchronous operation.
        var connection = new MySqlConnection(_fixtureConnectionString);
        MySqlCommand? command = null;
        Exception? failure = null;
        var completed = false;
        try
        {
            connection.Open();
            command = connection.CreateCommand();
            for (var ordinal = MySqlRuntimeConvergenceMigration.TableCount - 1; ordinal >= 0; ordinal--)
            {
                command.CommandText = $"DROP TABLE IF EXISTS `{MySqlRuntimeConvergenceMigration.TableName(ordinal)}`;";
                command.ExecuteNonQuery();
            }

            command.CommandText = "DROP TABLE IF EXISTS `__RuntimeWorkloadHistory`;";
            command.ExecuteNonQuery();
            if (initialState != "empty")
            {
                for (var ordinal = 0; ordinal < MySqlRuntimeConvergenceMigration.TableCount; ordinal++)
                {
                    command.CommandText = MySqlLiveBenchmarkWorkload.BuildLegacyRuntimeTable(initialState, ordinal);
                    command.ExecuteNonQuery();
                }
            }

            completed = true;
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            failure = DisposeIterationResource(
                command,
                failure,
                "SafeMigrations.MySqlLiveBenchmarkResetCommandDisposeException");

            failure = DisposeIterationResource(
                connection,
                failure,
                "SafeMigrations.MySqlLiveBenchmarkResetConnectionDisposeException");

            if (completed && failure is not null)
            {
                ExceptionDispatchInfo
                    .Capture(failure)
                    .Throw();
            }
        }
    }

    /// <summary>Keeps native failure identity available without printing connection-bearing exception text.</summary>
    /// <param name="exception">The original acquisition, execution or cleanup failure.</param>
    internal static Exception ProtectFailure(
        Exception exception
    ) => MySqlLiveBenchmarkWorkload.ProtectFailure(exception);

    /// <summary>Retains synchronous Action-boundary cleanup failures until every owned resource was released.</summary>
    /// <param name="resource">The resource acquired by the current synchronous native callback.</param>
    /// <param name="failure">The primary work or previous cleanup failure, when present.</param>
    /// <param name="key">The low-cardinality cleanup stage identifier.</param>
    internal static Exception? DisposeIterationResource(
        IDisposable? resource,
        Exception? failure,
        string key
    )
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception exception)
        {
            if (failure is null)
            {
                return exception;
            }

            SafeMigrationAnalysisCleanup.RetainFailure(failure, exception, key);
        }

        return failure;
    }

    /// <summary>Clears one owned database pool without touching any inherited server-wide pool.</summary>
    /// <param name="connectionString">The private connection key of the generated database.</param>
    private static async Task ClearOwnedPoolAsync(
        string connectionString
    )
    {
        var connection = new MySqlConnection(connectionString);
        Exception? failure = null;
        try
        {
            // WHY: Acquired-pool cleanup remains independent of caller cancellation.
            await MySqlConnection.ClearPoolAsync(connection, CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                connection,
                failure,
                "SafeMigrations.MySqlLiveBenchmarkPoolHandleDisposeException");
        }
    }

    /// <summary>Retains native durability provenance outside timing without exporting connection details.</summary>
    /// <param name="cancellationToken">The explicit setup operation cancellation.</param>
    private async Task WriteNativeSettingsAsync(
        CancellationToken cancellationToken
    )
    {
        var command = _administration.CreateCommand();
        MySqlDataReader? reader = null;
        Exception? failure = null;
        try
        {
            command.CommandText = """
            SELECT @@innodb_flush_log_at_trx_commit, @@innodb_doublewrite,
            @@innodb_buffer_pool_size, @@log_bin;
            """;

            reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The native benchmark profile returned no durability metadata.");
            }

            // WHY: Native MariaDB values can be ON strings rather than numeric flags.
            // Keep the actual settings, never change them to make a diagnostic workload faster.
            Console.WriteLine("// MySqlNativeFixture: "
                + JsonSerializer.Serialize(new
                {
                    ServerVersion = ServerVersion.ToString(),
                    FlushLogAtTransactionCommit = reader.GetValue(0),
                    Doublewrite = reader.GetValue(1),
                    BufferPoolBytes = reader.GetValue(2),
                    BinaryLogging = reader.GetValue(3),
                }));
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        // WHY: Drain both acquisitions before throwing the retained primary failure outside a finally block.
        try
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                reader,
                failure,
                "SafeMigrations.MySqlLiveBenchmarkMetadataReaderDisposeException");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                command,
                failure,
                "SafeMigrations.MySqlLiveBenchmarkMetadataCommandDisposeException");
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

    private Task ExecuteAdministrationAsync(
        string sql,
        CancellationToken cancellationToken
    ) => ExecuteAsync(_administration, sql, cancellationToken);

    private static async Task ExecuteAsync(
        MySqlConnection connection,
        string sql,
        CancellationToken cancellationToken
    )
    {
        var command = connection.CreateCommand();
        Exception? failure = null;
        try
        {
            command.CommandTimeout = FixtureCommandTimeoutSeconds;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                command,
                failure,
                "SafeMigrations.MySqlLiveBenchmarkCommandDisposeException");
        }
    }
}
