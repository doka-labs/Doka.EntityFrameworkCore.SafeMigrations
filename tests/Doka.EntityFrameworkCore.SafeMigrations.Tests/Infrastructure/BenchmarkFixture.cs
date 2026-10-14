namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Infrastructure;

/// <summary>Owns isolated benchmark files and shared non-test workload helpers.</summary>
internal sealed class BenchmarkFixture : IDisposable
{
    /// <summary>Ensures a positive elapsed sample without a machine-specific duration budget.</summary>
    /// <returns>A stable workload result.</returns>
    internal static int WaitForTimer()
    {
        System.Threading.Thread.Sleep(1);

        return 0;
    }

    /// <summary>Keeps the selected allocation observable to the benchmark measurement.</summary>
    /// <param name="size">The number of bytes to retain through the workload call.</param>
    /// <returns>The retained buffer length.</returns>
    internal static int AllocateBuffer(
        int size
    )
    {
        var buffer = new byte[size];

        // WHY: The forced allocation must remain observable even if the JIT can fold an array's length.
        GC.KeepAlive(buffer);

        return buffer.Length;
    }

    /// <summary>
    /// Gets the isolated file owned exclusively by this fixture.
    /// </summary>
    public string OutputPath { get; } = Path.Combine(Path.GetTempPath(), $"safe-migrations-{Guid.NewGuid():N}.json");

    /// <summary>
    /// Creates a single-workload runner with controlled comparison limits.
    /// </summary>
    /// <param name="reportOnly">Whether an exceeded budget remains informational.</param>
    /// <param name="durationLimit">The maximum measured workload duration in milliseconds.</param>
    /// <param name="allocationLimit">The maximum measured workload allocation in bytes.</param>
    /// <returns>A runner that writes only this fixture's owned output.</returns>
    public BenchmarkRunner CreateRunner(
        bool reportOnly,
        double durationLimit = double.MaxValue,
        long allocationLimit = long.MaxValue
    )
        => new(
            new Dictionary<string, BenchmarkRunner.BenchmarkBudget>(StringComparer.Ordinal)
            {
                ["synthetic"] = new(durationLimit, 0, allocationLimit)
            },
            OutputPath,
            reportOnly);

    /// <summary>
    /// Writes an isolated synthetic document without changing the repository's budgets.
    /// </summary>
    /// <param name="baseline">The synthetic JSON baseline duration value.</param>
    /// <param name="tolerance">The synthetic JSON regression tolerance value.</param>
    /// <param name="allocations">The synthetic JSON allocation limit value.</param>
    /// <param name="schemaVersion">The synthetic performance-budget schema version.</param>
    /// <param name="workloadNames">The synthetic JSON array of workload names.</param>
    public void WriteBudgetConfiguration(
        string baseline,
        string tolerance,
        string allocations,
        int schemaVersion = 2,
        string workloadNames = "[\"synthetic\"]"
    )
        => File.WriteAllText(OutputPath, $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "benchmarkSets": { "synthetic": {{workloadNames}} },
              "budgets": {
                "synthetic": {
                  "baselineDurationMilliseconds": {{baseline}},
                  "regressionTolerancePercent": {{tolerance}},
                  "maximumAllocatedBytes": {{allocations}}
                }
              }
            }
            """);

    /// <summary>
    /// Completes the real selected budget set with one deterministic allocation overrun.
    /// </summary>
    /// <param name="runner">The isolated runner receiving every Core workload sample.</param>
    public static void MeasureCoreWorkloads(
        BenchmarkRunner runner
    )
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        var repositoryRoot = directory?.FullName
            ?? throw new DirectoryNotFoundException("Unable to locate benchmark configuration.");

        using var budgets = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot,
            "eng", "performance-budgets.json")));

        foreach (var nameElement in budgets.RootElement.GetProperty("benchmarkSets").GetProperty("core")
                     .EnumerateArray())
        {
            var name = nameElement.GetString()!;

            if (name == "core_intent_construction_1")
            {
                var limit = budgets.RootElement.GetProperty("budgets").GetProperty(name)
                    .GetProperty("maximumAllocatedBytes").GetInt64();

                runner.Measure(name, () => AllocateBuffer(checked((int)limit + 4096)));
            }
            else
            {
                runner.Measure(name, static () => 0);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (File.Exists(OutputPath))
        {
            File.Delete(OutputPath);
        }

        if (Directory.Exists(OutputPath))
        {
            Directory.Delete(OutputPath);
        }
    }
}
