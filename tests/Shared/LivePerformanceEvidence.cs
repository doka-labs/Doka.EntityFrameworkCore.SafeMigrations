namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

internal sealed record LivePerformanceMeasurement(
    double P50Milliseconds,
    double P95Milliseconds,
    SafeMigrationRunReport LastReport
);

internal static class LivePerformanceEvidence
{
    private const int SampleCount = 20;

    private static readonly System.Text.Json.JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = true,
    };

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

    public static void Write(
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

        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var directory = Path.Combine(root, "artifacts", "performance", "live");
        Directory.CreateDirectory(directory);
        var fileName = $"{provider}-{version}.json";
        var path = Path.Combine(directory, fileName);
        var payload = new
        {
            schemaVersion = 1,
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
                maximumNoisyP95Milliseconds = (clean.P95Milliseconds * 2d) + 250d,
                passed = noisy.P95Milliseconds <= (clean.P95Milliseconds * 2d) + 250d,
            },
        };

        File.WriteAllBytes(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload, s_serializerOptions));
    }

    private static object Result(
        LivePerformanceMeasurement measurement
    ) => new
    {
        measurement.P50Milliseconds,
        measurement.P95Milliseconds,
        assessmentCount = measurement.LastReport.Assessments.Count,
        unexpectedObjectCount = measurement.LastReport.UnexpectedObjects.Count,
    };

    private static double Percentile(
        double[] sorted,
        double percentile
    )
    {
        var index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

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
