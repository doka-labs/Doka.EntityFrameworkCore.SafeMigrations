namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks;

/// <summary>Measures the original pooled 100-table live runner against clean and 1000-foreign-table catalogs.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class PostgreSqlLiveCatalogBenchmarks : IAsyncDisposable
{
    private const int ManagedTableCount = 100;
    private const int ForeignTableCount = 1000;
    private PostgreSqlLiveBenchmarkDatabase? _database;
    private string[] _expectedCodes = [];
    private IReadOnlyList<MigrationOperation> _operations = [];
    private SafeMigrationRunOptions _options = null!;
    private ISafeMigrationRunner _runner = null!;

    /// <summary>Gets or sets whether the catalog includes exactly 1000 indexed foreign tables.</summary>
    [Params(false, true)]
    public bool ForeignObjects { get; set; }

    /// <summary>Verifies final deterministic results before awaiting all database-owned cleanup.</summary>
    /// <returns>The final verification and noncancellable resource release.</returns>
    public async ValueTask DisposeAsync()
    {
        Exception? failure = null;
        bool cleanupCompleted;
        try
        {
            if (_database is not null
                && _expectedCodes.Length != 0)
            {
                Validate(await AnalyzeCoreAsync(CancellationToken.None), ForeignObjects);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            cleanupCompleted = await SafeMigrationAnalysisCleanup.DisposeAsync(
                _database,
                failure,
                "SafeMigrations.PostgreSqlLiveCatalogCleanupException");
        }
        catch (Exception exception)
        {
            cleanupCompleted = false;
            failure = exception;
        }

        _database = null;
        GC.SuppressFinalize(this);
        if (failure is NpgsqlException nativeFailure)
        {
            throw PostgreSqlLiveBenchmarkDatabase.NativeFailure(nativeFailure, "catalog finalization");
        }

        if (failure is not null)
        {
            if (!cleanupCompleted
                && failure is not OperationCanceledException)
            {
                // WHY: Preserve the readable invariant while keeping secondary native cleanup data private to BDN.
                throw new InvalidOperationException(failure.Message + " Owned native cleanup also failed.");
            }

            ExceptionDispatchInfo
                .Capture(failure)
                .Throw();
        }
    }

    /// <summary>Creates the exact native fixture and validates both catalog states outside measured work.</summary>
    /// <returns>Completion of isolated setup and deterministic result validation.</returns>
    [GlobalSetup]
    public async Task Setup()
    {
        if (_database is not null)
        {
            throw new InvalidOperationException("The native catalog benchmark already owns a database.");
        }

        try
        {
            _database = await PostgreSqlLiveBenchmarkDatabase.CreateAsync(pooling: true, CancellationToken.None);
            await _database.ExecuteAsync(
                BuildTables("expected_perf_", ManagedTableCount, false),
                CancellationToken.None);

            var context = _database.Context;
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            for (var index = 0; index < ManagedTableCount; index++)
            {
                builder.EnsureTable(
                    new ExpectedTableDefinition(
                        $"expected_perf_{index:D4}",
                        [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer")]),
                    SafeMigrationTableMode.StrictDefinition,
                    SafeMigrationPolicy.ThrowIfDifferent);
            }

            _operations = builder.Operations;
            _runner = context.GetService<ISafeMigrationRunner>();
            _options = new SafeMigrationRunOptions("live-performance");
            var clean = await AnalyzeCoreAsync(CancellationToken.None);
            _expectedCodes = clean
                .Assessments
                .Select(static assessment => assessment.Code)

                // WHY: Preserve the complete clean-state code order for noisy and final result comparisons.
                // ReSharper disable once UseCollectionExpression
                .ToArray();

            Validate(clean, foreignObjects: false);
            if (ForeignObjects)
            {
                await _database.ExecuteAsync(
                    BuildTables("foreign_perf_", ForeignTableCount, true),
                    CancellationToken.None);
            }

            Validate(await AnalyzeCoreAsync(CancellationToken.None), ForeignObjects);
        }
        catch (Exception exception)
        {
            var cleanupCompleted = await SafeMigrationAnalysisCleanup.DisposeAsync(
                _database,
                exception,
                "SafeMigrations.PostgreSqlLiveCatalogSetupCleanupException");

            _database = null;

            if (exception is NpgsqlException nativeFailure)
            {
                throw PostgreSqlLiveBenchmarkDatabase.NativeFailure(nativeFailure, "catalog setup");
            }

            if (!cleanupCompleted
                && exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(exception.Message + " Owned native cleanup also failed.");
            }

            throw;
        }
    }

    /// <summary>Analyzes the complete catalog through the real pooled SafeMigrations runner.</summary>
    /// <returns>The actual native runner report consumed by BenchmarkDotNet.</returns>
    [Benchmark]
    public async Task<SafeMigrationRunReport> Analyze()
    {
        try
        {
            // WHY: The runner opens pooled connections itself; BDN must never log their raw authentication errors.
            return await AnalyzeCoreAsync(CancellationToken.None);
        }
        catch (NpgsqlException exception)
        {
            throw PostgreSqlLiveBenchmarkDatabase.NativeFailure(exception, "catalog analysis");
        }
    }

    /// <summary>Keeps raw native failures private until the owning setup or final cleanup has completed.</summary>
    /// <param name="cancellationToken">Cancels actual forward native analysis.</param>
    /// <returns>The complete native report.</returns>
    private Task<SafeMigrationRunReport> AnalyzeCoreAsync(
        CancellationToken cancellationToken
    ) => _runner.AnalyzeAsync(
        _database?.Context ?? throw new InvalidOperationException("Native setup is incomplete."),
        _operations,
        _options,
        cancellationToken);

    /// <summary>Releases only the generated database after native measurements and final verification.</summary>
    /// <returns>The actual asynchronous cleanup completion.</returns>
    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    /// <summary>
    /// Checks exact managed identity, classification order and foreign inventory without timing verdicts.
    /// </summary>
    /// <param name="report">The complete native runner result.</param>
    /// <param name="foreignObjects">Whether this state contains the 1000 foreign fixture tables.</param>
    private void Validate(
        SafeMigrationRunReport report,
        bool foreignObjects
    )
    {
        MaintenanceAttribution.Require(
            report is { Status: SafeMigrationReportStatus.Ready, Assessments.Count: ManagedTableCount },
            "The native PostgreSQL catalog lost its complete managed table result.");

        for (var index = 0; index < ManagedTableCount; index++)
        {
            var assessment = report.Assessments[index];
            MaintenanceAttribution.Require(
                assessment.Ordinal == index
                && assessment.ObjectName == $"expected_perf_{index:D4}"
                && assessment is
                {
                    OperationKind: SafeMigrationOperationKind.EnsureTable,
                    ObservedState: SafeMigrationObservedState.Matching,
                    Action: SafeMigrationAction.NoOp,
                },
                "The native PostgreSQL managed table order or classification changed.");
        }

        MaintenanceAttribution.Require(
            report.UnexpectedObjects.Count
            == (foreignObjects
                ? ForeignTableCount
                : 0)
            && report.UnexpectedObjects.All(static value => value.ObjectKind == SafeMigrationDatabaseObjectKind.Table)
            && _expectedCodes.SequenceEqual(report.Assessments.Select(static value => value.Code)),
            "Foreign PostgreSQL objects changed managed classification or unexpected-object inventory.");
    }

    /// <summary>Creates exact managed or indexed foreign fixture tables without changing database defaults.</summary>
    /// <param name="prefix">The fixed source-controlled table-name prefix.</param>
    /// <param name="count">The exact fixture cardinality.</param>
    /// <param name="includeIndex">Whether each foreign table owns its payload index.</param>
    /// <returns>The complete fixture DDL used only during global setup.</returns>
    private static string BuildTables(
        string prefix,
        int count,
        bool includeIndex
    ) => string.Join(
        Environment.NewLine,
        Enumerable
            .Range(0, count)
            .Select(index =>
            {
                var table = $"{prefix}{index:D4}";
                var payload = includeIndex
                    ? ", \"payload\" integer NULL"
                    : string.Empty;

                var indexSql = includeIndex
                    ? $" CREATE INDEX \"ix_{table}_payload\" ON \"{table}\" (\"payload\");"
                    : string.Empty;

                return $"CREATE TABLE \"{table}\" (\"id\" integer NOT NULL{payload});{indexSql}";
            }));
}
