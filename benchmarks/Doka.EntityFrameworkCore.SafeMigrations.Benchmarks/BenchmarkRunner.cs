namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

internal sealed class BenchmarkRunner
{
    private const int SampleCount = 5;

    private readonly IReadOnlyDictionary<string, BenchmarkBudget> _budgets;
    private readonly HashSet<string> _measuredNames = new(StringComparer.Ordinal);
    private readonly string _outputPath;
    private readonly List<BenchmarkResult> _results = [];

    private BenchmarkRunner(
        IReadOnlyDictionary<string, BenchmarkBudget> budgets,
        string outputPath
    )
    {
        _budgets = budgets;
        _outputPath = outputPath;
    }

    public static BenchmarkRunner Create(
        string[] arguments,
        string defaultOutputFileName,
        string benchmarkSet
    )
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var budgetPath = Path.Combine(repositoryRoot, "eng", "performance-budgets.json");
        var outputPath = ReadOutputPath(arguments, repositoryRoot, defaultOutputFileName);

        return new BenchmarkRunner(ReadBudgets(budgetPath, benchmarkSet), outputPath);
    }

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
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{result.Name}: {result.DurationMilliseconds:F3} ms, "
                    + $"{result.AllocatedBytes} bytes, {(result.Passed ? "PASS" : "FAIL")}"));
        }

        return _results.All(static result => result.Passed) ? 0 : 1;
    }

    private static Dictionary<string, BenchmarkBudget> ReadBudgets(
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

            allBudgets.Add(
                property.Name,
                new BenchmarkBudget(
                    value
                        .GetProperty("baselineDurationMilliseconds")
                        .GetDouble(),
                    value
                        .GetProperty("regressionTolerancePercent")
                        .GetDouble(),
                    value
                        .GetProperty("maximumAllocatedBytes")
                        .GetInt64()));
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

    private static string ReadOutputPath(
        string[] arguments,
        string repositoryRoot,
        string defaultOutputFileName
    )
    {
        if (arguments.Length == 0)
        {
            return Path.Combine(repositoryRoot, "artifacts", "performance", defaultOutputFileName);
        }

        return arguments is ["--output", _]
            ? Path.GetFullPath(arguments[1], repositoryRoot)
            : throw new ArgumentException("Usage: benchmark [--output <path>]");
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

    private sealed record BenchmarkBudget(
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
