using TypeFilter = BenchmarkDotNet.Running.TypeFilter;

namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Runs native BenchmarkDotNet measurements without hardware-sensitive pass/fail budgets.</summary>
internal static class BenchmarkProgram
{
    /// <summary>Runs the selected workload family or a native discovery/help command.</summary>
    /// <param name="args">BenchmarkDotNet arguments, including optional filters, jobs and artifact directory.</param>
    /// <param name="benchmarkTypes">The isolated workload types available in this executable mode.</param>
    /// <returns>
    /// Zero for valid discovery or complete execution/export; one for configuration or execution failure.
    /// </returns>
    internal static int Run(
        string[] args,
        params Type[] benchmarkTypes
    )
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(benchmarkTypes);

        if (benchmarkTypes.Length == 0)
        {
            throw new ArgumentException("At least one benchmark type is required.", nameof(benchmarkTypes));
        }

        var config = ManualConfig
            .Create(DefaultConfig.Instance)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .WithOption(ConfigOptions.StopOnFirstError, true);

        // WHY: Explicit selection prevents BenchmarkDotNet from opening an interactive prompt in CI.
        var arguments = args.Length == 0
            ? ["--filter", "*"]
            : args;

        var switcher = BenchmarkSwitcher.FromTypes(benchmarkTypes);
        var attributionOutput = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT");
        var sqlServerOutput = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT");

        try
        {
            // WHY: The native parser reports help as an unsuccessful parse. Accept only its standalone
            // terminal forms so an invalid measurement option cannot hide behind a help/list request.
            if (arguments is ["--help"] or ["-h"] or ["--version"])
            {
                _ = switcher.Run(arguments, config);

                return 0;
            }

            var (parsed, parsedConfig, options) = ConfigParser.Parse(arguments, ConsoleLogger.Default, config);
            if (!parsed)
            {
                return 1;
            }

            if (!IsSafeInvocation(options))
            {
                Console.Error.WriteLine(
                    "Resume, environment-bearing build logs and attribution --envVars overrides are not supported.");

                return 1;
            }

            // WHY: BDN children run inside disposable generated directories. Relative evidence paths
            // must be bound to the invoking directory before those child processes are launched.
            NormalizeOutputDirectory("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT", attributionOutput);
            NormalizeOutputDirectory("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT", sqlServerOutput);
            if (options.PrintInformation
                || options.ListBenchmarkCaseMode != ListBenchmarkCaseMode.Disabled)
            {
                _ = switcher.Run(arguments, config);

                return 0;
            }

            // WHY: Bind already-expanded native jobs rather than adding a default or mutator job;
            // both can override CLI Dry/Short/runtime/in-process selections before measurement.
            var boundConfig = new SourceBoundConfig(config, parsedConfig);
            var (validTypes, runnableTypes) = TypeFilter.GetTypesWithRunnableBenchmarks(
                benchmarkTypes,
                [],
                ConsoleLogger.Default);

            if (!validTypes)
            {
                return 1;
            }

            var expectedCases = TypeFilter
                .Filter(boundConfig, runnableTypes)
                .Sum(static info => info.BenchmarksCases.Length);

            var summaries = switcher
                .RunAll(boundConfig)
                .ToArray();

            if (!HasCompleteExecution(summaries, expectedCases))
            {
                Console.Error.WriteLine("BenchmarkDotNet did not complete every selected benchmark successfully.");

                return 1;
            }

            foreach (var summary in summaries)
            {
                // WHY: BDN's composite exporter logs and swallows export failures. Calling its native
                // full exporter here keeps unwritable/missing evidence fatal without another JSON schema.
                var paths = JsonExporter
                    .Full
                    .ExportToFiles(summary, ConsoleLogger.Default)
                    .ToArray();

                if (paths.Length != 1)
                {
                    throw new InvalidDataException("BenchmarkDotNet did not export exactly one full JSON report.");
                }

                ValidateExport(paths[0], summary.Reports.Length);
            }

            // WHY: Duration, allocation, ratios and statistical warnings are evidence, never CI verdicts.
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Benchmark execution or evidence failed ({exception.GetType().Name}).");

            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT", attributionOutput);
            Environment.SetEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT", sqlServerOutput);
        }
    }

    /// <summary>Rejects reuse of old measurements and build options that can disclose inherited credentials.</summary>
    /// <param name="options">The options validated by BenchmarkDotNet's own parser.</param>
    /// <returns>
    /// Whether execution can retain complete current-run evidence without environment-bearing build logs.
    /// </returns>
    internal static bool IsSafeInvocation(
        CommandLineOptions options
    ) => options is { Resume: false, LogBuildOutput: false, GenerateMSBuildBinLog: false }

        // WHY: BDN job variables override inherited values and are displayed in reports. Keep owned
        // output paths parent-bound and the native connection out of command lines and report parameters.
        && !options.EnvironmentVariables.Any(static variable =>
            variable.StartsWith("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT:", StringComparison.OrdinalIgnoreCase)
            || variable.StartsWith("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT:", StringComparison.OrdinalIgnoreCase)
            || variable.StartsWith(
                "SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_CONNECTION:",
                StringComparison.OrdinalIgnoreCase));

    /// <summary>Binds an optional functional-evidence directory to the caller rather than a generated child.</summary>
    /// <param name="name">The single documented output environment variable.</param>
    /// <param name="value">The original caller-owned value, restored when the invocation finishes.</param>
    private static void NormalizeOutputDirectory(
        string name,
        string? value
    )
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Environment.SetEnvironmentVariable(name, Path.GetFullPath(value));
        }
    }

    /// <summary>
    /// Requires nonempty summaries and successful build/child execution for every reported benchmark.
    /// </summary>
    /// <param name="summaries">The summaries returned by BenchmarkDotNet for this invocation.</param>
    /// <param name="expectedCases">
    /// The independent native filtered case count, when validating a full invocation.
    /// </param>
    /// <returns>Whether every selected case has successful execution evidence.</returns>
    internal static bool HasCompleteExecution(
        IReadOnlyList<Summary> summaries,
        int? expectedCases = null
    ) => summaries.Count > 0
        && (!expectedCases.HasValue || summaries.Sum(static summary => summary.Reports.Length) == expectedCases)
        && summaries.All(static summary => summary is { HasCriticalValidationErrors: false, Reports.Length: > 0 }
            && summary.Reports.All(static report =>
                report is { Success: true, ResultStatistics: not null, ExecuteResults.Count: > 0 }
                && report.ExecuteResults.All(static result => result.ExitCode == 0)));

    /// <summary>Checks native report completeness without interpreting any measured performance value.</summary>
    /// <param name="path">The actual path returned by BenchmarkDotNet's full JSON exporter.</param>
    /// <param name="expectedCases">The number of successfully executed cases in the summary.</param>
    internal static void ValidateExport(
        string path,
        int expectedCases
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCases);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var benchmarks = document.RootElement.GetProperty("Benchmarks");
        if (benchmarks.ValueKind != JsonValueKind.Array
            || benchmarks.GetArrayLength() != expectedCases)
        {
            throw new InvalidDataException("The BenchmarkDotNet full report is incomplete.");
        }

        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
        foreach (var benchmark in benchmarks.EnumerateArray())
        {
            if (benchmark.GetProperty("Statistics")
                    .ValueKind
                != JsonValueKind.Object
                || benchmark
                    .GetProperty("Measurements")
                    .GetArrayLength()
                == 0)
            {
                throw new InvalidDataException("The BenchmarkDotNet full report lacks measurement evidence.");
            }
        }
    }
}
