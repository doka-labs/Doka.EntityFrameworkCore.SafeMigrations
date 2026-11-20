namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks;

/// <summary>Measures the real pooled runner against 100 managed tables and zero or 1000 foreign tables.</summary>

// WHY: BenchmarkDotNet's generated isolated harness derives from the benchmark type.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class MySqlLiveCatalogBenchmarks : IAsyncDisposable
{
    private readonly SafeMigrationRunOptions _options = new("live-performance");
    private IReadOnlyList<string> _cleanCodes = [];
    private LiveCatalogContext? _context;
    private MySqlLiveBenchmarkDatabase? _database;
    private Exception? _failure;
    private SafeMigrationRunReport? _lastReport;
    private IReadOnlyList<MigrationOperation> _operations = [];
    private ISafeMigrationRunner? _runner;

    /// <summary>Gets or sets the exact unrelated-table population, never a connection or credential.</summary>
    [Params(0, MySqlLiveBenchmarkWorkload.ForeignTableCount)]
    public int ForeignTableCount { get; set; }

    /// <summary>Completes noncancellable cleanup without changing the ownership of an inherited server.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await CleanupAsync(_failure);
            GC.SuppressFinalize(this);
        }
        catch (Exception exception)
        {
            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Creates and verifies the unchanged native fixture outside BenchmarkDotNet's timed method.</summary>
    /// <returns>The asynchronous owned-database preparation.</returns>
    [GlobalSetup]
    public async Task Setup()
    {
        if (ForeignTableCount is not (0 or MySqlLiveBenchmarkWorkload.ForeignTableCount))
        {
            throw new InvalidOperationException("The live catalog workload size is not supported.");
        }

        try
        {
            _database = await MySqlLiveBenchmarkDatabase.CreateAsync(CancellationToken.None);
            await _database.ExecuteFixtureAsync(
                MySqlLiveBenchmarkWorkload.BuildManagedTables(),
                CancellationToken.None);

            var options = new DbContextOptionsBuilder<LiveCatalogContext>()
                .UseMySql(_database.ConnectionString, _database.ServerVersion)
                .UseMySqlSafeMigrations()
                .Options;

            _context = new LiveCatalogContext(options);
            _runner = _context.GetService<ISafeMigrationRunner>();
            _operations = MySqlLiveBenchmarkWorkload.CreateCatalogOperations(_context.Database.ProviderName!);
            var clean = await _runner.AnalyzeAsync(_context, _operations, _options, CancellationToken.None);
            MySqlLiveBenchmarkWorkload.VerifyCatalogReport(clean, 0);

            // WHY: Retain the actual clean decisions independently of later noisy observations.
            // ReSharper disable once UseCollectionExpression
            _cleanCodes = clean
                .Assessments
                .Select(static assessment => assessment.Code)
                .ToArray();

            if (ForeignTableCount != 0)
            {
                await _database.ExecuteFixtureAsync(
                    MySqlLiveBenchmarkWorkload.BuildForeignTables(),
                    CancellationToken.None);

                var noisy = await _runner.AnalyzeAsync(_context, _operations, _options, CancellationToken.None);
                MySqlLiveBenchmarkWorkload.VerifyCatalogReport(noisy, ForeignTableCount);
                if (!clean
                    .Assessments
                    .Select(static value => value.Code)
                    .SequenceEqual(noisy.Assessments.Select(static value => value.Code), StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("Foreign catalog objects changed managed decisions.");
                }
            }
        }
        catch (Exception exception)
        {
            _failure = exception;
            await CleanupAsync(exception);

            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Measures actual native runner analysis, including its catalog round trips.</summary>
    /// <returns>The actual complete report retained for untimed iteration verification.</returns>
    [Benchmark]
    public async Task<SafeMigrationRunReport> Analyze()
    {
        try
        {
            _lastReport = await (_runner ?? throw new InvalidOperationException("Setup has not completed."))
                .AnalyzeAsync(
                    _context ?? throw new InvalidOperationException("Setup has not completed."),
                    _operations,
                    _options,
                    CancellationToken.None);

            return _lastReport;
        }
        catch (Exception exception)
        {
            _failure = exception;

            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Checks the full ordered result outside timing without additional database IO.</summary>
    [IterationCleanup]
    public void VerifyIteration()
    {
        if (_failure is not null)
        {
            return;
        }

        try
        {
            var report = _lastReport
                ?? throw new InvalidOperationException("The measured native analysis did not complete.");

            MySqlLiveBenchmarkWorkload.VerifyCatalogReport(report, ForeignTableCount);
            if (!_cleanCodes.SequenceEqual(report.Assessments.Select(static assessment => assessment.Code),
                StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Unrelated native catalog objects changed the managed decisions.");
            }
        }
        catch (Exception exception)
        {
            _failure = exception;

            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    /// <summary>Releases acquired contexts and the generated database after the explicit measurement.</summary>
    /// <returns>The protected asynchronous resource cleanup.</returns>
    [GlobalCleanup]
    public async Task Cleanup()
    {
        try
        {
            await DisposeAsync();
        }
        catch (Exception exception)
        {
            throw MySqlLiveBenchmarkDatabase.ProtectFailure(exception);
        }
    }

    private async ValueTask CleanupAsync(
        Exception? originalFailure
    )
    {
        var failure = originalFailure;
        try
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                _context,
                originalFailure,
                "SafeMigrations.MySqlLiveCatalogContextDisposeException");
        }
        catch (Exception exception)
        {
            failure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                _database,
                failure,
                "SafeMigrations.MySqlLiveCatalogDatabaseDisposeException");
        }
    }

    private sealed class LiveCatalogContext : DbContext
    {
        internal LiveCatalogContext(
            DbContextOptions<LiveCatalogContext> options
        ) : base(options) { }
    }
}
