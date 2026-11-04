namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks;

/// <summary>Measures PostgreSQL column SQL generation at the retained operation counts.</summary>
public class PostgreSqlGenerationBenchmarks : IDisposable
{
    private PostgreSqlGenerationContext _context = null!;
    private IMigrationsSqlGenerator _generator = null!;
    private IReadOnlyList<MigrationOperation> _operations = null!;

    /// <summary>Gets or sets the number of column operations generated per invocation.</summary>
    [Params(1, 100, 1000)]
    public int OperationCount { get; set; }

    /// <summary>Creates the native provider services, model and operations outside the measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new PostgreSqlGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _ = _context.Model;
        _operations = ColumnBenchmarkWorkload.CreateOperations(OperationCount);
    }

    /// <summary>Generates SQL for the configured column workload.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int Generate() => _generator.Generate(_operations, _context.Model).Count;

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
