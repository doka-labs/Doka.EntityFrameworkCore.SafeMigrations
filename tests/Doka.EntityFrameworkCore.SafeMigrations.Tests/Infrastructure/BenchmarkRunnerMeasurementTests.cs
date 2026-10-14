namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Infrastructure;

/// <summary>Verifies benchmark measurements without interrupting other warmed allocation contracts.</summary>
// WHY: BenchmarkRunner.Measure forces full process collections after its workload warmup.
[Collection(ProcessWideTestGroup.Name)]
public sealed class BenchmarkRunnerMeasurementTests
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
        // Arrange
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(
            reportOnly,
            exceedAllocations ? double.MaxValue : 0,
            exceedAllocations ? 0 : long.MaxValue);

        // Act
        // WHY: The selected overrun must be deterministic without a machine-specific performance expectation.
        runner.Measure("synthetic", exceedAllocations
            ? static () => BenchmarkFixture.AllocateBuffer(4096)
            : BenchmarkFixture.WaitForTimer);

        var exitCode = runner.Complete();

        // Assert
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
        // Arrange
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(reportOnly);

        // Act
        runner.Measure("synthetic", static () => 0);

        var exitCode = runner.Complete();

        // Assert
        using var report = JsonDocument.Parse(File.ReadAllBytes(fixture.OutputPath));

        Assert.Equal(0, exitCode);
        Assert.True(Assert.Single(report.RootElement.GetProperty("results").EnumerateArray())
            .GetProperty("passed").GetBoolean());
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
        // Arrange
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(reportOnly);
        runner.Measure("synthetic", static () => 0);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => runner.Measure("synthetic", static () => 0));

        // Assert
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
        // Arrange
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(reportOnly);
        var expected = new InvalidOperationException("Synthetic workload failure.");
        var invocationCount = 0;

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => runner.Measure("synthetic", () =>
        {
            invocationCount++;

            if (!failDuringMeasurement || invocationCount > 1)
            {
                throw expected;
            }

            return 0;
        }));

        // Assert
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
        // Arrange
        using var fixture = new BenchmarkFixture();
        var runner = fixture.CreateRunner(reportOnly);
        Directory.CreateDirectory(fixture.OutputPath);
        runner.Measure("synthetic", static () => 0);

        // Act
        var exception = Record.Exception(() => runner.Complete());

        // Assert
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
        // Arrange
        using var fixture = new BenchmarkFixture();
        var arguments = !reportOnly
            ? new[] { "--output", fixture.OutputPath }
            : flagFirst
                ? ["--report-only", "--output", fixture.OutputPath]
                : ["--output", fixture.OutputPath, "--report-only"];

        var runner = BenchmarkRunner.Create(arguments, "unused.json", "core");
        BenchmarkFixture.MeasureCoreWorkloads(runner);

        // Act
        var exitCode = runner.Complete();

        // Assert
        using var report = JsonDocument.Parse(File.ReadAllBytes(fixture.OutputPath));
        var firstResult = report.RootElement.GetProperty("results")[0];

        Assert.Equal(expectedExitCode, exitCode);
        Assert.False(firstResult.GetProperty("passed").GetBoolean());
        Assert.True(firstResult.GetProperty("allocatedBytes").GetInt64()
            > firstResult.GetProperty("maximumAllocatedBytes").GetInt64());
    }
}
