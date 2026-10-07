namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Collects paired live column evidence on an explicitly supplied diagnostic engine.</summary>
internal static partial class SqlServerColumnAttribution
{
    private const int Samples = 5;
    private const int CommandTimeoutSeconds = 180;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>Runs isolated fixtures with raw samples, catalog results, SQL and server statistics.</summary>
    /// <param name="arguments">The mode, root connection, output directory and optional row count.</param>
    /// <returns>Zero only after every safety invariant and artifact write succeeds.</returns>
    internal static int Run(
        string[] arguments
    )
    {
        if (arguments.Length is not (3 or 4)
            || arguments[0] != "--column-attribution")
        {
            throw new ArgumentException(
                "Usage: --column-attribution <connectionString> <outputDirectory> [rowCount].",
                nameof(arguments));
        }

        var rows = arguments.Length == 4
            ? int.Parse(arguments[3], CultureInfo.InvariantCulture)
            : 10_000;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, 200_000);
        var output = Path.GetFullPath(arguments[2]);
        Directory.CreateDirectory(output);
        if (Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw new ArgumentException("The attribution output directory must be empty.", nameof(arguments));
        }

        var settings = new SqlConnectionStringBuilder(arguments[1])
        {
            InitialCatalog = "master",
            Pooling = false,
            ApplicationName = "SafeMigrations.ColumnAttribution",
        };

        using var root = new SqlConnection(settings.ConnectionString);
        root.Open();
        var database = "sm_column_attribution_" + Guid.NewGuid().ToString("N");
        Execute(root, $"CREATE DATABASE [{database}];");
        var results = new List<ScenarioResult>();
        try
        {
            settings.InitialCatalog = database;
            using var connection = new SqlConnection(settings.ConnectionString);
            connection.Open();
            WriteProvenance(connection, output, rows);
            MeasureGuardGeneration(output);
            ProbeRepairBinding(connection, output);
            ProbeMatchingBinding(connection, output);
            foreach (var scenario in CreateScenarios())
            {
                results.Add(MeasureScenario(connection, settings.ConnectionString, output, rows, scenario));
            }

            WriteJson(Path.Combine(output, "results.json"), results);
        }
        finally
        {
            // WHY: Only this generated database is eligible for cleanup; caller databases are never reset.
            Execute(root, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
                + $"DROP DATABASE [{database}];");
        }

        Console.WriteLine(
            $"SQL Server column attribution completed: {results.Count} scenarios, {Samples} raw samples.");
        Console.WriteLine("Measurement only; emulated executions do not qualify a native SQL Server engine.");

        return 0;
    }

    /// <summary>Measures generation, actual provider analysis and execution against the same fixture.</summary>
    private static ScenarioResult MeasureScenario(
        SqlConnection connection,
        string connectionString,
        string output,
        int rows,
        Scenario scenario
    )
    {
        var scenarioOutput = Path.Combine(output, scenario.Name);
        Directory.CreateDirectory(scenarioOutput);
        var fixture = BuildFixture(scenario, rows);
        File.WriteAllText(Path.Combine(scenarioOutput, "fixture.sql"), fixture);
        // WHY: SQL Server can bind INSERT to the pre-drop shape when DROP/CREATE share one batch.
        Execute(connection, "DROP TABLE IF EXISTS dbo.attribution_items;");
        Execute(connection, fixture);
        using var context = new AttributionContext(connectionString);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = CreateOperations(scenario);
        var commands = generator.Generate(operations, context.Model);
        if (scenario.Name == "safe_repair_then_replay")
        {
            PrepareRepairReplay(connection, context, analyzer, operations, commands, scenario, scenarioOutput);
        }

        var original = Snapshot(connection, scenario);
        var sql = string.Join("\n", commands.Select(static command => command.CommandText));
        File.WriteAllText(Path.Combine(scenarioOutput, "runtime.sql"), sql);
        var generatedBytes = Encoding.UTF8.GetByteCount(sql);
        var generation = new List<Sample>(Samples);
        var catalog = new List<Sample>(Samples);
        var runtime = new List<Sample>(Samples);
        var statistics = new List<string>();

        void Capture(
            object sender,
            SqlInfoMessageEventArgs message
        ) => statistics.Add(message.Message);

        connection.InfoMessage += Capture;
        context.Database.OpenConnection();
        var catalogConnection = (SqlConnection)context.Database.GetDbConnection();
        catalogConnection.InfoMessage += Capture;
        Execute(connection, "SET STATISTICS IO ON; SET STATISTICS TIME ON;");
        Execute(catalogConnection, "SET STATISTICS IO ON; SET STATISTICS TIME ON;");
        try
        {
            // WHY: JIT and SqlClient initialization are warmed separately; five raw samples remain unsorted.
            ValidateAnalyses(analyzer.AnalyzeAsync(context, operations).GetAwaiter().GetResult(), scenario);
            for (var index = 0; index < Samples; index++)
            {
                generation.Add(Measure(() =>
                {
                    commands = generator.Generate(operations, context.Model);
                    return commands.Count;
                }, threadLocal: true));

                statistics.Clear();
                IReadOnlyList<SafeMigrationProviderAnalysis>? analyses = null;
                catalog.Add(Measure(() =>
                {
                    analyses = analyzer.AnalyzeAsync(context, operations).GetAwaiter().GetResult();
                    return analyses.Count;
                }, threadLocal: false));

                ValidateAnalyses(analyses!, scenario);
                File.WriteAllLines(Path.Combine(scenarioOutput, $"catalog-statistics-{index}.log"), statistics);
                WriteJson(Path.Combine(scenarioOutput, $"catalog-results-{index}.json"), analyses);
                ExportCatalogSql(catalogConnection, Path.Combine(scenarioOutput, $"catalog-cached-sql-{index}.json"));
                statistics.Clear();
                runtime.Add(Measure(() => ExecuteGenerated(connection, commands, scenario), threadLocal: true));
                File.WriteAllLines(Path.Combine(scenarioOutput, $"runtime-statistics-{index}.log"), statistics);
                var after = Snapshot(connection, scenario);
                if (original != after)
                {
                    throw new InvalidOperationException($"{scenario.Name}: runtime changed fixture rows or metadata.");
                }
            }
        }
        finally
        {
            connection.InfoMessage -= Capture;
            catalogConnection.InfoMessage -= Capture;
            Execute(connection, "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;");
            Execute(catalogConnection, "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;");
        }

        var result = new ScenarioResult(scenario.Name, rows, operations.Count, scenario.ExpectedState.ToString(),
            original, sql.Length, generatedBytes, CountOccurrences(sql, "FROM sys.columns"),
            generation, catalog, runtime);

        WriteJson(Path.Combine(scenarioOutput, "measurement.json"), result);
        Console.WriteLine($"{scenario.Name}: verified {operations.Count} classifications, data/catalog unchanged.");

        return result;
    }

    /// <summary>Records finite raw samples without enforcing timing or allocation limits.</summary>
    private static Sample Measure(
        Func<int> action,
        bool threadLocal
    )
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = threadLocal ? GC.GetAllocatedBytesForCurrentThread() : GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var count = action();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocated = (threadLocal ? GC.GetAllocatedBytesForCurrentThread()
            : GC.GetTotalAllocatedBytes(precise: true)) - before;

        return new Sample(elapsed, allocated, count,
            threadLocal ? "current_thread" : "process_including_async_sqlclient");
    }

    /// <summary>Requires exact state, postcondition and fail-closed repair capability for every classifier.</summary>
    private static void ValidateAnalyses(
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses,
        Scenario scenario
    )
    {
        if (analyses.Count != scenario.Columns
            || analyses.Any(analysis => analysis.ObservedState != scenario.ExpectedState
                || analysis.PostconditionSatisfied != (scenario.ExpectedState == SafeMigrationObservedState.Matching)
                || scenario.RejectRuntime && analysis.RepairCapability == SafeMigrationRepairCapability.Safe))
        {
            throw new InvalidOperationException($"{scenario.Name}: unexpected analyzer state: "
                + JsonSerializer.Serialize(analyses, s_jsonOptions));
        }
    }

    /// <summary>Executes generated batches transactionally and accepts only the expected provider refusal.</summary>
    private static int ExecuteGenerated(
        SqlConnection connection,
        IReadOnlyList<MigrationCommand> commands,
        Scenario scenario
    )
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            foreach (var migrationCommand in commands)
            {
                using var command = connection.CreateCommand();
                command.CommandTimeout = CommandTimeoutSeconds;
                command.CommandText = migrationCommand.CommandText;
                command.Transaction = transaction;
                command.ExecuteNonQuery();
            }

            if (scenario.RejectRuntime)
            {
                throw new InvalidOperationException($"{scenario.Name}: unsafe runtime unexpectedly succeeded.");
            }

            transaction.Commit();

            return commands.Count;
        }
        catch (SqlException exception) when (scenario.RejectRuntime
            && exception.Number == (scenario.ExpectedState == SafeMigrationObservedState.DataBlocked ? 51003 : 51001))
        {
            transaction.Rollback();

            return exception.Number;
        }
    }

    /// <summary>Exports actual cached classifier batches without overlapping-statistics aggregation.</summary>
    private static void ExportCatalogSql(
        SqlConnection connection,
        string output
    )
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = "SELECT TOP (128) cp.usecounts, cp.objtype, st.text "
            + "FROM sys.dm_exec_cached_plans cp CROSS APPLY sys.dm_exec_sql_text(cp.plan_handle) st "
            + "CROSS APPLY sys.dm_exec_plan_attributes(cp.plan_handle) pa "
            + "WHERE pa.attribute = N'dbid' AND CONVERT(int, pa.value) = DB_ID() "
            + "AND st.text LIKE N'%@doka_ordinal%' AND st.text NOT LIKE N'%sys.dm_exec_cached_plans%' "
            + "ORDER BY cp.usecounts DESC;";

        using var reader = command.ExecuteReader();
        var entries = new List<object[]>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            entries.Add(values);
        }

        WriteJson(output, entries);
    }

    /// <summary>Captures engine and host provenance without saving connection settings or credentials.</summary>
    private static void WriteProvenance(
        SqlConnection connection,
        string output,
        int rows
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@VERSION, CONVERT(nvarchar(128), SERVERPROPERTY('Edition')), "
            + "cpu_count, physical_memory_kb FROM sys.dm_os_sys_info;";
        command.CommandTimeout = CommandTimeoutSeconds;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("The server returned no attribution provenance.");
        }

        var product = typeof(SafeMigrationOperation).Assembly.Location;
        var provider = typeof(SqlServerSafeMigrationRuntimePlan).Assembly.Location;
        var harness = typeof(SqlServerColumnAttribution).Assembly.Location;
        var metadata = new
        {
            SchemaVersion = 1,
            Status = "measurement_only_not_engine_qualification",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            SampleCount = Samples,
            RowCount = rows,
            Runtime = Environment.Version.ToString(),
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            HostProcessorCount = Environment.ProcessorCount,
            ServerVersion = reader.GetString(0),
            ServerEdition = reader.GetString(1),
            ServerCpuCount = reader.GetInt32(2),
            ServerPhysicalMemoryKilobytes = reader.GetInt64(3),
            ProductSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(product))),
            ProviderSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(provider))),
            HarnessSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(harness))),
            Statistics = "Raw nested STATISTICS IO/TIME messages; do not sum overlapping scopes.",
            CatalogAllocationScope = "Process including async SqlClient; not thread-local product-only allocations.",
        };

        WriteJson(Path.Combine(output, "provenance.json"), metadata);
    }

    /// <summary>Writes indented raw evidence through one shared serializer configuration.</summary>
    private static void WriteJson<T>(
        string path,
        T value
    )
        => File.WriteAllText(path, JsonSerializer.Serialize(value, s_jsonOptions));

    /// <summary>Runs fixture and diagnostic SQL with an explicit finite server budget.</summary>
    private static void Execute(
        SqlConnection connection,
        string sql
    )
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Counts repeated SQL structure without changing generated batches.</summary>
    private static int CountOccurrences(
        string source,
        string token
    )
    {
        var count = 0;
        var position = 0;
        while ((position = source.IndexOf(token, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += token.Length;
        }

        return count;
    }

    /// <summary>Retains one unaggregated duration, allocation result and explicit allocation scope.</summary>
    private sealed record Sample(
        double DurationMilliseconds,
        long AllocatedBytes,
        int Result,
        string AllocationScope
    );

    /// <summary>Pairs fixture identity and generated SQL structure with the three measured execution paths.</summary>
    private sealed record ScenarioResult(
        string Name,
        int RowCount,
        int OperationCount,
        string ExpectedState,
        string FixtureFingerprint,
        int GeneratedSqlCharacters,
        int GeneratedSqlUtf8Bytes,
        int SysColumnsSubqueryOccurrences,
        IReadOnlyList<Sample> GenerationSamples,
        IReadOnlyList<Sample> CatalogSamples,
        IReadOnlyList<Sample> RuntimeSamples
    );

    /// <summary>Configures the real product generator and analyzer without package/public API changes.</summary>
    private sealed class AttributionContext(
        string connectionString
    ) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            optionsBuilder.UseSqlServer(connectionString, sql => sql.CommandTimeout(CommandTimeoutSeconds));
            optionsBuilder.UseSqlServerSafeMigrations<AttributionContext>();
        }
    }
}
