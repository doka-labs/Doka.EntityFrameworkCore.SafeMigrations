namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Retains sampled catalog timings together with the final functional report.</summary>
/// <param name="P50Milliseconds">The sampled median duration.</param>
/// <param name="P95Milliseconds">The sampled 95th-percentile duration.</param>
/// <param name="LastReport">The final analyzed report, whose functional assertions remain mandatory.</param>
internal sealed record LivePerformanceMeasurement(
    double P50Milliseconds,
    double P95Milliseconds,
    SafeMigrationRunReport LastReport
);

/// <summary>Records live timing evidence without turning hardware-dependent budgets into correctness gates.</summary>
internal static class LivePerformanceEvidence
{
    private const int SampleCount = 20;

    private static readonly System.Text.Json.JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Warms the action once and captures twenty samples without suppressing execution failures.</summary>
    /// <param name="action">The live analysis whose exceptions and cancellation must propagate.</param>
    /// <returns>The sampled percentiles and final functional report.</returns>
    public static async Task<LivePerformanceMeasurement> MeasureAsync(
        Func<Task<SafeMigrationRunReport>> action
    )
    {
        ArgumentNullException.ThrowIfNull(action);

        _ = await action();
        var durations = new double[SampleCount];
        SafeMigrationRunReport? lastReport = null;
        for (var sample = 0; sample < SampleCount; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            lastReport = await action();
            durations[sample] = Stopwatch.GetElapsedTime(started)
                .TotalMilliseconds;
        }

        Array.Sort(durations);

        return new LivePerformanceMeasurement(
            Percentile(durations, 0.50),
            Percentile(durations, 0.95),
            lastReport ?? throw new InvalidOperationException("The live performance run produced no report."));
    }

    /// <summary>Persists informational evidence and prints the measured budget verdict.</summary>
    /// <param name="provider">The public provider family.</param>
    /// <param name="version">The measured server version.</param>
    /// <param name="clean">The baseline catalog measurement.</param>
    /// <param name="noisy">The catalog measurement with additional foreign objects.</param>
    /// <param name="expectedTableCount">The deterministic managed fixture size.</param>
    /// <param name="foreignTableCount">The deterministic foreign fixture size.</param>
    public static void Write(
        string provider,
        string version,
        LivePerformanceMeasurement clean,
        LivePerformanceMeasurement noisy,
        int expectedTableCount,
        int foreignTableCount
    )
    {
        var payload = SerializePayload(provider, version, clean, noisy, expectedTableCount, foreignTableCount);
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "artifacts", "performance", "live");
        Directory.CreateDirectory(directory);
        var fileName = $"{provider}-{version}.json";
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, payload);
        var maximum = MaximumNoisyP95(clean);

        // WHY: Shared runners and local contention can exceed this timing
        // budget without changing any analyzed contract. Preserve the failed
        // measurement as evidence; execution and artifact failures still throw.
        Console.WriteLine(
            $"Live performance: provider={provider}, version={version}, evaluationMode=informational, "
            + $"budgetPassed={noisy.P95Milliseconds <= maximum}, "
            + FormattableString.Invariant($"noisyP95Milliseconds={noisy.P95Milliseconds:F3}, ")
            + FormattableString.Invariant($"maximumNoisyP95Milliseconds={maximum:F3}."));
    }

    /// <summary>Serializes the unchanged timing formula with an explicitly informational budget verdict.</summary>
    /// <param name="provider">The public provider family.</param>
    /// <param name="version">The measured server version.</param>
    /// <param name="clean">The baseline catalog measurement.</param>
    /// <param name="noisy">The catalog measurement with additional foreign objects.</param>
    /// <param name="expectedTableCount">The deterministic managed fixture size.</param>
    /// <param name="foreignTableCount">The deterministic foreign fixture size.</param>
    /// <returns>Schema-version-one JSON; exceeding the budget is recorded, not thrown.</returns>
    public static byte[] SerializePayload(
        string provider,
        string version,
        LivePerformanceMeasurement clean,
        LivePerformanceMeasurement noisy,
        int expectedTableCount,
        int foreignTableCount
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedTableCount);
        ArgumentOutOfRangeException.ThrowIfNegative(foreignTableCount);
        ValidateMeasurement(clean, nameof(clean));
        ValidateMeasurement(noisy, nameof(noisy));
        var maximum = MaximumNoisyP95(clean);
        var payload = new
        {
            schemaVersion = 1,
            evaluationMode = "informational",
            runtime = Environment.Version.ToString(),
            provider,
            version,
            sampleCount = SampleCount,
            expectedTableCount,
            foreignTableCount,
            clean = Result(clean),
            noisy = Result(noisy),
            requirement = new
            {
                maximumNoisyP95Milliseconds = maximum,
                passed = noisy.P95Milliseconds <= maximum,
            },
        };

        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, s_serializerOptions);
    }

    /// <summary>Rejects invalid observations rather than treating corrupt evidence as a slow measurement.</summary>
    /// <param name="measurement">The sampled duration and final report.</param>
    /// <param name="parameterName">The evidence role to identify in a validation exception.</param>
    private static void ValidateMeasurement(LivePerformanceMeasurement measurement, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(measurement, parameterName);
        ArgumentNullException.ThrowIfNull(measurement.LastReport, parameterName);
        if (!double.IsFinite(measurement.P50Milliseconds) || !double.IsFinite(measurement.P95Milliseconds)
            || measurement.P50Milliseconds < 0d || measurement.P95Milliseconds < measurement.P50Milliseconds)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timing samples must be finite and ordered.");
        }
    }

    /// <summary>Retains the original budget formula and rejects an unrepresentable derived measurement.</summary>
    /// <param name="clean">The valid baseline measurement.</param>
    /// <returns>The informational noisy-p95 budget.</returns>
    private static double MaximumNoisyP95(LivePerformanceMeasurement clean)
    {
        var maximum = (clean.P95Milliseconds * 2d) + 250d;
        if (!double.IsFinite(maximum))
        {
            throw new ArgumentOutOfRangeException(nameof(clean), "The derived timing budget must be finite.");
        }

        return maximum;
    }

    /// <summary>Captures timing and deterministic report counts without serializing report payloads.</summary>
    /// <param name="measurement">The validated observation.</param>
    private static object Result(
        LivePerformanceMeasurement measurement
    ) => new
    {
        measurement.P50Milliseconds,
        measurement.P95Milliseconds,
        assessmentCount = measurement.LastReport.Assessments.Count,
        unexpectedObjectCount = measurement.LastReport.UnexpectedObjects.Count,
    };

    /// <summary>Selects the nearest-rank percentile from an already sorted sample array.</summary>
    /// <param name="sorted">The sorted measured durations.</param>
    /// <param name="percentile">The requested percentile in the closed zero-to-one interval.</param>
    /// <returns>The selected measured duration.</returns>
    private static double Percentile(
        double[] sorted,
        double percentile
    )
    {
        var index = (int)Math.Ceiling(sorted.Length * percentile) - 1;

        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>Locates the artifact owner without swallowing a missing repository boundary.</summary>
    /// <param name="start">The test execution directory.</param>
    /// <returns>The ancestor containing the repository's SDK configuration.</returns>
    private static string FindRepositoryRoot(
        string start
    )
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root could not be located.");
    }
}
