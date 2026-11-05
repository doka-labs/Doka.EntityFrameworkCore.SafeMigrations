namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Measures full SQL Server generation against preconstructed column operations.</summary>
[MemoryDiagnoser]
public class SqlServerGenerationBenchmarks : IDisposable
{
    private SqlServerGenerationContext? _context;
    private IMigrationsSqlGenerator? _generator;
    private IReadOnlyList<MigrationOperation> _operations = [];

    /// <summary>Gets or sets the number of guarded column operations.</summary>
    [Params(1, 100, 1000)]
    public int OperationCount { get; set; }

    private SqlServerGenerationContext Context =>
        _context ?? throw new InvalidOperationException("Setup has not completed.");

    /// <summary>Releases the context owned by this benchmark instance.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Builds the provider services and operations without including them in the measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new SqlServerGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _ = _context.Model;
        _operations = ColumnBenchmarkWorkload.CreateOperations(OperationCount);
    }

    /// <summary>Renders the complete guarded command collection.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int Generate() => (_generator ?? throw new InvalidOperationException("Setup has not completed."))
        .Generate(_operations, Context.Model)
        .Count;

    /// <summary>Disposes the context owned by this benchmark case.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources when the benchmark owner is disposed.</summary>
    /// <param name="disposing">Whether managed resources are eligible for disposal.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            _context?.Dispose();
            _context = null;
        }
    }
}

/// <summary>Measures catalog-plan construction independently of SQL rendering.</summary>
[MemoryDiagnoser]
public class SqlServerAnalyzerBenchmarks : IDisposable
{
    private SqlServerSafeMigrationCatalogSqlBuilder? _catalogBuilder;
    private SqlServerGenerationContext? _context;
    private IReadOnlyList<MigrationOperation> _operations = [];

    /// <summary>Gets or sets the analyzer operation cardinality.</summary>
    [Params(1, 512, 1000)]
    public int OperationCount { get; set; }

    /// <summary>Releases the context owned by this benchmark instance.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Prepares services and immutable operations before timing begins.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new SqlServerGenerationContext();
        _catalogBuilder = SqlServerBenchmarkPlans.CreateBuilder(_context);
        _ = _context.Model;
        _operations = ColumnBenchmarkWorkload.CreateOperations(OperationCount);
    }

    /// <summary>Constructs one catalog plan for each guarded operation.</summary>
    /// <returns>The number of catalog plans constructed.</returns>
    [Benchmark]
    public int BuildPlans() => SqlServerBenchmarkPlans.Build(
        _catalogBuilder ?? throw new InvalidOperationException("Setup has not completed."),
        _operations);

    /// <summary>Disposes the provider context for this parameter set.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources when the benchmark owner is disposed.</summary>
    /// <param name="disposing">Whether managed resources are eligible for disposal.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            _context?.Dispose();
            _context = null;
        }
    }
}

/// <summary>Measures generation and catalog construction for 1000 supported safe column repairs.</summary>
[MemoryDiagnoser]
public class SqlServerRepairBenchmarks : IDisposable
{
    private SqlServerSafeMigrationCatalogSqlBuilder? _catalogBuilder;
    private SqlServerGenerationContext? _context;
    private IMigrationsSqlGenerator? _generator;
    private IReadOnlyList<MigrationOperation> _operations = [];

    private SqlServerGenerationContext Context =>
        _context ?? throw new InvalidOperationException("Setup has not completed.");

    /// <summary>Releases the context owned by this benchmark instance.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Preconstructs repairs and requires a real safe repair plan for every operation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new SqlServerGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _catalogBuilder = SqlServerBenchmarkPlans.CreateBuilder(_context);
        _ = _context.Model;
        _operations = SqlServerColumnBenchmarkWorkload.CreateRepairOperations(1000);

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var operation in _operations.Cast<SafeMigrationOperation>())
        {
            var plan = _catalogBuilder.Build(operation);
            if (operation is not { Policy: SafeMigrationPolicy.RepairIfSafe, Intent: AlterColumnIntent }
                || plan.IsStaticallyUnsupported
                || plan.RepairCapability != SafeMigrationRepairCapability.Safe)
            {
                throw new InvalidOperationException(
                    "The SQL Server repair benchmark requires supported column repair plans.");
            }
        }
    }

    /// <summary>Renders the complete guarded repair SQL.</summary>
    /// <returns>The number of generated migration commands.</returns>
    [Benchmark]
    public int Generate() => (_generator ?? throw new InvalidOperationException("Setup has not completed."))
        .Generate(_operations, Context.Model)
        .Count;

    /// <summary>Builds the catalog plans for the prevalidated repair operations.</summary>
    /// <returns>The number of repair plans constructed.</returns>
    [Benchmark]
    public int BuildPlans() => SqlServerBenchmarkPlans.Build(
        _catalogBuilder ?? throw new InvalidOperationException("Setup has not completed."),
        _operations);

    /// <summary>Disposes this case's context.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources when the benchmark owner is disposed.</summary>
    /// <param name="disposing">Whether managed resources are eligible for disposal.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            _context?.Dispose();
            _context = null;
        }
    }
}

/// <summary>Measures the 384-operation model-managed data workload without fixture construction.</summary>
[MemoryDiagnoser]
public class SqlServerModelManagedDataBenchmarks : IDisposable
{
    private SqlServerSafeMigrationCatalogSqlBuilder? _catalogBuilder;
    private SqlServerGenerationContext? _context;
    private IMigrationsSqlGenerator? _generator;
    private IReadOnlyList<MigrationOperation> _operations = [];

    private SqlServerGenerationContext Context =>
        _context ?? throw new InvalidOperationException("Setup has not completed.");

    /// <summary>Releases the context owned by this benchmark instance.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates provider-specific typed data operations before measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new SqlServerGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _catalogBuilder = SqlServerBenchmarkPlans.CreateBuilder(_context);
        _ = _context.Model;
        _operations = ModelManagedDataBenchmarkWorkload.CreateOperations(
            _context.Database.ProviderName!,
            "int",
            "nvarchar(64)");
    }

    /// <summary>Renders the complete guarded model-managed data command collection.</summary>
    /// <returns>The generated migration command count.</returns>
    [Benchmark]
    public int Generate() => (_generator ?? throw new InvalidOperationException("Setup has not completed."))
        .Generate(_operations, Context.Model)
        .Count;

    /// <summary>Constructs catalog plans for all model-managed data operations.</summary>
    /// <returns>The constructed plan count.</returns>
    [Benchmark]
    public int BuildPlans() => SqlServerBenchmarkPlans.Build(
        _catalogBuilder ?? throw new InvalidOperationException("Setup has not completed."),
        _operations);

    /// <summary>Disposes the provider context owned by this case.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources when the benchmark owner is disposed.</summary>
    /// <param name="disposing">Whether managed resources are eligible for disposal.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            _context?.Dispose();
            _context = null;
        }
    }
}

/// <summary>Measures canonical model validation and fingerprint creation.</summary>
[MemoryDiagnoser]
public class SqlServerCanonicalModelBenchmarks : IDisposable
{
    private SqlServerGenerationContext? _context;

    /// <summary>Releases the context owned by this benchmark instance.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates the provider context before timing model validation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new SqlServerGenerationContext();
        _ = _context.Model;
    }

    /// <summary>Validates the snapshot contract and creates its canonical fingerprint.</summary>
    /// <returns>The fingerprint length.</returns>
    [Benchmark]
    public int ValidateCanonicalModel() => SafeMigrationRunner.ValidateCanonicalMigrationModelAndCreateFingerprint(
            _context ?? throw new InvalidOperationException("Setup has not completed."),
            "Microsoft.EntityFrameworkCore.SqlServer")
        .Length;

    /// <summary>Disposes the context owned by this case.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources when the benchmark owner is disposed.</summary>
    /// <param name="disposing">Whether managed resources are eligible for disposal.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            _context?.Dispose();
            _context = null;
        }
    }
}

/// <summary>Shares fixture construction, not measurement, between the bounded SQL Server cases.</summary>
internal static class SqlServerBenchmarkPlans
{
    /// <summary>Resolves the SQL Server catalog-builder dependencies from one benchmark context.</summary>
    /// <param name="context">The context owning the provider services.</param>
    /// <returns>The provider-specific catalog plan builder.</returns>
    internal static SqlServerSafeMigrationCatalogSqlBuilder CreateBuilder(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Builds exactly one plan per guarded migration operation.</summary>
    /// <param name="catalogBuilder">The preconstructed provider builder.</param>
    /// <param name="operations">The immutable preconstructed operations.</param>
    /// <returns>The number of constructed plans.</returns>
    internal static int Build(
        SqlServerSafeMigrationCatalogSqlBuilder catalogBuilder,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        foreach (var operation in operations.Cast<SafeMigrationOperation>())
        {
            _ = catalogBuilder.Build(operation);
        }

        return operations.Count;
    }
}

/// <summary>Configures generation-only services without connecting to a database.</summary>
internal sealed class SqlServerGenerationContext : DbContext
{
    /// <inheritdoc />
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlServer(
            "Server=127.0.0.1,1;Database=benchmark;User ID=sa;Password=BenchmarkOnly123!;TrustServerCertificate=True");

        optionsBuilder.UseSqlServerSafeMigrations<SqlServerGenerationContext>();
    }
}

/// <summary>Provides the empty canonical model snapshot required by the validation benchmark.</summary>
[DbContext(typeof(SqlServerGenerationContext))]
internal sealed class SqlServerGenerationContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => ArgumentNullException.ThrowIfNull(modelBuilder);
}
