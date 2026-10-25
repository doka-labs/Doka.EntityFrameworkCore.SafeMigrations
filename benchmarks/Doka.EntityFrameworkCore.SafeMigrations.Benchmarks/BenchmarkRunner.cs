namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Records raw prepared-workload samples without imposing hardware-sensitive limits.</summary>
internal static class MaintenanceAttribution
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>Samples the same prepared action after warmup, keeping allocation and elapsed evidence separate.</summary>
    /// <param name="action">The synchronous action whose result prevents dead-code elimination.</param>
    /// <param name="iterations">The fixed number of invocations in each raw sample.</param>
    /// <returns>Seven unsorted samples, each retaining its result checksum.</returns>
    internal static object[] Measure(
        Func<int> action,
        int iterations
    )
    {
        for (var warmup = 0; warmup < 8; warmup++)
        {
            _ = action();
        }

        var samples = new object[7];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var checksum = 0L;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                checksum += action();
            }

            samples[sample] = new
            {
                Iterations = iterations,
                ElapsedMilliseconds = Stopwatch.GetElapsedTime(started)
                    .TotalMilliseconds,
                AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
                Checksum = checksum,
            };
        }

        return samples;
    }

    /// <summary>Writes source-labeled attribution results; execution and serialization errors remain fatal.</summary>
    /// <param name="outputPath">The caller-owned evidence file.</param>
    /// <param name="provider">The provider profile, not a claim of live engine execution.</param>
    /// <param name="results">All measured workload results and structural controls.</param>
    /// <returns>Zero once complete evidence has been persisted.</returns>
    internal static int Write(
        string outputPath,
        string provider,
        IReadOnlyList<object> results
    )
    {
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new
                {
                    Runtime = Environment.Version.ToString(),
                    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    Provider = provider,
                    Measurement = "prepared client workload; elapsed times informational",
                    SourceLabel = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_SOURCE")
                        ?? "unlabelled",
                    Results = results,
                },
                s_jsonOptions));
        Console.WriteLine($"Recorded {results.Count} maintenance workloads in {path}.");

        return 0;
    }

    /// <summary>Hashes exact output outside measured actions so equality costs do not pollute generation evidence.</summary>
    /// <param name="value">The complete deterministic structural output.</param>
    /// <returns>The uppercase SHA-256 digest of its UTF-8 representation.</returns>
    internal static string Hash(
        string value
    ) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    /// <summary>Fails attribution immediately when a structural or adversarial control is not satisfied.</summary>
    /// <param name="condition">The exact functional invariant.</param>
    /// <param name="message">The violated measurement contract.</param>
    internal static void Require(
        bool condition,
        string message
    )
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

/// <summary>
/// Captures complete benchmark evidence with an optional informational budget verdict.
/// </summary>
internal sealed class BenchmarkRunner
{
    private const int SampleCount = 5;

    private readonly IReadOnlyDictionary<string, BenchmarkBudget> _budgets;
    private readonly HashSet<string> _measuredNames = new(StringComparer.Ordinal);
    private readonly string _outputPath;
    private readonly List<BenchmarkResult> _results = [];

    /// <summary>
    /// Gets whether completed budget overruns are informational instead of process failures.
    /// </summary>
    internal bool ReportOnly { get; }

    /// <summary>
    /// Creates a runner over known budgets and an owned output path.
    /// </summary>
    internal BenchmarkRunner(
        IReadOnlyDictionary<string, BenchmarkBudget> budgets,
        string outputPath,
        bool reportOnly
    )
    {
        _budgets = budgets;
        _outputPath = outputPath;
        ReportOnly = reportOnly;
    }

    /// <summary>
    /// Reads the selected budget set and accepts an optional output path and report-only mode.
    /// </summary>
    public static BenchmarkRunner Create(
        string[] arguments,
        string defaultOutputFileName,
        string benchmarkSet
    )
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var budgetPath = Path.Combine(repositoryRoot, "eng", "performance-budgets.json");
        var (outputPath, reportOnly) = ReadOptions(arguments, repositoryRoot, defaultOutputFileName);

        return new BenchmarkRunner(ReadBudgets(budgetPath, benchmarkSet), outputPath, reportOnly);
    }

    /// <summary>
    /// Measures one known workload without suppressing configuration or execution errors.
    /// </summary>
    public void Measure(
        string name,
        Func<int> action
    )
    {
        if (!_budgets.TryGetValue(name, out var budget))
        {
            throw new InvalidOperationException($"No performance budget exists for '{name}'.");
        }

        if (!_measuredNames.Add(name))
        {
            throw new InvalidOperationException($"Benchmark '{name}' was measured more than once.");
        }

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

        var duration = durations[SampleCount / 2];
        var allocated = allocations[SampleCount / 2];
        var maximumDuration = budget.BaselineDurationMilliseconds * (1d + (budget.RegressionTolerancePercent / 100d));

        _results.Add(
            new BenchmarkResult(
                name,
                duration,
                allocated,
                maximumDuration,
                budget.MaximumAllocatedBytes,
                duration <= maximumDuration && allocated <= budget.MaximumAllocatedBytes));
    }

    /// <summary>
    /// Writes all measurements and returns a budget failure only when strict evaluation was requested.
    /// </summary>
    public int Complete()
    {
        var unmeasuredBudgets = _budgets.Keys
            .Where(name => !_measuredNames.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unmeasuredBudgets.Length > 0)
        {
            throw new InvalidOperationException(
                $"Performance budgets were not measured: {string.Join(", ", unmeasuredBudgets)}.");
        }

        WriteResults(_outputPath, _results);

        foreach (var result in _results)
        {
            var verdict = result.Passed
                ? "PASS"
                : ReportOnly ? "EXCEEDED (informational)" : "FAIL";

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{result.Name}: {result.DurationMilliseconds:F3} ms, "
                    + $"{result.AllocatedBytes} bytes, {verdict}"));
        }

        // Shared CI hardware cannot provide a stable budget verdict. Only a complete,
        // successfully written report is informational; execution errors still propagate.
        return ReportOnly || _results.All(static result => result.Passed) ? 0 : 1;
    }

    /// <summary>
    /// Validates the complete budget document before selecting a benchmark set.
    /// </summary>
    internal static Dictionary<string, BenchmarkBudget> ReadBudgets(
        string path,
        string benchmarkSet
    )
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 2)
        {
            throw new InvalidOperationException("Unsupported performance-budget schema version.");
        }

        var allBudgets = new Dictionary<string, BenchmarkBudget>(StringComparer.Ordinal);

        foreach (var property in root
                     .GetProperty("budgets")
                     .EnumerateObject())
        {
            var value = property.Value;
            var baselineDuration = value.GetProperty("baselineDurationMilliseconds").GetDouble();
            var tolerance = value.GetProperty("regressionTolerancePercent").GetDouble();
            var allocationLimit = value.GetProperty("maximumAllocatedBytes").GetInt64();

            // Report-only must not reinterpret a malformed limit as an ordinary overrun.
            if (!double.IsFinite(baselineDuration) || baselineDuration < 0
                || !double.IsFinite(tolerance) || tolerance < 0
                || allocationLimit < 0
                || !double.IsFinite(baselineDuration * (1d + (tolerance / 100d))))
            {
                throw new InvalidOperationException(
                    $"Performance budget '{property.Name}' requires finite, nonnegative comparison limits.");
            }

            allBudgets.Add(
                property.Name,
                new BenchmarkBudget(baselineDuration, tolerance, allocationLimit));
        }

        var assignedNames = new HashSet<string>(StringComparer.Ordinal);
        var selectedNames = new List<string>();
        var selectedSetExists = false;
        foreach (var set in root.GetProperty("benchmarkSets").EnumerateObject())
        {
            if (set.NameEquals(benchmarkSet))
            {
                selectedSetExists = true;
            }

            foreach (var nameElement in set.Value.EnumerateArray())
            {
                var name = nameElement.GetString()
                    ?? throw new InvalidOperationException("Benchmark names must not be null.");
                if (!allBudgets.ContainsKey(name))
                {
                    throw new InvalidOperationException(
                        $"Benchmark set '{set.Name}' references missing budget '{name}'.");
                }

                if (!assignedNames.Add(name))
                {
                    throw new InvalidOperationException(
                        $"Performance budget '{name}' belongs to more than one benchmark set.");
                }

                if (set.NameEquals(benchmarkSet))
                {
                    selectedNames.Add(name);
                }
            }
        }

        if (!selectedSetExists)
        {
            throw new InvalidOperationException($"Unknown benchmark set '{benchmarkSet}'.");
        }

        var orphanedBudgets = allBudgets.Keys
            .Where(name => !assignedNames.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (orphanedBudgets.Length > 0)
        {
            throw new InvalidOperationException(
                $"Performance budgets do not belong to a benchmark set: {string.Join(", ", orphanedBudgets)}.");
        }

        var result = new Dictionary<string, BenchmarkBudget>(selectedNames.Count, StringComparer.Ordinal);
        foreach (var name in selectedNames)
        {
            result.Add(name, allBudgets[name]);
        }

        return result;
    }

    private static void WriteResults(
        string path,
        IReadOnlyList<BenchmarkResult> results
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("runtime", Environment.Version.ToString());
            writer.WriteStartArray("results");

            foreach (var result in results)
            {
                writer.WriteStartObject();
                writer.WriteString("name", result.Name);
                writer.WriteNumber("durationMilliseconds", result.DurationMilliseconds);
                writer.WriteNumber("allocatedBytes", result.AllocatedBytes);
                writer.WriteNumber("maximumDurationMilliseconds", result.MaximumDurationMilliseconds);
                writer.WriteNumber("maximumAllocatedBytes", result.MaximumAllocatedBytes);
                writer.WriteBoolean("passed", result.Passed);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        File.WriteAllBytes(path, buffer.WrittenSpan.ToArray());
    }

    private static (string OutputPath, bool ReportOnly) ReadOptions(
        string[] arguments,
        string repositoryRoot,
        string defaultOutputFileName
    )
    {
        var options = arguments switch
        {
            [] => (OutputPath: null, ReportOnly: false),
            ["--report-only"] => (OutputPath: null, ReportOnly: true),
            ["--output", var path] => (OutputPath: (string?)path, ReportOnly: false),
            ["--report-only", "--output", var path] => (OutputPath: (string?)path, ReportOnly: true),
            ["--output", var path, "--report-only"] => (OutputPath: (string?)path, ReportOnly: true),
            _ => throw new ArgumentException("Usage: benchmark [--report-only] [--output <path>]"),
        };

        if (options.OutputPath is { } outputPath
            && (string.IsNullOrWhiteSpace(outputPath) || outputPath.StartsWith("--", StringComparison.Ordinal)))
        {
            throw new ArgumentException("The benchmark output path must be a nonempty file path.");
        }

        return (
            options.OutputPath is null
                ? Path.Combine(repositoryRoot, "artifacts", "performance", defaultOutputFileName)
                : Path.GetFullPath(options.OutputPath, repositoryRoot),
            options.ReportOnly);
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

    /// <summary>
    /// Defines a workload's duration comparison and allocation limit.
    /// </summary>
    internal sealed record BenchmarkBudget(
        double BaselineDurationMilliseconds,
        double RegressionTolerancePercent,
        long MaximumAllocatedBytes
    );

    private sealed record BenchmarkResult(
        string Name,
        double DurationMilliseconds,
        long AllocatedBytes,
        double MaximumDurationMilliseconds,
        long MaximumAllocatedBytes,
        bool Passed
    );
}
