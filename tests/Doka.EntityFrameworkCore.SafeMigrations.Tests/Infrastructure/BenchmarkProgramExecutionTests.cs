namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Pins execution completeness without measuring test-host timing or allocations.</summary>
public sealed class BenchmarkProgramExecutionTests
{
    /// <summary>Accepts native results regardless of their measured duration or allocation size.</summary>
    /// <param name="nanoseconds">An independent synthetic workload duration.</param>
    /// <param name="allocatedBytes">An independent synthetic allocation count.</param>
    [Theory]
    [InlineData(1D, 0L)]
    [InlineData(1_000_000_000_000_000D, 1_000_000_000_000L)]
    public void HasCompleteExecution_AcceptsMeasurementsWithoutPerformanceBudgets(
        double nanoseconds,
        long allocatedBytes
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var benchmark = BenchmarkProgramTestEvidence.CreateCases()[0];
        var execution = BenchmarkProgramTestEvidence.CreateExecution(nanoseconds, allocatedBytes);
        var report = BenchmarkProgramTestEvidence.CreateReport(benchmark, true, execution);
        var summary = evidence.CreateSummary([report]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.True(complete);
        Assert.NotNull(report.ResultStatistics);
        Assert.Equal(nanoseconds, report.ResultStatistics.Mean);
        Assert.Equal(allocatedBytes, report.GcStats.GetTotalAllocatedBytes(false));
    }

    /// <summary>Accepts all native cases and noncritical warnings rather than turning warnings into budgets.</summary>
    [Fact]
    public void HasCompleteExecution_AcceptsMultipleCasesAndNoncriticalWarnings()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var reports = BenchmarkProgramTestEvidence.CreateCases()
            .Select(static benchmark => BenchmarkProgramTestEvidence.CreateReport(
                benchmark, true, BenchmarkProgramTestEvidence.CreateExecution()))
            .ToArray();
        var summary = evidence.CreateSummary(reports, new ValidationError(false, "Synthetic warning."));

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.True(complete);
        Assert.Equal(2, summary.BenchmarksCases.Length);
        Assert.Equal(2, summary.Reports.Length);
        Assert.False(summary.HasCriticalValidationErrors);
    }

    /// <summary>Requires the independent native selected-case count to match all completed reports exactly.</summary>
    /// <param name="expectedCases">The independent selected-case inventory.</param>
    /// <param name="expectedComplete">Whether the completed report inventory matches the selection.</param>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void HasCompleteExecution_RequiresExactSelectedCaseCount(
        int expectedCases,
        bool expectedComplete
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var reports = BenchmarkProgramTestEvidence.CreateCases()
            .Select(static benchmark => BenchmarkProgramTestEvidence.CreateReport(
                benchmark, true, BenchmarkProgramTestEvidence.CreateExecution()))
            .ToArray();
        var summary = evidence.CreateSummary(reports);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary], expectedCases);

        // Assert
        Assert.Equal(expectedComplete, complete);
        Assert.Equal(2, summary.Reports.Length);
    }

    /// <summary>Rejects successful prefix evidence when a later independently selected case is missing.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsMissingSelectedSuffix()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var selectedCases = BenchmarkProgramTestEvidence.CreateCases();
        var report = BenchmarkProgramTestEvidence.CreateReport(
            selectedCases[0], true, BenchmarkProgramTestEvidence.CreateExecution());
        var summary = evidence.CreateSummary([report]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary], selectedCases.Length);

        // Assert
        Assert.False(complete);
        Assert.True(report.Success);
        Assert.NotNull(report.ResultStatistics);
        Assert.Equal(2, selectedCases.Length);
        Assert.Single(summary.Reports);
    }

    /// <summary>Counts successful selected cases across every native workload summary.</summary>
    [Fact]
    public void HasCompleteExecution_AcceptsExactCountAcrossSummaries()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var selectedCases = BenchmarkProgramTestEvidence.CreateCases();
        var summaries = selectedCases
            .Select(benchmark => evidence.CreateSummary([BenchmarkProgramTestEvidence.CreateReport(
                benchmark, true, BenchmarkProgramTestEvidence.CreateExecution())]))
            .ToArray();

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution(summaries, selectedCases.Length);

        // Assert
        Assert.True(complete);
        Assert.Equal(2, summaries.Length);
    }

    /// <summary>Rejects an invocation that did not select or execute any benchmark family.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsEmptySummaries()
    {
        // Arrange
        Summary[] summaries = [];

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution(summaries);

        // Assert
        Assert.False(complete);
    }

    /// <summary>Rejects a native summary with no benchmark report evidence.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsSummaryWithoutReports()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var summary = evidence.CreateSummary([]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.False(complete);
    }

    /// <summary>Rejects critical native validation errors even when measurements are present.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsCriticalValidationErrors()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var benchmark = BenchmarkProgramTestEvidence.CreateCases()[0];
        var report = BenchmarkProgramTestEvidence.CreateReport(
            benchmark, true, BenchmarkProgramTestEvidence.CreateExecution());
        var summary = evidence.CreateSummary([report], new ValidationError(true, "Synthetic configuration failure."));

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.False(complete);
        Assert.NotNull(report.ResultStatistics);
    }

    /// <summary>Rejects a failed report even when its child emitted otherwise usable measurements.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsFailedReportWithMeasurements()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var benchmark = BenchmarkProgramTestEvidence.CreateCases()[0];
        var report = BenchmarkProgramTestEvidence.CreateReport(
            benchmark, false, BenchmarkProgramTestEvidence.CreateExecution());
        var summary = evidence.CreateSummary([report]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.False(complete);
        Assert.NotNull(report.ResultStatistics);
    }

    /// <summary>Rejects the native failed-build report shape without invoking a build.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsFailedBuild()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var benchmark = BenchmarkProgramTestEvidence.CreateCases()[0];
        var generation = GenerateResult.Success(ArtifactsPaths.Empty, []);
        var build = BuildResult.Failure(generation, "Synthetic build failure.");
        var report = new BenchmarkReport(false, benchmark, generation, build, [], []);
        var summary = evidence.CreateSummary([report]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.False(complete);
        Assert.False(report.BuildResult.IsBuildSuccess);
        Assert.Empty(report.ExecuteResults);
    }

    /// <summary>Rejects a success flag that has no corresponding child execution evidence.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsMissingChildExecutions()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var report = BenchmarkProgramTestEvidence.CreateReport(BenchmarkProgramTestEvidence.CreateCases()[0], true);
        var summary = evidence.CreateSummary([report]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.False(complete);
        Assert.Empty(report.ExecuteResults);
    }

    /// <summary>Rejects every nonzero or unavailable child status, including after a successful launch.</summary>
    /// <param name="exitCode">The independently failed or unavailable child status.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(123)]
    [InlineData(null)]
    public void HasCompleteExecution_RejectsAnyFailedChildExitAfterSuccessfulLaunch(
        int? exitCode
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var benchmark = BenchmarkProgramTestEvidence.CreateCases()[0];
        var report = BenchmarkProgramTestEvidence.CreateReport(benchmark, true,
            BenchmarkProgramTestEvidence.CreateExecution(),
            BenchmarkProgramTestEvidence.CreateExecution(exitCode: exitCode));
        var summary = evidence.CreateSummary([report]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.False(complete);
        Assert.NotNull(report.ResultStatistics);
        Assert.Equal(2, report.ExecuteResults.Count);
    }

    /// <summary>Rejects a child with preliminary measurements but no final workload results.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsMissingResultMeasurements()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var benchmark = BenchmarkProgramTestEvidence.CreateCases()[0];
        var execution = BenchmarkProgramTestEvidence.CreateExecution(stage: IterationStage.Actual);
        var report = BenchmarkProgramTestEvidence.CreateReport(benchmark, true, execution);
        var summary = evidence.CreateSummary([report]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.False(complete);
        Assert.NotEmpty(report.AllMeasurements);
        Assert.Null(report.ResultStatistics);
    }

    /// <summary>Rejects a zero-status child that did not produce any measurements.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsEmptyChildMeasurements()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var execution = new ExecuteResult(true, 0, 1, [], [], [], 1);
        var report = BenchmarkProgramTestEvidence.CreateReport(
            BenchmarkProgramTestEvidence.CreateCases()[0], true, execution);
        var summary = evidence.CreateSummary([report]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.False(complete);
        Assert.Null(report.ResultStatistics);
        Assert.Single(report.ExecuteResults);
    }

    /// <summary>Rejects incomplete evidence in any selected case, not just the first report.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsFailedCaseAfterSuccessfulCase()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var benchmarks = BenchmarkProgramTestEvidence.CreateCases();
        var successful = BenchmarkProgramTestEvidence.CreateReport(
            benchmarks[0], true, BenchmarkProgramTestEvidence.CreateExecution());
        var failed = BenchmarkProgramTestEvidence.CreateReport(benchmarks[1], false);
        var summary = evidence.CreateSummary([successful, failed]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([summary]);

        // Assert
        Assert.False(complete);
        Assert.Equal(2, summary.Reports.Length);
    }

    /// <summary>Rejects incomplete evidence in any returned summary, not just the first workload family.</summary>
    [Fact]
    public void HasCompleteExecution_RejectsFailedSummaryAfterSuccessfulSummary()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var benchmark = BenchmarkProgramTestEvidence.CreateCases()[0];
        var successfulReport = BenchmarkProgramTestEvidence.CreateReport(
            benchmark, true, BenchmarkProgramTestEvidence.CreateExecution());
        var successful = evidence.CreateSummary([successfulReport]);
        var failed = evidence.CreateSummary([BenchmarkProgramTestEvidence.CreateReport(benchmark, false)]);

        // Act
        var complete = BenchmarkProgram.HasCompleteExecution([successful, failed]);

        // Assert
        Assert.False(complete);
    }

    /// <summary>Rejects a missing argument vector before entering native configuration or execution.</summary>
    [Fact]
    public void Run_RejectsNullArguments()
    {
        // Arrange
        Type[] benchmarkTypes = [typeof(BenchmarkProgramOutcomeProbe)];

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.Run(null!, benchmarkTypes));

        // Assert
        var argument = Assert.IsType<ArgumentNullException>(exception);
        Assert.Equal("args", argument.ParamName);
    }

    /// <summary>Rejects a missing workload inventory before entering native configuration or execution.</summary>
    [Fact]
    public void Run_RejectsNullBenchmarkTypes()
    {
        // Arrange
        string[] arguments = ["--list", "flat"];

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.Run(arguments, null!));

        // Assert
        var argument = Assert.IsType<ArgumentNullException>(exception);
        Assert.Equal("benchmarkTypes", argument.ParamName);
    }

    /// <summary>Rejects an empty workload inventory before entering native configuration or execution.</summary>
    [Fact]
    public void Run_RejectsEmptyBenchmarkTypes()
    {
        // Arrange
        string[] arguments = ["--list", "flat"];

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.Run(arguments));

        // Assert
        var argument = Assert.IsType<ArgumentException>(exception);
        Assert.Equal("benchmarkTypes", argument.ParamName);
    }
}
