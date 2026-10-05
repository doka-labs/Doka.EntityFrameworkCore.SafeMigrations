namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies durable, ordered and non-sensitive qualification stage evidence.</summary>
public sealed class SqlServerLiveQualificationEvidenceTests
{
    /// <summary>Each phase is persisted immediately, preserving earlier phases instead of overwriting them.</summary>
    [Fact]
    public void WriteStage_AppendsOrderedUtcMarkers()
    {
        // Arrange
        var workload = "probe-" + Guid.NewGuid().ToString("N");
        var path = SqlServerLiveQualificationEvidence.GetPath(workload);

        try
        {
            // Act
            SqlServerLiveQualificationEvidence.WriteStage(workload, "initial-analysis");
            SqlServerLiveQualificationEvidence.WriteStage(workload, "completed");
            var lines = File.ReadAllLines(path);

            // Assert
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("\tinitial-analysis", lines[0], StringComparison.Ordinal);
            Assert.EndsWith("\tcompleted", lines[1], StringComparison.Ordinal);
            Assert.All(lines, line => Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(
                line[..line.IndexOf('\t')], CultureInfo.InvariantCulture).Offset));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Path traversal and diagnostic payloads cannot become artifact names or stage content.</summary>
    /// <param name="value">The invalid workload or stage.</param>
    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("connection;password")]
    [InlineData("sql\ntext")]
    [InlineData("")]
    public void WriteStage_RejectsPathsAndPayloads(string value)
    {
        // Arrange
        const string validIdentifier = "probe";

        // Act
        var workloadFailure = Record.Exception(() =>
            SqlServerLiveQualificationEvidence.WriteStage(value, validIdentifier));
        var stageFailure = Record.Exception(() =>
            SqlServerLiveQualificationEvidence.WriteStage(validIdentifier, value));

        // Assert
        Assert.IsAssignableFrom<ArgumentException>(workloadFailure);
        Assert.IsAssignableFrom<ArgumentException>(stageFailure);
    }

    /// <summary>Records completion duration, submitted operations, and database transport counts.</summary>
    [Fact]
    public void AnalysisCaptureWritesOnlyBoundedStageMetadata()
    {
        // Arrange
        using var directory = new EvidenceDirectory();
        var path = directory.FilePath("bounded.log");
        using var capture = new SqlServerLiveQualificationEvidence.AnalysisStageCapture(path);

        // Act
        using (var activity = SafeMigrationTelemetry.StartAnalysisStage("catalog-batch", 7))
        {
            activity?.SetTag("safe_migrations.catalog.statement_count", 2);
        }

        capture.ThrowIfWriteFailed();
        var lines = File.ReadAllLines(path);

        // Assert
        Assert.Equal(2, lines.Length);
        Assert.Contains("\tanalysis-stage-start\tstage=catalog-batch\toperations=7\t", lines[0]);
        Assert.Contains("\tanalysis-stage-completed\tstage=catalog-batch\toperations=7\t", lines[1]);
        Assert.Contains("\tduration-ms=", lines[1]);
        Assert.EndsWith("\tbatches=1\tstatements=2", lines[1]);
        Assert.DoesNotContain("sqlserver.live.qualification", string.Join('\n', lines));
    }

    /// <summary>Never mixes stage evidence when asynchronous workloads and listeners overlap.</summary>
    [Fact]
    public async Task AnalysisCaptureSeparatesConcurrentWorkloadTraces()
    {
        // Arrange
        using var directory = new EvidenceDirectory();
        var firstPath = directory.FilePath("first.log");
        var secondPath = directory.FilePath("second.log");
        var previousActivity = Activity.Current;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        await Task.WhenAll(
            RecordStageAsync(firstPath, 7, firstStarted, secondStarted.Task),
            RecordStageAsync(secondPath, 13, secondStarted, firstStarted.Task));

        var firstLines = File.ReadAllLines(firstPath);
        var secondLines = File.ReadAllLines(secondPath);

        // Assert
        Assert.Equal(2, firstLines.Length);
        Assert.Equal(2, secondLines.Length);
        Assert.All(firstLines, static line => Assert.Contains("\toperations=7\t", line));
        Assert.All(secondLines, static line => Assert.Contains("\toperations=13\t", line));
        Assert.Same(previousActivity, Activity.Current);
    }

    /// <summary>Preserves the analysis exception even when a stage callback encounters an unusable path.</summary>
    [Fact]
    public void AnalysisCaptureWriteFailureDoesNotReplaceTheWorkloadException()
    {
        // Arrange
        using var directory = new EvidenceDirectory();
        using var capture = new SqlServerLiveQualificationEvidence.AnalysisStageCapture(directory.Path);
        var original = new InvalidOperationException("The workload failed.");
        Action workload = () =>
        {
            using var activity = SafeMigrationTelemetry.StartAnalysisStage("provider-classification", 1);

            throw original;
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(workload);

        // Assert
        Assert.Same(original, exception);
    }

    /// <summary>Fails successful qualification explicitly when its stage evidence was not persisted.</summary>
    [Fact]
    public void AnalysisCaptureReportsWriteFailureAfterSuccessfulWorkload()
    {
        // Arrange
        using var directory = new EvidenceDirectory();
        using var capture = new SqlServerLiveQualificationEvidence.AnalysisStageCapture(directory.Path);

        // Act
        using (SafeMigrationTelemetry.StartAnalysisStage("provider-classification", 1))
        {
        }

        var exception = Assert.Throws<InvalidOperationException>(capture.ThrowIfWriteFailed);

        // Assert
        Assert.Equal("The analysis stage evidence could not be persisted.", exception.Message);
        Assert.True(exception.InnerException is IOException or UnauthorizedAccessException);
    }

    /// <summary>Waits for the peer listener before recording a phase under its own workload trace.</summary>
    private static async Task RecordStageAsync(
        string path,
        int operationCount,
        TaskCompletionSource started,
        Task peerStarted
    )
    {
        using var capture = new SqlServerLiveQualificationEvidence.AnalysisStageCapture(path);
        started.SetResult();
        await peerStarted;
        using (SafeMigrationTelemetry.StartAnalysisStage("catalog-batch", operationCount))
        {
        }

        capture.ThrowIfWriteFailed();
    }

    /// <summary>Owns a unique temporary directory so evidence tests never mutate shared artifact paths.</summary>
    private sealed class EvidenceDirectory : IDisposable
    {
        /// <summary>Creates an exclusively owned temporary test directory.</summary>
        internal EvidenceDirectory() => Path = Directory.CreateTempSubdirectory("sqlserver-stage-evidence-").FullName;

        /// <summary>Gets the owned temporary directory.</summary>
        internal string Path { get; }

        /// <summary>Combines a fixed test filename with the owned directory.</summary>
        internal string FilePath(string name) => System.IO.Path.Combine(Path, name);

        /// <summary>Removes only the directory created by this test instance.</summary>
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
