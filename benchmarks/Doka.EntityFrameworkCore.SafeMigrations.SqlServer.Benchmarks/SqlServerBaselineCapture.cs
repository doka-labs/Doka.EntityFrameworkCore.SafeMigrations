namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Captures diagnostic medians without issuing a performance qualification verdict.</summary>
/// <param name="outputPath">The absolute artifact path for measurement evidence.</param>
internal sealed class SqlServerBaselineCapture(
    string outputPath
)
{
    private const int SampleCount = 5;

    private readonly List<BaselineResult> _results = [];

    /// <summary>Warms up and measures five isolated samples of one workload.</summary>
    /// <param name="name">The stable benchmark identity.</param>
    /// <param name="action">The synchronous workload whose thread-local allocations are measured.</param>
    public void Measure(
        string name,
        Func<int> action
    )
    {
        _ = action();

        var durations = new double[SampleCount];
        var allocations = new long[SampleCount];

        for (var sample = 0; sample < SampleCount; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var result = action();

            durations[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            GC.KeepAlive(result);
        }

        Array.Sort(durations);
        Array.Sort(allocations);

        _results.Add(
            new BaselineResult(
                name,
                durations[SampleCount / 2],
                allocations[SampleCount / 2]));
    }

    /// <summary>Writes measured values with host provenance and no qualification verdict.</summary>
    /// <returns>Zero after the measurement artifact was written successfully.</returns>
    public int Complete()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("status", "measurement_only");
            writer.WriteString("capturedAtUtc", DateTimeOffset.UtcNow);
            writer.WriteNumber("sampleCount", SampleCount);
            writer.WriteString("runtime", Environment.Version.ToString());
            writer.WriteString("operatingSystem", Environment.OSVersion.ToString());
            writer.WriteString("architecture",
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
            writer.WriteStartArray("results");

            foreach (var result in _results)
            {
                writer.WriteStartObject();
                writer.WriteString("name", result.Name);
                writer.WriteNumber("durationMilliseconds", result.DurationMilliseconds);
                writer.WriteNumber("allocatedBytes", result.AllocatedBytes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        File.WriteAllBytes(outputPath, buffer.WrittenSpan.ToArray());

        Console.WriteLine(
            $"SQL Server measurement-only baseline written to '{outputPath}'. No qualification verdict was issued.");

        return 0;
    }

    private sealed record BaselineResult(
        string Name,
        double DurationMilliseconds,
        long AllocatedBytes
    );
}
