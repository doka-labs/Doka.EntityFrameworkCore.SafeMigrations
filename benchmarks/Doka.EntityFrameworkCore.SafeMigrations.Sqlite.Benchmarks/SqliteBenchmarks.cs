namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Benchmarks;

/// <summary>Measures SQLite column SQL generation at the retained operation counts.</summary>
// WHY: BenchmarkDotNet discovers methods, assigns parameters, and generates derived benchmark hosts.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class SqliteGenerationBenchmarks : IDisposable
{
    private SqliteGenerationContext _context = null!;
    private IMigrationsSqlGenerator _generator = null!;
    private IReadOnlyList<MigrationOperation> _operations = null!;

    /// <summary>Gets or sets the number of column operations generated per invocation.</summary>
    [Params(1, 100, 1000)]
    public int OperationCount { get; set; }

    /// <summary>Releases the owned benchmark context.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates the native provider services, model and operations outside the measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new SqliteGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _ = _context.Model;
        _operations = ColumnBenchmarkWorkload.CreateOperations(OperationCount);
    }

    /// <summary>Generates SQL for the configured column workload.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int Generate() => _generator.Generate(_operations, _context.Model)
        .Count;

    /// <summary>Disposes the native provider context after measurement.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources owned by this benchmark fixture.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            // WHY: Cleanup also runs when BenchmarkDotNet setup has not acquired this resource.
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            _context?.Dispose();
        }
    }
}

/// <summary>Measures the retained SQLite canonical, repair and model-managed-data SQL workloads.</summary>
// WHY: BenchmarkDotNet discovers methods, assigns parameters, and generates derived benchmark hosts.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class SqliteMigrationBenchmarks : IDisposable
{
    private SqliteGenerationContext _context = null!;
    private IMigrationsSqlGenerator _generator = null!;
    private IReadOnlyList<MigrationOperation> _modelManagedOperations = null!;
    private IReadOnlyList<MigrationOperation> _repairOperations = null!;

    /// <summary>Releases the owned benchmark context.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates the native provider services, model and fixed workloads outside the measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new SqliteGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _ = _context.Model;
        _repairOperations = ColumnBenchmarkWorkload.CreateRepairOperations(1000);
        _modelManagedOperations = ModelManagedDataBenchmarkWorkload.CreateOperations(
            _context.Database.ProviderName!,
            "INTEGER",
            "TEXT");
    }

    /// <summary>Validates the canonical SQLite migration model and creates its fingerprint.</summary>
    /// <returns>The canonical fingerprint length.</returns>
    [Benchmark]
    public int ValidateCanonicalModel() => SafeMigrationRunner.ValidateCanonicalMigrationModelAndCreateFingerprint(
            _context,
            "Microsoft.EntityFrameworkCore.Sqlite")
        .Length;

    /// <summary>Generates SQL for 1000 repair operations.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int GenerateRepairs() => _generator.Generate(_repairOperations, _context.Model)
        .Count;

    /// <summary>Generates SQL for 384 model-managed-data transitions.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int GenerateModelManagedData() => _generator.Generate(_modelManagedOperations, _context.Model)
        .Count;

    /// <summary>Disposes the native provider context after measurement.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources owned by this benchmark fixture.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            // WHY: Cleanup also runs when BenchmarkDotNet setup has not acquired this resource.
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            _context?.Dispose();
        }
    }
}

/// <summary>Measures native SQLite model-managed-data analysis across the retained catalog sizes.</summary>
// WHY: BenchmarkDotNet discovers methods, assigns parameters, and generates derived benchmark hosts.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class SqliteModelManagedDataAnalysisBenchmarks : IDisposable, IAsyncDisposable
{
    private SqliteConnection _connection = null!;
    private SqliteGenerationContext _context = null!;
    private string _instanceId = null!;
    private IReadOnlyList<MigrationOperation> _operations = null!;
    private ISafeMigrationRunner _runner = null!;

    /// <summary>Gets or sets the catalog table count; the 1000-table case includes indexes and foreign keys.</summary>
    [Params(200, 1000)]
    public int TableCount { get; set; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            // WHY: Cleanup also runs when BenchmarkDotNet setup has not acquired this resource.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (_context is not null)
            {
                await _context.DisposeAsync();
            }
        }
        finally
        {
            // WHY: Cleanup also runs when BenchmarkDotNet setup has not acquired this resource.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Releases the owned native context and catalog connection.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates the native in-memory catalog and model-managed-data workload outside the measurement.</summary>
    /// <returns>The asynchronous native catalog preparation.</returns>
    [GlobalSetup]
    public async Task Setup()
    {
        var includeRelationalObjects = TableCount == 1000;
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await _connection.OpenAsync(CancellationToken.None);
        await CreateRuntimeCatalogAsync(_connection, TableCount, includeRelationalObjects, CancellationToken.None);
        _context = new SqliteGenerationContext(_connection);
        _runner = _context.GetService<ISafeMigrationRunner>();
        _operations = ModelManagedDataBenchmarkWorkload.CreateOperations(
            _context.Database.ProviderName!,
            "INTEGER",
            "TEXT");

        _instanceId = includeRelationalObjects
            ? "sqlite-relational-runtime-benchmark"
            : "sqlite-runtime-benchmark";
    }

    /// <summary>Analyzes 384 model-managed-data transitions against the configured native catalog.</summary>
    /// <returns>The completed assessment count.</returns>
    [Benchmark]
    public async Task<int> AnalyzeModelManagedData()
    {
        var report = await _runner
            .AnalyzeAsync(_context, _operations, new SafeMigrationRunOptions(_instanceId), CancellationToken.None)
            .ConfigureAwait(false);

        return report.Assessments.Count;
    }

    /// <summary>Disposes the native context before closing its in-memory catalog connection.</summary>
    /// <returns>The asynchronous, noncancellable fixture cleanup.</returns>
    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    /// <summary>Releases managed resources owned by this benchmark fixture.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            try
            {
                // WHY: Cleanup also runs when BenchmarkDotNet setup has not acquired this resource.
                // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
                _context?.Dispose();
            }
            finally
            {
                // WHY: Cleanup also runs when BenchmarkDotNet setup has not acquired this resource.
                // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
                _connection?.Dispose();
            }
        }
    }

    /// <summary>Creates the exact runtime catalog outside measured invocations.</summary>
    /// <param name="connection">The opened fixture connection.</param>
    /// <param name="tableCount">The complete prepared catalog size.</param>
    /// <param name="includeRelationalObjects">Whether indexes and foreign keys are included.</param>
    /// <param name="cancellationToken">The token controlling fixture DDL.</param>
    /// <returns>The native asynchronous catalog creation.</returns>
    private static async Task CreateRuntimeCatalogAsync(
        SqliteConnection connection,
        int tableCount,
        bool includeRelationalObjects,
        CancellationToken cancellationToken
    )
    {
        // WHY: SqliteConnection's typed synchronous transaction factory is deliberate;
        // the provider does not implement asynchronous transaction I/O for its embedded engine.
        // ReSharper disable once MethodHasAsyncOverload
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE benchmark_model_data ("
            + "id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL);";

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

        for (var index = 1; index < tableCount; index++)
        {
            command.CommandText = includeRelationalObjects
                ? $"""
                CREATE TABLE benchmark_catalog_{index} (id INTEGER NOT
                NULL PRIMARY KEY,
                parent_id INTEGER NULL, FOREIGN KEY (parent_id) REFERENCES benchmark_model_data(id));
                """
                : $"CREATE TABLE benchmark_catalog_{index} (id INTEGER NOT NULL PRIMARY KEY);";

            _ = await command.ExecuteNonQueryAsync(cancellationToken);

            if (includeRelationalObjects)
            {
                command.CommandText = $"""
                    CREATE INDEX ix_benchmark_catalog_{index}_parent
                    ON benchmark_catalog_{index}(parent_id);
                    """;

                _ = await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
