namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Measures paired live column paths on an explicitly supplied diagnostic engine.</summary>
/// <remarks>Native evidence is captured separately from BenchmarkDotNet timing and allocation reports.</remarks>
[MemoryDiagnoser]
public partial class SqlServerColumnAttribution : IDisposable
{
    private const int CommandTimeoutSeconds = 180;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private SqlConnection? _root;
    private SqlConnection? _connection;
    private AttributionContext? _context;
    private IMigrationsSqlGenerator? _generator;
    private ISafeMigrationProviderAnalyzer? _analyzer;
    private Scenario? _scenario;
    private string? _database;
    private string _output = string.Empty;
    private string _original = string.Empty;
    private string _sql = string.Empty;
    private List<SafeMigrationOperation> _operations = [];
    private IReadOnlyList<MigrationCommand> _commands = [];

    /// <summary>Gets or sets the exact live column fixture and expected classification contract.</summary>
    [Params(
        "matching_alter_1",
        "matching_ensure_1",
        "matching_alter_16",
        "matching_ensure_16",
        "matching_alter_64",
        "matching_ensure_64",
        "safe_repair_then_replay",
        "wrong_source_length",
        "unexpected_default",
        "unexpected_collation",
        "nullability_data_blocked",
        "caller_owned_index")]
    public string ScenarioName { get; set; } = "matching_alter_1";

    /// <summary>Gets or sets the actual fixture row count shown in the benchmark report.</summary>
    [ParamsSource(nameof(RowCounts))]
    public int RowCount { get; set; }

    /// <summary>Gets the configured row count, defaulting to 10000 and limited to 200000.</summary>
    public static IEnumerable<int> RowCounts => [ReadRowCount()];

    private SqlConnection Connection => _connection ?? throw new InvalidOperationException("Setup has not completed.");

    private AttributionContext Context => _context ?? throw new InvalidOperationException("Setup has not completed.");

    private IMigrationsSqlGenerator Generator =>
        _generator ?? throw new InvalidOperationException("Setup has not completed.");

    private ISafeMigrationProviderAnalyzer Analyzer =>
        _analyzer ?? throw new InvalidOperationException("Setup has not completed.");

    private Scenario ScenarioDefinition => _scenario ?? throw new InvalidOperationException("Setup has not completed.");

    /// <summary>Creates an owned database and captures fixture, safety and server evidence before timing.</summary>
    [GlobalSetup]
    public void Setup()
    {
        ValidateEnvironment();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RowCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RowCount, 200_000);
        _scenario = CreateScenarios()
            .Single(scenario => scenario.Name == ScenarioName);

        _output = CreateEvidenceDirectory("columns-" + ScenarioName + "-rows-" + RowCount);
        var settings =
            new SqlConnectionStringBuilder(
                ReadRequiredEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_CONNECTION"))
            {
                InitialCatalog = "master",
                Pooling = false,
                ApplicationName = "SafeMigrations.ColumnAttribution",
            };

        try
        {
            _root = new SqlConnection(settings.ConnectionString);
            _root.Open();
            var database = "sm_column_attribution_"
                + Guid
                    .NewGuid()
                    .ToString("N");

            Execute(_root, $"CREATE DATABASE [{database}];");
            _database = database;
            settings.InitialCatalog = database;
            _connection = new SqlConnection(settings.ConnectionString);
            _connection.Open();
            WriteProvenance(Connection, _output, RowCount);
            ProbeRepairBinding(Connection, _output);
            ProbeMatchingBinding(Connection, _output);

            var fixture = BuildFixture(ScenarioDefinition, RowCount);
            File.WriteAllText(Path.Combine(_output, "fixture.sql"), fixture);

            // WHY: SQL Server can bind INSERT to the pre-drop shape when DROP/CREATE share one batch.
            Execute(Connection, "DROP TABLE IF EXISTS dbo.attribution_items;");
            Execute(Connection, fixture);
            _context = new AttributionContext(settings.ConnectionString);
            _generator = Context.GetService<IMigrationsSqlGenerator>();
            _analyzer = Context.GetService<ISafeMigrationProviderAnalyzer>();
            _operations = CreateOperations(ScenarioDefinition);
            _commands = Generator.Generate(_operations, Context.Model);
            if (ScenarioName == "safe_repair_then_replay")
            {
                PrepareRepairReplay(
                    Connection,
                    Context,
                    Analyzer,
                    _operations,
                    _commands,
                    ScenarioDefinition,
                    _output);
            }

            _original = Snapshot(Connection, ScenarioDefinition);
            _sql = string.Join("\n", _commands.Select(static command => command.CommandText));
            File.WriteAllText(Path.Combine(_output, "runtime.sql"), _sql);
            CaptureDiagnosticEvidence();
            VerifyFixture();
        }
        catch
        {
            DisposeDatabase();

            throw;
        }
    }

    /// <summary>Measures complete provider SQL generation against the preconstructed live scenario.</summary>
    /// <returns>The number of generated migration commands.</returns>
    [Benchmark]
    public int Generate() => Generator.Generate(_operations, Context.Model).Count;

    /// <summary>Measures actual catalog analysis, including its database round trips.</summary>
    /// <returns>The number of provider analysis results.</returns>
    [Benchmark]
    public int Analyze() => Analyzer
        .AnalyzeAsync(Context, _operations)
        .GetAwaiter()
        .GetResult()
        .Count;

    /// <summary>Measures transactional guarded replay or the exact expected fail-closed provider refusal.</summary>
    /// <returns>The command count or expected SQL Server refusal number.</returns>
    [Benchmark]
    public int ExecuteRuntime() => ExecuteGenerated(Connection, _commands, ScenarioDefinition);

    /// <summary>Checks classification, deterministic SQL and unchanged rows/catalog outside timed iterations.</summary>
    [IterationCleanup]
    public void VerifyFixture()
    {
        ValidateAnalyses(
            Analyzer
                .AnalyzeAsync(Context, _operations)
                .GetAwaiter()
                .GetResult(),
            ScenarioDefinition);

        if (_original != Snapshot(Connection, ScenarioDefinition))
        {
            throw new InvalidOperationException($"{ScenarioName}: runtime changed fixture rows or metadata.");
        }

        var sql = string.Join(
            "\n",
            Generator
                .Generate(_operations, Context.Model)
                .Select(static command => command.CommandText));

        if (_sql != sql)
        {
            throw new InvalidOperationException($"{ScenarioName}: provider SQL generation was not deterministic.");
        }
    }

    /// <summary>Verifies the final fixture and drops only the generated diagnostic database.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            if (_original.Length > 0)
            {
                VerifyFixture();
            }
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Releases connections and removes only the owned generated diagnostic database.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases managed diagnostic resources when the benchmark owner is disposed.</summary>
    /// <param name="disposing">Whether managed resources are eligible for disposal.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            DisposeDatabase();
        }
    }

    /// <summary>Rejects missing explicit native configuration before creating a diagnostic database.</summary>
    internal static void ValidateEnvironment()
    {
        _ = ReadRequiredEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_CONNECTION");
        _ = ReadRequiredEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT");
        _ = ReadRowCount();
    }

    /// <summary>Creates a unique evidence directory for one isolated BenchmarkDotNet setup.</summary>
    /// <param name="label">The nonsecret fixture or microcontrol identity.</param>
    /// <returns>The full path of the newly created evidence directory.</returns>
    internal static string CreateEvidenceDirectory(
        string label
    )
    {
        var root = Path.GetFullPath(ReadRequiredEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT"));
        var output = Path.Combine(
            root,
            label
            + "-"
            + Guid
                .NewGuid()
                .ToString("N"));

        Directory.CreateDirectory(output);

        return output;
    }

    private static string ReadRequiredEnvironmentVariable(
        string name
    )
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Native attribution requires the {name} environment variable.");
        }

        return value;
    }

    private static int ReadRowCount()
    {
        var configured = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_ROWS");
        var rows = string.IsNullOrWhiteSpace(configured)
            ? 10_000
            : int.Parse(configured, CultureInfo.InvariantCulture);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, 200_000);

        return rows;
    }

    private void DisposeDatabase()
    {
        _context?.Dispose();
        _context = null;
        _generator = null;
        _analyzer = null;
        _original = string.Empty;
        _connection?.Dispose();
        _connection = null;
        try
        {
            if (_database is not null
                && _root is not null)
            {
                // WHY: Only this generated database is eligible; caller databases are never reset.
                Execute(
                    _root,
                    $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
                    + $"DROP DATABASE [{_database}];");
                _database = null;
            }
        }
        finally
        {
            _root?.Dispose();
            _root = null;
        }
    }

    private void CaptureDiagnosticEvidence()
    {
        Context.Database.OpenConnection();
        var catalogConnection = (SqlConnection)Context.Database.GetDbConnection();
        var statistics = new List<string>();

        Connection.InfoMessage += Capture;
        catalogConnection.InfoMessage += Capture;
        try
        {
            // WHY: Statistics and artifact I/O are diagnostic controls, never benchmark instrumentation.
            ValidateAnalyses(
                Analyzer
                    .AnalyzeAsync(Context, _operations)
                    .GetAwaiter()
                    .GetResult(),
                ScenarioDefinition);

            Execute(Connection, "SET STATISTICS IO ON; SET STATISTICS TIME ON;");
            Execute(catalogConnection, "SET STATISTICS IO ON; SET STATISTICS TIME ON;");
            statistics.Clear();
            var analyses = Analyzer
                .AnalyzeAsync(Context, _operations)
                .GetAwaiter()
                .GetResult();

            ValidateAnalyses(analyses, ScenarioDefinition);
            File.WriteAllLines(Path.Combine(_output, "catalog-statistics.log"), statistics);
            WriteJson(Path.Combine(_output, "catalog-results.json"), analyses);
            ExportCatalogSql(catalogConnection, Path.Combine(_output, "catalog-cached-sql.json"));
            statistics.Clear();
            var runtimeResult = ExecuteRuntime();
            File.WriteAllLines(Path.Combine(_output, "runtime-statistics.log"), statistics);
            WriteJson(
                Path.Combine(_output, "scenario.json"),
                new
                {
                    Name = ScenarioName,
                    RowCount,
                    OperationCount = _operations.Count,
                    ExpectedState = ScenarioDefinition.ExpectedState.ToString(),
                    ScenarioDefinition.RejectRuntime,
                    RuntimeResult = runtimeResult,
                    FixtureFingerprint = _original,
                    GeneratedSqlCharacters = _sql.Length,
                    GeneratedSqlUtf8Bytes = Encoding.UTF8.GetByteCount(_sql),
                    SysColumnsSubqueryOccurrences = CountOccurrences(_sql, "FROM sys.columns"),
                    TimingEvidence = "BenchmarkDotNet artifacts; diagnostics contain no timing samples.",
                });
        }
        finally
        {
            Connection.InfoMessage -= Capture;
            catalogConnection.InfoMessage -= Capture;
            Execute(Connection, "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;");
            Execute(catalogConnection, "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;");
        }

        return;

        void Capture(
            object sender,
            SqlInfoMessageEventArgs message
        ) => statistics.Add(message.Message);
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
            throw new InvalidOperationException(
                $"{scenario.Name}: unexpected analyzer state: " + JsonSerializer.Serialize(analyses, s_jsonOptions));
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
            && exception.Number
            == (scenario.ExpectedState == SafeMigrationObservedState.DataBlocked
                ? 51003
                : 51001))
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
            SchemaVersion = 2,
            Status = "measurement_only_not_engine_qualification",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            TimingFramework = "BenchmarkDotNet",
            DiagnosticExecutionsPerPath = 1,
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
            AllocationScope = "BenchmarkDotNet MemoryDiagnoser; not product-only async SqlClient attribution.",
        };

        WriteJson(Path.Combine(output, "provenance.json"), metadata);
    }

    /// <summary>Writes indented raw evidence through one shared serializer configuration.</summary>
    internal static void WriteJson<T>(
        string path,
        T value
    ) => File.WriteAllText(path, JsonSerializer.Serialize(value, s_jsonOptions));

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
    internal static int CountOccurrences(
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
