namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Infrastructure;

/// <summary>
/// Separates informational budget verdicts from fatal benchmark failures.
/// </summary>
public sealed class BenchmarkRunnerTests
{
    /// <summary>
    /// Preserves failed duration and allocation verdicts without rejecting report-only runs.
    /// </summary>
    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    public void CompletePreservesExceededBudgetEvidence(
        bool reportOnly,
        bool exceedAllocations,
        int expectedExitCode
    )
    {
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(
            reportOnly,
            exceedAllocations ? double.MaxValue : 0,
            exceedAllocations ? 0 : long.MaxValue);

        // Force the selected overrun without a hardware-dependent performance expectation.
        runner.Measure("synthetic", exceedAllocations ? static () => AllocateBuffer(4096) : WaitForTimer);

        var exitCode = runner.Complete();

        using var report = JsonDocument.Parse(File.ReadAllBytes(fixture.OutputPath));
        var result = Assert.Single(report.RootElement.GetProperty("results").EnumerateArray());

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(result.GetProperty("passed").GetBoolean());
        Assert.Equal("synthetic", result.GetProperty("name").GetString());
        Assert.Equal(exceedAllocations ? double.MaxValue : 0,
            result.GetProperty("maximumDurationMilliseconds").GetDouble());
        Assert.Equal(exceedAllocations ? 0 : long.MaxValue,
            result.GetProperty("maximumAllocatedBytes").GetInt64());
        Assert.True(exceedAllocations
            ? result.GetProperty("allocatedBytes").GetInt64() > 0
            : result.GetProperty("durationMilliseconds").GetDouble() > 0);
    }

    /// <summary>
    /// Accepts complete in-budget reports in strict and informational modes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteAcceptsMeasurementsWithinBudgets(
        bool reportOnly
    )
    {
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(reportOnly);
        runner.Measure("synthetic", static () => 0);

        var exitCode = runner.Complete();

        using var report = JsonDocument.Parse(File.ReadAllBytes(fixture.OutputPath));

        Assert.Equal(0, exitCode);
        Assert.True(Assert.Single(report.RootElement.GetProperty("results").EnumerateArray())
            .GetProperty("passed").GetBoolean());
    }

    /// <summary>
    /// Keeps missing workload evidence fatal even in informational mode.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteRejectsBudgetsWithoutMeasurements(
        bool reportOnly
    )
    {
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(reportOnly);

        var exception = Assert.Throws<InvalidOperationException>(() => runner.Complete());

        Assert.Contains("Performance budgets were not measured", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    /// <summary>
    /// Keeps unknown workloads fatal in both modes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasureRejectsUnknownBenchmarkNames(
        bool reportOnly
    )
    {
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(reportOnly);

        var exception = Assert.Throws<InvalidOperationException>(() => runner.Measure("unknown", static () => 0));

        Assert.Contains("No performance budget exists", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    /// <summary>
    /// Keeps duplicate measurements fatal in both modes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeasureRejectsDuplicateBenchmarkNames(
        bool reportOnly
    )
    {
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(reportOnly);
        runner.Measure("synthetic", static () => 0);

        var exception = Assert.Throws<InvalidOperationException>(() => runner.Measure("synthetic", static () => 0));

        Assert.Contains("measured more than once", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    /// <summary>
    /// Propagates action failures instead of turning them into informational results.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MeasurePropagatesExecutionFailures(
        bool reportOnly,
        bool failDuringMeasurement
    )
    {
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(reportOnly);
        var expected = new InvalidOperationException("Synthetic workload failure.");
        var invocationCount = 0;

        var exception = Assert.Throws<InvalidOperationException>(() => runner.Measure("synthetic", () =>
        {
            invocationCount++;

            if (!failDuringMeasurement || invocationCount > 1)
            {
                throw expected;
            }

            return 0;
        }));

        Assert.Same(expected, exception);
        Assert.Equal(failDuringMeasurement ? 2 : 1, invocationCount);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    /// <summary>
    /// Requires successfully written evidence even when numerical overruns are informational.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletePropagatesOutputFailures(
        bool reportOnly
    )
    {
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(reportOnly);
        Directory.CreateDirectory(fixture.OutputPath);
        runner.Measure("synthetic", static () => 0);

        var exception = Record.Exception(() => runner.Complete());

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.True(Directory.Exists(fixture.OutputPath));
    }

    /// <summary>
    /// Honors the parsed policy in both option orders while retaining strict default evaluation.
    /// </summary>
    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 1)]
    public void CompleteHonorsCommandLineBudgetPolicy(
        bool reportOnly,
        bool flagFirst,
        int expectedExitCode
    )
    {
        using var fixture = new BenchmarkFixture();
        var arguments = !reportOnly
            ? new[] { "--output", fixture.OutputPath }
            : flagFirst
                ? ["--report-only", "--output", fixture.OutputPath]
                : ["--output", fixture.OutputPath, "--report-only"];

        var runner = BenchmarkRunner.Create(arguments, "unused.json", "core");
        BenchmarkFixture.MeasureCoreWorkloads(runner);

        var exitCode = runner.Complete();

        using var report = JsonDocument.Parse(File.ReadAllBytes(fixture.OutputPath));
        var firstResult = report.RootElement.GetProperty("results")[0];

        Assert.Equal(expectedExitCode, exitCode);
        Assert.False(firstResult.GetProperty("passed").GetBoolean());
        Assert.True(firstResult.GetProperty("allocatedBytes").GetInt64()
            > firstResult.GetProperty("maximumAllocatedBytes").GetInt64());
    }

    /// <summary>
    /// Accepts strict or report-only defaults without creating an output before measurement.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true, "--report-only")]
    public void CreateAcceptsDefaultOutputOptions(
        bool expectedReportOnly,
        params string[] arguments
    )
    {
        var runner = BenchmarkRunner.Create(arguments, "unused.json", "core");

        Assert.Equal(expectedReportOnly, runner.ReportOnly);
    }

    /// <summary>
    /// Rejects unknown, duplicated, incomplete and ambiguous command-line options.
    /// </summary>
    [Theory]
    [InlineData("--unknown")]
    [InlineData("--output")]
    [InlineData("--output", "")]
    [InlineData("--output", " ")]
    [InlineData("--output", "--report-only")]
    [InlineData("--report-only", "--output")]
    [InlineData("--report-only", "--report-only")]
    [InlineData("--report-only", "--output", "--report-only")]
    [InlineData("--output", "first.json", "--output", "second.json")]
    public void CreateRejectsMalformedArguments(
        params string[] arguments
    )
    {
        var exception = Assert.Throws<ArgumentException>(
            () => BenchmarkRunner.Create(arguments, "unused.json", "core"));

        Assert.NotEmpty(exception.Message);
    }

    /// <summary>
    /// Retains budget-set validation in report-only mode.
    /// </summary>
    [Fact]
    public void CreateRejectsUnknownBudgetSetsInReportOnlyMode()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => BenchmarkRunner.Create(["--report-only"], "unused.json", "unknown"));

        Assert.Contains("Unknown benchmark set", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects negative, nonfinite and overflowing limits before budget evaluation.
    /// </summary>
    [Theory]
    [InlineData("-1", "0", "1")]
    [InlineData("1", "-1", "1")]
    [InlineData("1", "0", "-1")]
    [InlineData("1e309", "0", "1")]
    [InlineData("1", "1e309", "1")]
    [InlineData("1e308", "200", "1")]
    public void ReadBudgetsRejectsMalformedComparisonLimits(
        string baseline,
        string tolerance,
        string allocations
    )
    {
        using var fixture = new BenchmarkFixture();
        fixture.WriteBudgetConfiguration(baseline, tolerance, allocations);

        var exception = Assert.Throws<InvalidOperationException>(
            () => BenchmarkRunner.ReadBudgets(fixture.OutputPath, "synthetic"));

        Assert.Contains("finite, nonnegative comparison limits", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Retains complete-document validation for missing, duplicate and orphaned budgets.
    /// </summary>
    [Theory]
    [InlineData(1, "[\"synthetic\"]", "Unsupported performance-budget schema version")]
    [InlineData(2, "[\"missing\"]", "references missing budget")]
    [InlineData(2, "[\"synthetic\",\"synthetic\"]", "belongs to more than one benchmark set")]
    [InlineData(2, "[]", "do not belong to a benchmark set")]
    public void ReadBudgetsRejectsMalformedWorkloadSets(
        int schemaVersion,
        string workloadNames,
        string expectedMessage
    )
    {
        using var fixture = new BenchmarkFixture();
        fixture.WriteBudgetConfiguration("1", "0", "1", schemaVersion, workloadNames);

        var exception = Assert.Throws<InvalidOperationException>(
            () => BenchmarkRunner.ReadBudgets(fixture.OutputPath, "synthetic"));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    private static int WaitForTimer()
    {
        System.Threading.Thread.Sleep(1);

        return 0;
    }

    private static int AllocateBuffer(
        int size
    )
    {
        var buffer = new byte[size];

        // Keep the forced allocation observable even if the JIT can fold an array's length.
        GC.KeepAlive(buffer);

        return buffer.Length;
    }

    private sealed class BenchmarkFixture : IDisposable
    {
        /// <summary>
        /// Gets the isolated file owned exclusively by this fixture.
        /// </summary>
        public string OutputPath { get; } = Path.Combine(Path.GetTempPath(), $"safe-migrations-{Guid.NewGuid():N}.json");

        /// <summary>
        /// Creates a single-workload runner with controlled comparison limits.
        /// </summary>
        public BenchmarkRunner CreateRunner(
            bool reportOnly,
            double durationLimit = double.MaxValue,
            long allocationLimit = long.MaxValue
        )
            => new(
                new Dictionary<string, BenchmarkRunner.BenchmarkBudget>(StringComparer.Ordinal)
                {
                    ["synthetic"] = new(durationLimit, 0, allocationLimit)
                },
                OutputPath,
                reportOnly);

        /// <summary>
        /// Writes an isolated synthetic document without changing the repository's budgets.
        /// </summary>
        public void WriteBudgetConfiguration(
            string baseline,
            string tolerance,
            string allocations,
            int schemaVersion = 2,
            string workloadNames = "[\"synthetic\"]"
        )
            => File.WriteAllText(OutputPath, $$"""
                {
                  "schemaVersion": {{schemaVersion}},
                  "benchmarkSets": { "synthetic": {{workloadNames}} },
                  "budgets": {
                    "synthetic": {
                      "baselineDurationMilliseconds": {{baseline}},
                      "regressionTolerancePercent": {{tolerance}},
                      "maximumAllocatedBytes": {{allocations}}
                    }
                  }
                }
                """);

        /// <summary>
        /// Completes the real selected budget set with one deterministic allocation overrun.
        /// </summary>
        public static void MeasureCoreWorkloads(
            BenchmarkRunner runner
        )
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                directory = directory.Parent;
            }

            var repositoryRoot = directory?.FullName
                ?? throw new DirectoryNotFoundException("Unable to locate benchmark configuration.");

            using var budgets = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot,
                "eng", "performance-budgets.json")));

            foreach (var nameElement in budgets.RootElement.GetProperty("benchmarkSets").GetProperty("core")
                         .EnumerateArray())
            {
                var name = nameElement.GetString()!;

                if (name == "core_intent_construction_1")
                {
                    var limit = budgets.RootElement.GetProperty("budgets").GetProperty(name)
                        .GetProperty("maximumAllocatedBytes").GetInt64();

                    runner.Measure(name, () => AllocateBuffer(checked((int)limit + 4096)));
                }
                else
                {
                    runner.Measure(name, static () => 0);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (File.Exists(OutputPath))
            {
                File.Delete(OutputPath);
            }

            if (Directory.Exists(OutputPath))
            {
                Directory.Delete(OutputPath);
            }
        }
    }
}
