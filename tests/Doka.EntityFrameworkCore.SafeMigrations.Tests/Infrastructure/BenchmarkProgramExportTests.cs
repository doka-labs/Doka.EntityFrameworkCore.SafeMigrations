namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Pins complete native JSON evidence independently of benchmark duration and allocations.</summary>
public sealed class BenchmarkProgramExportTests
{
    /// <summary>Accepts real full exporter output, including extreme synthetic durations and allocations.</summary>
    /// <param name="nanoseconds">The synthetic native measurement duration.</param>
    /// <param name="allocatedBytes">The synthetic native allocation count.</param>
    [Theory]
    [InlineData(100D, 64L)]
    [InlineData(1_000_000_000_000_000D, 1_000_000_000_000L)]
    public void ValidateExport_AcceptsNativeFullExportWithoutPerformanceBudgets(
        double nanoseconds,
        long allocatedBytes
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var reports = BenchmarkProgramTestEvidence
            .CreateCases()
            .Select(benchmark => BenchmarkProgramTestEvidence.CreateReport(
                benchmark,
                true,
                BenchmarkProgramTestEvidence.CreateExecution(nanoseconds, allocatedBytes)))
            .ToArray();

        var summary = evidence.CreateSummary(reports);
        var paths = JsonExporter
            .Full
            .ExportToFiles(summary, NullLogger.Instance)
            .ToArray();

        var path = paths.Single();

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.ValidateExport(path, reports.Length));

        // Assert
        Assert.Null(exception);
        Assert.Single(paths);
        Assert.True(File.Exists(path));
    }

    /// <summary>Rejects an empty, truncated, or nonarray benchmark inventory.</summary>
    /// <param name="json">The independently incomplete export.</param>
    /// <param name="expectedCases">The successful native report count.</param>
    [Theory]
    [InlineData("{\"Benchmarks\":[]}", 1)]
    [InlineData("{\"Benchmarks\":[{\"Statistics\":{},\"Measurements\":[{}]}]}", 2)]
    [InlineData("{\"Benchmarks\":{}}", 1)]
    public void ValidateExport_RejectsIncompleteCaseInventory(
        string json,
        int expectedCases
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        evidence.WriteExport(json);

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.ValidateExport(evidence.ReportPath, expectedCases));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
    }

    /// <summary>Rejects malformed evidence instead of accepting an existing but unreadable JSON report.</summary>
    /// <param name="json">The independently malformed export.</param>
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not-json")]
    public void ValidateExport_RejectsMalformedJson(
        string json
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        evidence.WriteExport(json);

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.ValidateExport(evidence.ReportPath, 1));

        // Assert
        Assert.IsAssignableFrom<JsonException>(exception);
    }

    /// <summary>Rejects missing native fields rather than accepting unrelated JSON content.</summary>
    /// <param name="json">The independently incomplete field structure.</param>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Benchmarks\":[{}]}")]
    [InlineData("{\"Benchmarks\":[{\"Statistics\":{}}]}")]
    public void ValidateExport_RejectsMissingNativeFields(
        string json
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        evidence.WriteExport(json);

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.ValidateExport(evidence.ReportPath, 1));

        // Assert
        Assert.IsType<KeyNotFoundException>(exception);
    }

    /// <summary>Rejects absent or invalid native statistics and empty measurement evidence.</summary>
    /// <param name="json">The independently missing measurement evidence.</param>
    [Theory]
    [InlineData("{\"Benchmarks\":[{\"Statistics\":null,\"Measurements\":[{}]}]}")]
    [InlineData("{\"Benchmarks\":[{\"Statistics\":[],\"Measurements\":[{}]}]}")]
    [InlineData("{\"Benchmarks\":[{\"Statistics\":{},\"Measurements\":[]}]}")]
    public void ValidateExport_RejectsMissingStatisticsOrMeasurements(
        string json
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        evidence.WriteExport(json);

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.ValidateExport(evidence.ReportPath, 1));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
    }

    /// <summary>Rejects a later incomplete case even when the first case contains usable evidence.</summary>
    [Fact]
    public void ValidateExport_RejectsIncompleteCaseAfterCompleteCase()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        evidence.WriteExport(
            """
            {"Benchmarks":[
              {"Statistics":{"Mean":100},"Measurements":[{"Nanoseconds":100}]},
              {"Statistics":null,"Measurements":[]}
            ]}
            """);

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.ValidateExport(evidence.ReportPath, 2));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
    }

    /// <summary>Rejects a missing current export rather than accepting completion without a file.</summary>
    [Fact]
    public void ValidateExport_RejectsMissingFile()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.ValidateExport(evidence.ReportPath, 1));

        // Assert
        Assert.IsType<FileNotFoundException>(exception);
    }

    /// <summary>Rejects a directory where the current full report file should exist.</summary>
    [Fact]
    public void ValidateExport_RejectsDirectoryInPlaceOfFile()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.ValidateExport(evidence.DirectoryPath, 1));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(exception);
    }

    /// <summary>Rejects nonpositive successful-case counts before reading unrelated evidence.</summary>
    /// <param name="expectedCases">The invalid count of successfully executed cases.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateExport_RejectsNonpositiveExpectedCases(
        int expectedCases
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();

        // Act
        var exception = Record.Exception(() => BenchmarkProgram.ValidateExport(evidence.ReportPath, expectedCases));

        // Assert
        var argument = Assert.IsType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("expectedCases", argument.ParamName);
    }
}
