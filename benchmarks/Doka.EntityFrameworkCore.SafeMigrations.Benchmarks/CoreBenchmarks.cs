namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Measures column intent construction, planning and report serialization.</summary>
public class CoreColumnBenchmarks
{
    private SafeMigrationRunReport _report = null!;

    /// <summary>Gets or sets the number of operations or assessments processed per invocation.</summary>
    [Params(1, 100, 1000)]
    public int OperationCount { get; set; }

    /// <summary>Creates the immutable report outside the serialization measurement.</summary>
    [GlobalSetup]
    public void Setup() => _report = ColumnBenchmarkWorkload.CreateReport(OperationCount);

    /// <summary>Constructs the configured number of safe column intents.</summary>
    /// <returns>The constructed operation count.</returns>
    [Benchmark]
    public int ConstructIntents() => ColumnBenchmarkWorkload.CreateOperations(OperationCount)
        .Count;

    /// <summary>Plans the configured number of column decisions.</summary>
    /// <returns>The decision action checksum.</returns>
    [Benchmark]
    public int PlanDecisions() => ColumnBenchmarkWorkload.RunPlanner(OperationCount);

    /// <summary>Serializes the configured report to UTF-8 JSON.</summary>
    /// <returns>The serialized byte count.</returns>
    [Benchmark]
    public int SerializeReport() => SafeMigrationReportJson.SerializeToUtf8Bytes(_report)
        .Length;
}

/// <summary>Measures the blocking-only view of the retained 50000-assessment report.</summary>
public class CoreReportViewBenchmarks
{
    private SafeMigrationRunReport _report = null!;

    /// <summary>Creates the report with eight blockers outside the measurement.</summary>
    [GlobalSetup]
    public void Setup() => _report = ColumnBenchmarkWorkload.CreateBlockingReportViewWorkload(
        count: 50_000,
        blockerCount: 8);

    /// <summary>Serializes only the blocking assessments from the prepared report.</summary>
    /// <returns>The serialized byte count.</returns>
    [Benchmark]
    public int SerializeBlockingReport() => SafeMigrationReportJson.SerializeToUtf8Bytes(
            _report,
            SafeMigrationReportSelection.BlockingOnly)
        .Length;
}

/// <summary>Measures construction and fingerprinting of 384 model-managed-data transitions.</summary>
public class CoreModelManagedDataBenchmarks
{
    private readonly string _providerName = "benchmark";
    private IReadOnlyList<MigrationOperation> _operations = null!;

    /// <summary>Creates the model-managed-data operations outside the fingerprint measurement.</summary>
    [GlobalSetup]
    public void Setup() => _operations = ModelManagedDataBenchmarkWorkload.CreateOperations(
        _providerName,
        "integer",
        "varchar(64)");

    /// <summary>Constructs the retained model-managed-data transition intents.</summary>
    /// <returns>The constructed operation count.</returns>
    [Benchmark]
    public int ConstructModelManagedDataIntents() => ModelManagedDataBenchmarkWorkload.CreateOperations(
            _providerName,
            "integer",
            "varchar(64)")
        .Count;

    /// <summary>Creates the contract fingerprint for the retained model-managed-data transitions.</summary>
    /// <returns>The fingerprint length.</returns>
    [Benchmark]
    public int CreateModelManagedDataFingerprint() => SafeMigrationContractFingerprint.Create(_operations)
        .Length;
}
