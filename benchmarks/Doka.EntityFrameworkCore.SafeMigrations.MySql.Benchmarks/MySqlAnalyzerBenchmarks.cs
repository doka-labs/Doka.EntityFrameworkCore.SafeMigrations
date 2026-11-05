namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks;

/// <summary>Measures MySQL plan capture at the retained analyzer operation counts.</summary>
public class MySqlAnalyzerBenchmarks : IDisposable
{
    private MySqlGenerationContext _context = null!;
    private IMigrationsSqlGenerator _generator = null!;
    private IModel _model = null!;
    private IReadOnlyList<MigrationOperation> _operations = null!;
    private MySqlSafeMigrationPlanCapture _planCapture = null!;

    /// <summary>Gets or sets the number of column operations analyzed per invocation.</summary>
    [Params(1, 512, 1000)]
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
        _context = new MySqlGenerationContext();
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _planCapture = _context.GetService<MySqlSafeMigrationPlanCapture>();
        _model = _context.Model;
        _operations = ColumnBenchmarkWorkload.CreateOperations(OperationCount);
    }

    /// <summary>Captures the generated plans for the configured column workload.</summary>
    /// <returns>The captured plan count.</returns>
    [Benchmark]
    public int BuildPlans() => MySqlBenchmarkPlanCapture.CapturePlans(_generator, _planCapture, _model, _operations);

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

internal static class MySqlBenchmarkPlanCapture
{
    public static int CapturePlans(
        IMigrationsSqlGenerator generator,
        MySqlSafeMigrationPlanCapture planCapture,
        IModel model,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var safeOperations = operations
            .Cast<SafeMigrationOperation>()
            .ToArray();

        using var lease = planCapture.Begin(safeOperations);
        _ = generator.Generate(operations, model);

        return lease.Complete()
            .Length;
    }
}
