namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Benchmarks;

/// <summary>Measures SQLite column SQL generation at the retained operation counts.</summary>
public class SqliteGenerationBenchmarks : IDisposable
{
    private SqliteGenerationContext _context = null!;
    private IMigrationsSqlGenerator _generator = null!;
    private IReadOnlyList<MigrationOperation> _operations = null!;

    /// <summary>Gets or sets the number of column operations generated per invocation.</summary>
    [Params(1, 100, 1000)]
    public int OperationCount { get; set; }

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

    /// <summary>Releases the owned benchmark context.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases managed resources owned by this benchmark fixture.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            _context?.Dispose();
        }
    }
}

/// <summary>Measures the retained SQLite canonical, repair and model-managed-data SQL workloads.</summary>
public class SqliteMigrationBenchmarks : IDisposable
{
    private SqliteGenerationContext _context = null!;
    private IMigrationsSqlGenerator _generator = null!;
    private IReadOnlyList<MigrationOperation> _repairOperations = null!;
    private IReadOnlyList<MigrationOperation> _modelManagedOperations = null!;

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
    public int GenerateModelManagedData() => _generator.Generate(_modelManagedOperations, _context.Model).Count;

    /// <summary>Disposes the native provider context after measurement.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases the owned benchmark context.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases managed resources owned by this benchmark fixture.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            _context?.Dispose();
        }
    }
}

/// <summary>Measures native SQLite model-managed-data analysis across the retained catalog sizes.</summary>
public class SqliteModelManagedDataAnalysisBenchmarks : IDisposable
{
    private SqliteConnection _connection = null!;
    private SqliteGenerationContext _context = null!;
    private ISafeMigrationRunner _runner = null!;
    private IReadOnlyList<MigrationOperation> _operations = null!;
    private string _instanceId = null!;

    /// <summary>Gets or sets the catalog table count; the 1000-table case includes indexes and foreign keys.</summary>
    [Params(200, 1000)]
    public int TableCount { get; set; }

    /// <summary>Creates the native in-memory catalog and model-managed-data workload outside the measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var includeRelationalObjects = TableCount == 1000;
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        _connection.Open();
        CreateRuntimeCatalog(_connection, TableCount, includeRelationalObjects);
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
    public async System.Threading.Tasks.Task<int> AnalyzeModelManagedData()
    {
        var report = await _runner
            .AnalyzeAsync(_context, _operations, new SafeMigrationRunOptions(_instanceId))
            .ConfigureAwait(false);

        return report.Assessments.Count;
    }

    /// <summary>Disposes the native context before closing its in-memory catalog connection.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases the owned native context and catalog connection.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

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
                _context?.Dispose();
            }
            finally
            {
                _connection?.Dispose();
            }
        }
    }

    private static void CreateRuntimeCatalog(
        SqliteConnection connection,
        int tableCount,
        bool includeRelationalObjects
    )
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE benchmark_model_data ("
            + "id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL);";

        _ = command.ExecuteNonQuery();

        for (var index = 1; index < tableCount; index++)
        {
            command.CommandText = includeRelationalObjects
                ? $"CREATE TABLE benchmark_catalog_{index} ("
                + "id INTEGER NOT NULL PRIMARY KEY, parent_id INTEGER NULL, "
                + "FOREIGN KEY (parent_id) REFERENCES benchmark_model_data(id));"
                : $"CREATE TABLE benchmark_catalog_{index} (id INTEGER NOT NULL PRIMARY KEY);";
            _ = command.ExecuteNonQuery();

            if (includeRelationalObjects)
            {
                command.CommandText = $"CREATE INDEX ix_benchmark_catalog_{index}_parent "
                    + $"ON benchmark_catalog_{index}(parent_id);";
                _ = command.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }
}
