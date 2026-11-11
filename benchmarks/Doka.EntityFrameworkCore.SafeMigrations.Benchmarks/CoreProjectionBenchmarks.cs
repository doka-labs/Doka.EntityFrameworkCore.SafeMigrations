namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Measures prepared accepted-column projection without a functional-test allocation budget.</summary>
public class CoreProjectionBenchmarks
{
    // WHY: This fixture input keeps the constructor benchmark instance-bound, as required by BenchmarkDotNet.
    // ReSharper disable once ConvertToConstant.Local
    private readonly string _analysisCode = "projected_matching";
    private PreparedColumnProjectionWorkload _workload = null!;

    /// <summary>Gets or sets the number of prepared columns in one measured invocation.</summary>

    // WHY: Retain workload cardinality in exported benchmark metadata even with one current parameter value.
    [Params(1_000)]
    public int OperationCount { get; set; }

    /// <summary>Prepares and validates the exact functional workload outside the measured method.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _workload = new PreparedColumnProjectionWorkload(OperationCount);
        VerifyResult();
    }

    /// <summary>Projects a fresh accepted-column stream with native BenchmarkDotNet allocation diagnostics.</summary>
    /// <returns>The matching-column count, not an allocation or timing verdict.</returns>
    [Benchmark]
    public int ProjectAcceptedColumns() => _workload.ProjectAcceptedColumns();

    /// <summary>Expires weak runtime caches outside the measured constructor invocation.</summary>
    [IterationSetup(Target = nameof(CreateAnalysisAfterCollection))]
    public void CollectBeforeAnalysis()
    {
        // WHY: A warm-only measurement cannot expose constructor metadata reinitialization after GC.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>Measures a provider-analysis result independently of surviving enum metadata.</summary>
    /// <returns>The newly owned analysis; allocation values remain informational.</returns>
    [Benchmark]
    public SafeMigrationProviderAnalysis CreateAnalysisAfterCollection() => new(
        SafeMigrationObservedState.Matching,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: true,
        _analysisCode);

    /// <summary>Rejects incorrect workload output, never an expensive or slow valid result.</summary>
    [GlobalCleanup]
    public void VerifyResult()
    {
        if (_workload.ProjectAcceptedColumns() != OperationCount)
        {
            throw new InvalidOperationException("Prepared projection did not preserve every accepted column.");
        }
    }
}
