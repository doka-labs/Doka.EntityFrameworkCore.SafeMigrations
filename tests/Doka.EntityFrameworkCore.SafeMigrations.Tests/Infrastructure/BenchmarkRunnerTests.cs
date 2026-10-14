namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Infrastructure;

/// <summary>
/// Separates informational budget verdicts from fatal benchmark failures.
/// </summary>
public sealed class BenchmarkRunnerTests
{

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

}
