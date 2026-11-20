namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Measures paired live column paths on an explicitly supplied diagnostic engine.</summary>
/// <remarks>Native evidence is captured separately from BenchmarkDotNet timing and allocation reports.</remarks>
[MemoryDiagnoser]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public partial class SqlServerColumnAttribution : IDisposable, IAsyncDisposable
{
    private const int CommandTimeoutSeconds = 180;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private ISafeMigrationProviderAnalyzer? _analyzer;
    private IReadOnlyList<MigrationCommand> _commands = [];
    private SqlConnection? _connection;
    private AttributionContext? _context;
    private string? _database;
    private IMigrationsSqlGenerator? _generator;
    private List<SafeMigrationOperation> _operations = [];
    private string _original = string.Empty;
    private string _output = string.Empty;

    private SqlConnection? _root;
    private Scenario? _scenario;
    private string _sql = string.Empty;

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

    private IMigrationsSqlGenerator Generator
        => _generator ?? throw new InvalidOperationException("Setup has not completed.");

    private ISafeMigrationProviderAnalyzer Analyzer
        => _analyzer ?? throw new InvalidOperationException("Setup has not completed.");

    private Scenario ScenarioDefinition => _scenario ?? throw new InvalidOperationException("Setup has not completed.");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Exception? originalFailure = null;
        try
        {
            if (_original.Length > 0)
            {
                await VerifyFixture();
            }
        }
        catch (Exception exception)
        {
            originalFailure = exception;

            throw;
        }
        finally
        {
            try
            {
                await DisposeDatabaseAsync();
            }
            catch (Exception cleanupFailure) when (originalFailure is not null)
            {
                SafeMigrationAnalysisCleanup.RetainFailure(
                    originalFailure,
                    cleanupFailure,
                    "SafeMigrations.BenchmarkFinalCleanupException");
            }

            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Releases connections and removes only the owned generated diagnostic database.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates an owned database and captures fixture, safety and server evidence before timing.</summary>
    /// <returns>The asynchronous fixture setup and evidence capture.</returns>
    [GlobalSetup]
    public async Task Setup()
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
            await _root.OpenAsync(CancellationToken.None);
            var database = "sm_column_attribution_"
                + Guid
                    .NewGuid()
                    .ToString("N");

            await ExecuteAsync(_root, $"CREATE DATABASE [{database}];", CancellationToken.None);
            _database = database;
            settings.InitialCatalog = database;
            _connection = new SqlConnection(settings.ConnectionString);
            await _connection.OpenAsync(CancellationToken.None);
            await WriteProvenanceAsync(Connection, _output, RowCount, CancellationToken.None);
            await ProbeRepairBindingAsync(Connection, _output, CancellationToken.None);
            await ProbeMatchingBindingAsync(Connection, _output, CancellationToken.None);

            var fixture = BuildFixture(ScenarioDefinition, RowCount);
            await File.WriteAllTextAsync(Path.Combine(_output, "fixture.sql"), fixture, CancellationToken.None);

            // WHY: SQL Server can bind INSERT to the pre-drop shape when DROP/CREATE share one batch.
            await ExecuteAsync(Connection, "DROP TABLE IF EXISTS dbo.attribution_items;", CancellationToken.None);
            await ExecuteAsync(Connection, fixture, CancellationToken.None);
            _context = new AttributionContext(settings.ConnectionString);
            _generator = Context.GetService<IMigrationsSqlGenerator>();
            _analyzer = Context.GetService<ISafeMigrationProviderAnalyzer>();
            _operations = CreateOperations(ScenarioDefinition);
            _commands = Generator.Generate(_operations, Context.Model);
            if (ScenarioName == "safe_repair_then_replay")
            {
                await PrepareRepairReplayAsync(
                    Connection,
                    Context,
                    Analyzer,
                    _operations,
                    _commands,
                    ScenarioDefinition,
                    _output,
                    CancellationToken.None);
            }

            _original = await SnapshotAsync(Connection, ScenarioDefinition, CancellationToken.None);
            _sql = string.Join("\n", _commands.Select(static command => command.CommandText));
            await File.WriteAllTextAsync(Path.Combine(_output, "runtime.sql"), _sql, CancellationToken.None);
            await CaptureDiagnosticEvidenceAsync(CancellationToken.None);
            await VerifyFixture();
        }
        catch (Exception exception)
        {
            try
            {
                await DisposeDatabaseAsync();
            }
            catch (Exception cleanupFailure)
            {
                SafeMigrationAnalysisCleanup.RetainFailure(
                    exception,
                    cleanupFailure,
                    "SafeMigrations.BenchmarkSetupCleanupException");
            }

            throw;
        }
    }

    /// <summary>Measures complete provider SQL generation against the preconstructed live scenario.</summary>
    /// <returns>The number of generated migration commands.</returns>
    [Benchmark]
    public int Generate() => Generator.Generate(_operations, Context.Model)
        .Count;

    /// <summary>Measures actual catalog analysis, including its database round trips.</summary>
    /// <returns>The number of provider analysis results.</returns>
    [Benchmark]
    public async Task<int> Analyze()
    {
        var analyses = await Analyzer.AnalyzeAsync(Context, _operations, CancellationToken.None);

        return analyses.Count;
    }

    /// <summary>Measures transactional guarded replay or the exact expected fail-closed provider refusal.</summary>
    /// <returns>The command count or expected SQL Server refusal number.</returns>
    [Benchmark]
    public int ExecuteRuntime() => ExecuteGenerated(Connection, _commands, ScenarioDefinition);

    /// <summary>Checks classification, deterministic SQL and unchanged rows/catalog during setup and final cleanup.</summary>
    /// <returns>The asynchronous functional verification outside measured work.</returns>
    public async Task VerifyFixture()
    {
        ValidateAnalyses(
            await Analyzer.AnalyzeAsync(Context, _operations, CancellationToken.None),
            ScenarioDefinition);

        if (_original != await SnapshotAsync(Connection, ScenarioDefinition, CancellationToken.None))
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
    /// <returns>The final asynchronous verification and noncancellable cleanup.</returns>
    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    /// <summary>Configures the real product generator and analyzer without package/public API changes.</summary>
    private sealed class AttributionContext : DbContext
    {
        private readonly string _connectionString;

        /// <summary>Creates a context targeting the owned attribution database.</summary>
        /// <param name="connectionString">The owned database connection.</param>
        internal AttributionContext(
            string connectionString
        )
        {
            _connectionString = connectionString;
        }

        /// <inheritdoc />
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            optionsBuilder.UseSqlServer(_connectionString, sql => sql.CommandTimeout(CommandTimeoutSeconds));
            optionsBuilder.UseSqlServerSafeMigrations<AttributionContext>();
        }
    }
}
