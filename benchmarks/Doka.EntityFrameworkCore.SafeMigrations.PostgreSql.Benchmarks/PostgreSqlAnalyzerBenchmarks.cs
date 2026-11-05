namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks;

/// <summary>Measures PostgreSQL catalog-plan building at the retained analyzer operation counts.</summary>
public class PostgreSqlAnalyzerBenchmarks : IDisposable
{
    private PostgreSqlSafeMigrationCatalogSqlBuilder _catalogBuilder = null!;
    private PostgreSqlGenerationContext _context = null!;
    private IReadOnlyList<MigrationOperation> _operations = null!;

    /// <summary>Gets or sets the number of column operations analyzed per invocation.</summary>
    [Params(1, 512, 1000)]
    public int OperationCount { get; set; }

    /// <summary>Releases the owned benchmark context.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates the native catalog builder and operations outside the measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _context = new PostgreSqlGenerationContext();
        _catalogBuilder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());

        _operations = ColumnBenchmarkWorkload.CreateOperations(OperationCount);
    }

    /// <summary>Builds the catalog plans for the configured column workload.</summary>
    /// <returns>The built plan count.</returns>
    [Benchmark]
    public int BuildPlans() => PostgreSqlBenchmarkPlanBuilder.BuildPlans(_catalogBuilder, _operations);

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

internal static class PostgreSqlBenchmarkPlanBuilder
{
    public static int BuildPlans(
        PostgreSqlSafeMigrationCatalogSqlBuilder catalogBuilder,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var count = 0;
        foreach (var operation in operations.Cast<SafeMigrationOperation>())
        {
            _ = catalogBuilder.Build(operation);
            count++;
        }

        return count;
    }
}
