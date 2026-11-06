namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks;

/// <summary>Measures the retained MySQL canonical, repair and model-managed-data workloads.</summary>
public class MySqlMigrationBenchmarks : IDisposable
{
    private MySqlGenerationContext _context = null!;
    private IMigrationsSqlGenerator _generator = null!;
    private IModel _model = null!;
    private IReadOnlyList<MigrationOperation> _modelManagedOperations = null!;
    private MySqlSafeMigrationPlanCapture _planCapture = null!;
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
        _context = new MySqlGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _planCapture = _context.GetService<MySqlSafeMigrationPlanCapture>();
        _model = _context.Model;
        _repairOperations = ColumnBenchmarkWorkload.CreateRepairOperations(1000);
        _modelManagedOperations = ModelManagedDataBenchmarkWorkload.CreateOperations(
            _context.Database.ProviderName!,
            "int",
            "varchar(64)");
    }

    /// <summary>Validates the canonical MySQL migration model and creates its fingerprint.</summary>
    /// <returns>The canonical fingerprint length.</returns>
    [Benchmark]
    public int ValidateCanonicalModel() => SafeMigrationRunner.ValidateCanonicalMigrationModelAndCreateFingerprint(
            _context,
            "Doka.EntityFrameworkCore.MySql")
        .Length;

    /// <summary>Generates SQL for 1000 repair operations.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int GenerateRepairs() => _generator.Generate(_repairOperations, _model)
        .Count;

    /// <summary>Captures the plans for 1000 repair operations.</summary>
    /// <returns>The captured plan count.</returns>
    [Benchmark]
    public int BuildRepairPlans() => MySqlBenchmarkPlanCapture.CapturePlans(
        _generator,
        _planCapture,
        _model,
        _repairOperations);

    /// <summary>Generates SQL for 384 model-managed-data transitions.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int GenerateModelManagedData() => _generator.Generate(_modelManagedOperations, _model)
        .Count;

    /// <summary>Captures the plans for 384 model-managed-data transitions.</summary>
    /// <returns>The captured plan count.</returns>
    [Benchmark]
    public int BuildModelManagedDataPlans() => MySqlBenchmarkPlanCapture.CapturePlans(
        _generator,
        _planCapture,
        _model,
        _modelManagedOperations);

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
            _context.Dispose();
        }
    }
}
