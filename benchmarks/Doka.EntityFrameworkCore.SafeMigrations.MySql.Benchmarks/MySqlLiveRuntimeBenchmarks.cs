namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks;

/// <summary>Measures real migration application and history-only replay from each authored starting state.</summary>
[InvocationCount(1)]

// WHY: BenchmarkDotNet's generated isolated harness derives from the benchmark type.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class MySqlLiveRuntimeBenchmarks : IAsyncDisposable
{
    private const int OperationCount = MySqlRuntimeConvergenceMigration.TableCount * 18 - 1;
    private readonly MySqlRuntimeCommandInterceptor _observer = new();
    private bool _consumed;
    private MySqlRuntimePerformanceDbContext? _context;
    private MySqlLiveBenchmarkDatabase? _database;
    private Exception? _failure;
    private string _schemaBeforeReplay = string.Empty;

    /// <summary>Gets or sets the exact legacy catalog state established outside measured execution.</summary>
    [Params("empty", "matching", "partial", "widening")]
    public string InitialState { get; set; } = "empty";

    /// <summary>Gets or sets whether the invocation applies pending migrations or replays their history.</summary>
    [Params("apply", "replay")]
    public string Phase { get; set; } = "apply";

    /// <summary>Preserves any original workload failure while draining its owned context and database.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await CleanupAsync();
            GC.SuppressFinalize(this);
        }
        catch (Exception exception)
        {
            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Acquires only an owned database before BenchmarkDotNet begins its iteration lifecycle.</summary>
    /// <returns>The asynchronous native acquisition.</returns>
    [GlobalSetup]
    public async Task Setup()
    {
        MySqlLiveBenchmarkWorkload.RequireInitialState(InitialState);
        if (Phase is not ("apply" or "replay"))
        {
            throw new InvalidOperationException("The live runtime measurement phase is not supported.");
        }

        _database = await MySqlLiveBenchmarkDatabase.CreateAsync(CancellationToken.None);
    }

    /// <summary>Resets only the owned fixture before one measured invocation, including BDN warmup.</summary>
    [IterationSetup]
    public void PrepareIteration()
    {
        try
        {
            // WHY: Fixture reset is outside the timed async workload. Genuine synchronous provider
            // calls prepare this existing path directly; no asynchronous Task is synchronously waited.
            _context?.Dispose();
            _context = null;
            var database = _database ?? throw new InvalidOperationException("Setup has not completed.");
            database.ResetRuntime(InitialState);
            _context = MySqlLiveRuntimeContext.Create(database, _observer);
            _context.Database.OpenConnection();
            if (Phase == "replay")
            {
                _context.Database.Migrate();
                _schemaBeforeReplay = ReadSchema((MySqlConnection)_context.Database.GetDbConnection());
            }

            _observer.Reset();
            _consumed = false;
        }
        catch (Exception exception)
        {
            _failure = exception;

            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Runs the real application API without explicit preflight or fixture work in the timed path.</summary>
    /// <returns>The asynchronous migration application or history-only replay.</returns>
    [Benchmark]
    public async Task MigrateAsync()
    {
        try
        {
            MySqlLiveBenchmarkWorkload.ConsumeRuntimeIteration(ref _consumed);
            var context = _context ?? throw new InvalidOperationException("Iteration setup has not completed.");
            await context.Database.MigrateAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _failure = exception;

            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Checks schema, preserved rows, migration history and transport counters outside timing.</summary>
    [IterationCleanup]
    public void VerifyIteration()
    {
        if (_failure is not null)
        {
            return;
        }

        try
        {
            var context = _context ?? throw new InvalidOperationException("Iteration setup has not completed.");
            var connection = (MySqlConnection)context.Database.GetDbConnection();
            const string tables = "TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%'";
            const string constraints = "CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'runtime_records_%'";

            Require(ReadScalar(connection, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE " + tables) == 30);
            Require(ReadScalar(connection, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE " + tables) == 390);
            Require(ReadScalar(connection,
                    $"""
                    SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                    WHERE {constraints} AND CONSTRAINT_TYPE = 'PRIMARY KEY'
                    """)
                == 30);

            Require(ReadScalar(connection,
                    $"""
                    SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                    WHERE {constraints} AND CONSTRAINT_TYPE = 'UNIQUE'
                    """)
                == 30);

            Require(ReadScalar(connection,
                    $"""
                    SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
                    WHERE {tables} AND INDEX_NAME LIKE 'IX_runtime_records_%_ParentId'
                    """)
                == 30);

            Require(ReadScalar(connection,
                    $"""
                    SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                    WHERE {constraints} AND CONSTRAINT_TYPE = 'FOREIGN KEY'
                    """)
                == 29);

            Require(ReadScalar(connection, "SELECT COUNT(*) FROM `__RuntimeWorkloadHistory`") == 2);
            Require(ReadScalar(connection,
                    $"""
                    SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE {tables}
                    AND NOT ((COLUMN_NAME = 'Id' AND COLUMN_TYPE IN ('int','int(11)') AND IS_NULLABLE = 'NO')
                    OR (COLUMN_NAME = 'ParentId' AND COLUMN_TYPE IN ('int','int(11)') AND IS_NULLABLE = 'YES')
                    OR (COLUMN_NAME = 'Code' AND COLUMN_TYPE = 'varchar(80)' AND IS_NULLABLE = 'NO')
                    OR (COLUMN_NAME LIKE 'Value%' AND COLUMN_TYPE = 'varchar(40)' AND IS_NULLABLE = 'YES')
                    OR (COLUMN_NAME = 'Marker' AND COLUMN_TYPE = 'varchar(32)' AND IS_NULLABLE = 'YES'))
                    """)
                == 0);

            var expectedRows = InitialState == "empty"
                ? 0
                : 1;

            for (var ordinal = 0; ordinal < MySqlRuntimeConvergenceMigration.TableCount; ordinal++)
            {
                var table = MySqlRuntimeConvergenceMigration.TableName(ordinal);
                Require(ReadScalar(connection, $"SELECT COUNT(*) FROM `{table}`;") == expectedRows);
                Require(ReadScalar(connection,
                        $"""
                        SELECT COUNT(*) FROM `{table}`
                        WHERE `Id` = 1 AND `Code` = 'preserved' AND `Value01` = 'original' AND `Marker` IS NULL;
                        """)
                    == expectedRows);
            }

            Require(_observer.CommandCount > 0);
            var expectedBodies = Phase == "apply"
                ? OperationCount
                : 0;

            Require(_observer.GuardedBodyCount == expectedBodies);
            if (Phase == "apply")
            {
                Require(_observer.CommandCount <= OperationCount * 14 + 5);
            }
            else
            {
                Require(_schemaBeforeReplay == ReadSchema(connection));
            }
        }
        catch (Exception exception)
        {
            _failure = exception;

            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Completes protected resource release and removes only the generated diagnostic database.</summary>
    /// <returns>The asynchronous fixture cleanup.</returns>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception exception)
        {
            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    private async ValueTask CleanupAsync()
    {
        var failure = _failure;
        try
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                _context,
                failure,
                "SafeMigrations.MySqlLiveRuntimeContextDisposeException");
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                _database,
                failure,
                "SafeMigrations.MySqlLiveRuntimeDatabaseDisposeException");
        }
    }

    private static void Require(
        bool condition
    )
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "The live runtime workload changed its deterministic fixture contract.");
        }
    }

    private static int ReadScalar(
        MySqlConnection connection,
        string sql
    )
    {
        var command = connection.CreateCommand();
        Exception? failure = null;
        var completed = false;
        try
        {
            command.CommandText = sql;
            var value = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            completed = true;

            return value;
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            failure = MySqlLiveBenchmarkDatabase.DisposeIterationResource(
                command,
                failure,
                "SafeMigrations.MySqlLiveRuntimeScalarCommandDisposeException");

            if (completed && failure is not null)
            {
                ExceptionDispatchInfo
                    .Capture(failure)
                    .Throw();
            }
        }
    }

    private static string ReadSchema(
        MySqlConnection connection
    )
    {
        var command = connection.CreateCommand();
        MySqlDataReader? reader = null;
        Exception? failure = null;
        var completed = false;
        try
        {
            command.CommandText = MySqlLiveBenchmarkWorkload.RuntimeSchemaSql;
            reader = command.ExecuteReader();
            var schema = new StringBuilder();
            while (reader.Read())
            {
                schema.AppendLine(reader.GetString(0));
            }

            completed = true;

            return schema.ToString();
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            failure = MySqlLiveBenchmarkDatabase.DisposeIterationResource(
                reader,
                failure,
                "SafeMigrations.MySqlLiveRuntimeSchemaReaderDisposeException");

            failure = MySqlLiveBenchmarkDatabase.DisposeIterationResource(
                command,
                failure,
                "SafeMigrations.MySqlLiveRuntimeSchemaCommandDisposeException");

            if (completed && failure is not null)
            {
                ExceptionDispatchInfo
                    .Capture(failure)
                    .Throw();
            }
        }
    }
}

/// <summary>Builds the actual native migration context from source-linked, test-framework-free fixtures.</summary>
internal static class MySqlLiveRuntimeContext
{
    internal static MySqlRuntimePerformanceDbContext Create(
        MySqlLiveBenchmarkDatabase database,
        MySqlRuntimeCommandInterceptor observer
    )
    {
        var options = new DbContextOptionsBuilder<MySqlRuntimePerformanceDbContext>()
            .UseMySql(database.ConnectionString,
                database.ServerVersion,
                provider => provider
                    .MigrationsAssembly(typeof(MySqlRuntimePerformanceDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__RuntimeWorkloadHistory"))
            .UseMySqlSafeMigrations()
            .AddInterceptors(observer)
            .Options;

        return new MySqlRuntimePerformanceDbContext(options);
    }
}
