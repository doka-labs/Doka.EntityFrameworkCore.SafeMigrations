namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks;

/// <summary>Measures the retained PostgreSQL canonical, repair and model-managed-data workloads.</summary>
public class PostgreSqlMigrationBenchmarks : IDisposable
{
    private PostgreSqlGenerationContext _context = null!;
    private IMigrationsSqlGenerator _generator = null!;
    private PostgreSqlSafeMigrationCatalogSqlBuilder _catalogBuilder = null!;
    private IReadOnlyList<MigrationOperation> _repairOperations = null!;
    private IReadOnlyList<MigrationOperation> _modelManagedOperations = null!;

    /// <summary>Creates the native provider services, model and fixed workloads outside the measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new PostgreSqlGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _catalogBuilder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());

        _ = _context.Model;
        _repairOperations = ColumnBenchmarkWorkload.CreateRepairOperations(1000);
        _modelManagedOperations = ModelManagedDataBenchmarkWorkload.CreateOperations(
            _context.Database.ProviderName!,
            "integer",
            "character varying(64)");
    }

    /// <summary>Validates the canonical PostgreSQL migration model and creates its fingerprint.</summary>
    /// <returns>The canonical fingerprint length.</returns>
    [Benchmark]
    public int ValidateCanonicalModel() => SafeMigrationRunner.ValidateCanonicalMigrationModelAndCreateFingerprint(
            _context,
            "Npgsql.EntityFrameworkCore.PostgreSQL")
        .Length;

    /// <summary>Generates SQL for 1000 repair operations.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int GenerateRepairs() => _generator.Generate(_repairOperations, _context.Model)
        .Count;

    /// <summary>Builds the catalog plans for 1000 repair operations.</summary>
    /// <returns>The built plan count.</returns>
    [Benchmark]
    public int BuildRepairPlans() => PostgreSqlBenchmarkPlanBuilder.BuildPlans(_catalogBuilder, _repairOperations);

    /// <summary>Generates SQL for 384 model-managed-data transitions.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int GenerateModelManagedData() => _generator.Generate(_modelManagedOperations, _context.Model)
        .Count;

    /// <summary>Builds the catalog plans for 384 model-managed-data transitions.</summary>
    /// <returns>The built plan count.</returns>
    [Benchmark]
    public int BuildModelManagedDataPlans() => PostgreSqlBenchmarkPlanBuilder.BuildPlans(
        _catalogBuilder,
        _modelManagedOperations);

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
            _context.Dispose();
        }
    }
}
