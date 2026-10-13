namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Checks informational timing verdicts without weakening execution or evidence validation.</summary>
public sealed class LivePerformanceEvidenceTests
{
    /// <summary>Both timing verdicts serialize while the original budget and evidence shape remain unchanged.</summary>
    /// <param name="cleanP95">The deterministic baseline observation.</param>
    /// <param name="noisyP95">The deterministic observation with additional catalog objects.</param>
    /// <param name="passed">Whether the observation satisfies the unchanged informational formula.</param>
    [Theory]
    [InlineData(100d, 400d, true)]
    [InlineData(100d, 450d, true)]
    [InlineData(100d, 450.001d, false)]
    [InlineData(332.923d, 963.831d, false)]
    public void BudgetVerdictIsPersistedWithoutRejectingExceededMeasurements(
        double cleanP95,
        double noisyP95,
        bool passed
    )
    {
        // Arrange
        var report = CreateReport();
        var clean = new LivePerformanceMeasurement(10d, cleanP95, report);
        var noisy = new LivePerformanceMeasurement(20d, noisyP95, report);

        // Act
        var bytes = LivePerformanceEvidence.SerializePayload("mariadb", "11.8.9", clean, noisy, 100, 1000);

        // Assert
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("informational", root.GetProperty("evaluationMode").GetString());
        Assert.Equal(20, root.GetProperty("sampleCount").GetInt32());
        Assert.Equal(100, root.GetProperty("expectedTableCount").GetInt32());
        Assert.Equal(1000, root.GetProperty("foreignTableCount").GetInt32());
        Assert.Equal(cleanP95, root.GetProperty("clean").GetProperty("P95Milliseconds").GetDouble());
        Assert.Equal(noisyP95, root.GetProperty("noisy").GetProperty("P95Milliseconds").GetDouble());
        Assert.Equal((cleanP95 * 2d) + 250d,
            root.GetProperty("requirement").GetProperty("maximumNoisyP95Milliseconds").GetDouble());
        Assert.Equal(passed, root.GetProperty("requirement").GetProperty("passed").GetBoolean());
        Assert.Equal(0, root.GetProperty("noisy").GetProperty("assessmentCount").GetInt32());
        Assert.Equal(0, root.GetProperty("noisy").GetProperty("unexpectedObjectCount").GetInt32());
    }

    /// <summary>Malformed timing observations fail instead of becoming a merely exceeded budget.</summary>
    /// <param name="invalid">The invalid duration or unrepresentable budget baseline.</param>
    /// <param name="baseline">Whether the invalid observation belongs to the clean baseline.</param>
    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(-1d, false)]
    [InlineData(double.NaN, true)]
    [InlineData(double.MaxValue, true)]
    public void InvalidMeasurementsRemainFailures(double invalid, bool baseline)
    {
        // Arrange
        var report = CreateReport();
        var clean = new LivePerformanceMeasurement(0d, baseline ? invalid : 100d, report);
        var noisy = new LivePerformanceMeasurement(0d, baseline ? 100d : invalid, report);

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => LivePerformanceEvidence.SerializePayload(
            "mariadb", "11.8.9", clean, noisy, 100, 1000));

        // Assert
        Assert.Equal(baseline ? "clean" : "noisy", exception.ParamName);
    }

    /// <summary>Invalid public fixture metadata cannot produce a misleading informational artifact.</summary>
    /// <param name="invalid">The metadata field that violates the evidence contract.</param>
    [Theory]
    [InlineData("provider")]
    [InlineData("version")]
    [InlineData("expectedTableCount")]
    [InlineData("foreignTableCount")]
    public void InvalidMetadataRemainsAValidationFailure(string invalid)
    {
        // Arrange
        var measurement = new LivePerformanceMeasurement(10d, 100d, CreateReport());

        // Act
        var exception = Assert.ThrowsAny<ArgumentException>(() => LivePerformanceEvidence.SerializePayload(
            invalid == "provider" ? " " : "mariadb", invalid == "version" ? " " : "11.8.9",
            measurement, measurement, invalid == "expectedTableCount" ? -1 : 100,
            invalid == "foreignTableCount" ? -1 : 1000));

        // Assert
        Assert.Equal(invalid, exception.ParamName);
    }

    /// <summary>Failures during either warmup or sampling retain the original exception and stop execution.</summary>
    /// <param name="failureCall">The warmup or measured invocation that fails.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task ExecutionFailuresAreNotTreatedAsInformationalTiming(int failureCall)
    {
        // Arrange
        var report = CreateReport();
        var failure = new InvalidOperationException("Deterministic action failure.");
        var calls = 0;
        Func<Task<SafeMigrationRunReport>> action = () => ++calls == failureCall
            ? Task.FromException<SafeMigrationRunReport>(failure)
            : Task.FromResult(report);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LivePerformanceEvidence.MeasureAsync(action));

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(failureCall, calls);
    }

    /// <summary>Successful measurement still performs the warmup and all twenty samples.</summary>
    [Fact]
    public async Task MeasurementRetainsSampleCountAndLastFunctionalReport()
    {
        // Arrange
        var report = CreateReport();
        var calls = 0;
        Func<Task<SafeMigrationRunReport>> action = () =>
        {
            calls++;

            return Task.FromResult(report);
        };

        // Act
        var measurement = await LivePerformanceEvidence.MeasureAsync(action);

        // Assert
        Assert.Equal(21, calls);
        Assert.Same(report, measurement.LastReport);
        Assert.True(double.IsFinite(measurement.P50Milliseconds));
        Assert.True(double.IsFinite(measurement.P95Milliseconds));
        Assert.InRange(measurement.P50Milliseconds, 0d, measurement.P95Milliseconds);
    }

    /// <summary>Artifact persistence failures still fail even when the timing verdict is informational.</summary>
    [Fact]
    public void ArtifactWriteFailureIsNotSuppressed()
    {
        // Arrange
        const string provider = "persistence-tests";
        var version = Guid.NewGuid().ToString("N");
        var measurement = new LivePerformanceMeasurement(10d, 100d, CreateReport());
        var artifactPath = Path.Combine(FindFixtureRepositoryRoot(), "artifacts", "performance", "live",
            $"{provider}-{version}.json");

        // WHY: Only this unique, empty directory belongs to the test. A
        // directory at the exact output-file name forces real persistence to
        // fail without permission changes, mutable writer hooks or foreign data.
        Directory.CreateDirectory(artifactPath);
        try
        {
            // Act
            var exception = Record.Exception(() =>
                LivePerformanceEvidence.Write(provider, version, measurement, measurement, 100, 1000));

            // Assert
            Assert.True(exception is IOException or UnauthorizedAccessException,
                $"Expected an artifact-write failure, observed {exception?.GetType().Name ?? "success"}.");
            Assert.True(Directory.Exists(artifactPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(artifactPath));
        }
        finally
        {
            // WHY: Non-recursive removal affects only the test's own empty
            // directory, never another run's evidence or its parent directory.
            Directory.Delete(artifactPath);
        }
    }

    /// <summary>
    /// Mirrors the writer's repository discovery without exposing a configurable artifact destination.
    /// </summary>
    /// <returns>The ancestor containing the repository's SDK configuration.</returns>
    private static string FindFixtureRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The fixture repository root could not be located.");
    }

    /// <summary>Creates non-confidential immutable report metadata without a live database fixture.</summary>
    private static SafeMigrationRunReport CreateReport() => new(
        SafeMigrationReportMode.Preflight,
        SafeMigrationReportStatus.NoOperations,
        DateTimeOffset.UnixEpoch,
        "performance-evidence-tests",
        new SafeMigrationProviderEnvironment("mysql", "mariadb", "11.8.9"),
        targetMigrationId: null,
        modelFingerprint: "safe-relational-model:v1:mysql:sha256:" + new string('a', 64),
        contractFingerprint: new string('b', 64),
        assessments: []);
}
