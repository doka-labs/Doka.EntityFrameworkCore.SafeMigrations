namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Creates native benchmark evidence without building or running benchmark children.</summary>
internal sealed class BenchmarkProgramTestEvidence : IDisposable
{
    /// <summary>Creates an invocation-local directory for native and synthetic exports.</summary>
    internal BenchmarkProgramTestEvidence()
    {
        DirectoryPath = Directory.CreateTempSubdirectory("doka-benchmark-program-tests-")
            .FullName;
    }

    /// <summary>Gets the isolated native exporter destination.</summary>
    internal string DirectoryPath { get; }

    /// <summary>Gets a report path that does not exist until the test writes its evidence.</summary>
    internal string ReportPath => Path.Combine(DirectoryPath, "report-full.json");

    /// <summary>Removes only this instance's isolated evidence directory.</summary>
    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);

    /// <summary>Discovers two native parameterized cases without executing their workload.</summary>
    /// <returns>The distinct cases used to verify complete report aggregation.</returns>
    internal static BenchmarkCase[] CreateCases() => BenchmarkConverter
        .TypeToBenchmarks(typeof(BenchmarkProgramOutcomeProbe), DefaultConfig.Instance)
        .BenchmarksCases;

    /// <summary>Creates a child result by parsing the same measurement lines as native execution.</summary>
    /// <param name="nanoseconds">The measured workload duration, not a pass/fail budget.</param>
    /// <param name="allocatedBytes">The measured allocation count, not a pass/fail budget.</param>
    /// <param name="exitCode">The child exit status, including an unavailable status.</param>
    /// <param name="stage">The native measurement stage.</param>
    /// <returns>A native child result with independent execution and measurement evidence.</returns>
    internal static ExecuteResult CreateExecution(
        double nanoseconds = 100,
        long allocatedBytes = 64,
        int? exitCode = 0,
        IterationStage stage = IterationStage.Result
    )
    {
        var measurement = new Measurement(1, IterationMode.Workload, stage, 1, 1, nanoseconds);
        var garbageCollection = FormattableString.Invariant($"// GC: 0 0 0 {allocatedBytes} 1");

        return new ExecuteResult(
            true,
            exitCode,
            1,
            [measurement.ToString()],
            [garbageCollection],
            [],
            1);
    }

    /// <summary>Creates a native report with its success flag independent of its child evidence.</summary>
    /// <param name="benchmark">The selected native benchmark case.</param>
    /// <param name="success">The native report success state.</param>
    /// <param name="executions">The child launches retained by the report.</param>
    /// <returns>A native report for the selected case.</returns>
    internal static BenchmarkReport CreateReport(
        BenchmarkCase benchmark,
        bool success,
        params ExecuteResult[] executions
    )
    {
        var generation = GenerateResult.Success(ArtifactsPaths.Empty, []);
        var build = BuildResult.Success(generation);

        return new BenchmarkReport(success, benchmark, generation, build, executions, []);
    }

    /// <summary>Creates a native summary over the exact reports and validation messages supplied.</summary>
    /// <param name="reports">The reports retained by the native summary.</param>
    /// <param name="validationErrors">Critical errors or nonfatal validation warnings.</param>
    /// <returns>A complete native summary that does not require benchmark execution.</returns>
    internal Summary CreateSummary(
        IReadOnlyList<BenchmarkReport> reports,
        params ValidationError[] validationErrors
    ) => new(
        "outcome",
        reports.ToImmutableArray(),
        HostEnvironmentInfo.GetCurrent(),
        DirectoryPath,
        Path.Combine(DirectoryPath, "outcome.log"),
        TimeSpan.Zero,
        CultureInfo.InvariantCulture,
        validationErrors.ToImmutableArray(),
        ImmutableArray<IColumnHidingRule>.Empty);

    /// <summary>Writes only the current test's JSON evidence.</summary>
    /// <param name="json">The independent export content to validate.</param>
    internal void WriteExport(
        string json
    ) => File.WriteAllText(ReportPath, json);
}

/// <summary>Supplies native discovery metadata; contract tests never execute this workload.</summary>
public class BenchmarkProgramOutcomeProbe
{
    /// <summary>Gets or sets the native parameter that makes case identities distinct.</summary>
    [Params(1, 2)]
    public int OperationCount { get; set; }

    /// <summary>Provides a discoverable native benchmark method without external state.</summary>
    /// <returns>The selected case parameter.</returns>
    [Benchmark]
    public int Measure() => OperationCount;
}
