var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var budgetPath = Path.Combine(repositoryRoot, "eng", "performance-budgets.json");
var outputPath = ReadOutputPath(args, repositoryRoot);
var budgets = ReadBudgets(budgetPath);

using var mySqlContext = new MySqlGenerationContext();
using var postgreSqlContext = new PostgreSqlGenerationContext();
var mySqlGenerator = mySqlContext.GetService<IMigrationsSqlGenerator>();
var postgreSqlGenerator = postgreSqlContext.GetService<IMigrationsSqlGenerator>();
var results = new List<BenchmarkResult>();

foreach (var size in new[] { 1, 100, 1000 })
{
    var operations = ColumnBenchmarkWorkload.CreateOperations(size);
    var report = ColumnBenchmarkWorkload.CreateReport(size);
    results.Add(
        Measure(
            $"core_intent_construction_{size}",
            budgets,
            () => ColumnBenchmarkWorkload.CreateOperations(size)
                .Count));
    results.Add(Measure($"decision_planner_{size}", budgets, () => ColumnBenchmarkWorkload.RunPlanner(size)));
    results.Add(
        Measure(
            $"mysql_generation_{size}",
            budgets,
            () => mySqlGenerator.Generate(operations, mySqlContext.Model)
                .Count));
    results.Add(
        Measure(
            $"postgresql_generation_{size}",
            budgets,
            () => postgreSqlGenerator.Generate(operations, postgreSqlContext.Model)
                .Count));
    results.Add(
        Measure(
            $"report_json_{size}",
            budgets,
            () => SafeMigrationReportJson.SerializeToUtf8Bytes(report)
                .Length));
}

WriteResults(outputPath, results);
foreach (var result in results)
{
    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"{result.Name}: {result.DurationMilliseconds:F3} ms, "
            + $"{result.AllocatedBytes} bytes, {(result.Passed ? "PASS" : "FAIL")}"));
}

return results.All(static result => result.Passed) ? 0 : 1;

static BenchmarkResult Measure(
    string name,
    IReadOnlyDictionary<string, BenchmarkBudget> budgets,
    Func<int> action
)
{
    const int sampleCount = 5;
    if (!budgets.TryGetValue(name, out var budget))
    {
        throw new InvalidOperationException($"No performance budget exists for '{name}'.");
    }

    _ = action();
    var durations = new double[sampleCount];
    var allocations = new long[sampleCount];
    for (var sample = 0; sample < sampleCount; sample++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var result = action();
        durations[sample] = Stopwatch.GetElapsedTime(started)
            .TotalMilliseconds;
        allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        GC.KeepAlive(result);
    }

    Array.Sort(durations);
    Array.Sort(allocations);
    var duration = durations[sampleCount / 2];
    var allocated = allocations[sampleCount / 2];
    var maximumDuration = budget.BaselineDurationMilliseconds * (1d + (budget.RegressionTolerancePercent / 100d));
    return new BenchmarkResult(
        name,
        duration,
        allocated,
        maximumDuration,
        budget.MaximumAllocatedBytes,
        duration <= maximumDuration && allocated <= budget.MaximumAllocatedBytes);
}

static IReadOnlyDictionary<string, BenchmarkBudget> ReadBudgets(
    string path
)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    var result = new Dictionary<string, BenchmarkBudget>(StringComparer.Ordinal);
    foreach (var property in document
                 .RootElement.GetProperty("budgets")
                 .EnumerateObject())
    {
        var value = property.Value;
        result.Add(
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

    return result;
}

static void WriteResults(
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

static string ReadOutputPath(
    string[] arguments,
    string repositoryRoot
)
{
    if (arguments.Length == 0)
    {
        return Path.Combine(repositoryRoot, "artifacts", "performance", "results.json");
    }

    return arguments is ["--output", _]
        ? Path.GetFullPath(arguments[1], repositoryRoot)
        : throw new ArgumentException("Usage: benchmark [--output <path>]");
}

static string FindRepositoryRoot(
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

internal sealed record BenchmarkBudget(
    double BaselineDurationMilliseconds,
    double RegressionTolerancePercent,
    long MaximumAllocatedBytes
);

internal sealed record BenchmarkResult(
    string Name,
    double DurationMilliseconds,
    long AllocatedBytes,
    double MaximumDurationMilliseconds,
    long MaximumAllocatedBytes,
    bool Passed
);

internal sealed class MySqlGenerationContext : DbContext
{
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseMySql(
            "Server=127.0.0.1;Port=1;User ID=benchmark;Password=benchmark;Database=benchmark",
            MySqlServerVersion.MySql(new Version(8, 4, 11)));
        optionsBuilder.UseMySqlSafeMigrations();
    }
}

internal sealed class PostgreSqlGenerationContext : DbContext
{
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseNpgsql("Host=127.0.0.1;Port=1;Username=benchmark;Password=benchmark;Database=benchmark");
        optionsBuilder.UsePostgreSqlSafeMigrations();
    }
}
