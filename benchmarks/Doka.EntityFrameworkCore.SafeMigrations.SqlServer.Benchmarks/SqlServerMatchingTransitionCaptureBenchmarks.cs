namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Compares matching replay pruning with former selection on deterministic managed transport.</summary>
[MemoryDiagnoser]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class SqlServerMatchingTransitionCaptureBenchmarks : IDisposable
{
    private readonly SqlServerMatchingTransitionConnection _currentConnection = new();
    private readonly SqlServerMatchingTransitionConnection _formerConnection = new();
    private SqlServerGenerationContext? _context;
    private SqlServerSafeMigrationProviderAnalyzer? _current;
    private SafeMigrationProviderAnalysis[] _different = [];
    private SqlServerSafeMigrationProviderAnalyzer? _former;
    private SafeMigrationProviderAnalysis[] _matching = [];
    private SafeMigrationOperation[] _operations = [];

    /// <summary>Gets or sets the exact independent matching candidate count from the regression fixtures.</summary>
    [Params(1, 512)]
    public int CandidateCount { get; set; }

    /// <summary>Releases the independent provider and transport lifetimes owned by this case.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Constructs provider services, immutable operations and deterministic responses before timing.</summary>
    /// <returns>The completion of both structural qualification calls.</returns>
    [GlobalSetup]
    public async Task Setup()
    {
        _context = new SqlServerGenerationContext();
        _current = CreateAnalyzer(_context);
        _former = CreateAnalyzer(_context);
        _operations = Enumerable
            .Range(0, CandidateCount)
            .Select(static index => new SafeMigrationOperation(
                new AlterColumnIntent(
                    "items_" + index.ToString(CultureInfo.InvariantCulture),
                    new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
                    new ExpectedColumnDefinition("Value", typeof(int), false, "int")),
                SafeMigrationPolicy.RepairIfSafe))
            // WHY: Fluent terminal materialization preserves the existing snapshot and enumeration contract.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        _matching = _operations
            .Select(static _ => Analysis(SafeMigrationObservedState.Matching))
            // WHY: Fluent terminal materialization preserves the existing snapshot and enumeration contract.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        _different = _operations
            .Select(static _ => Analysis(SafeMigrationObservedState.Different))
            // WHY: Fluent terminal materialization preserves the existing snapshot and enumeration contract.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        await CaptureFormer();
        await CaptureMatching();
        VerifyFormer();
        VerifyMatching();
    }

    /// <summary>Resets transport evidence outside measurement, preventing unbounded record retention.</summary>
    [IterationSetup]
    public void ResetTransport()
    {
        _currentConnection.StatementCount = 0;
        _formerConnection.StatementCount = 0;
    }

    /// <summary>Captures source certificates as the former state-independent selection did.</summary>
    /// <returns>The actual local catalog capture operation, excluding fixture creation.</returns>
    [Benchmark(Baseline = true)]
    public Task CaptureFormer() => (_former ?? throw new InvalidOperationException("Setup has not completed."))
        .ReadProjectedColumnTransitionsAsync(
            _formerConnection,
            null,
            _operations,
            _different,
            true,
            73,
            CancellationToken.None);

    /// <summary>Prunes already matching replay candidates without catalog capture or retained growth.</summary>
    /// <returns>The actual matching capture-selection operation.</returns>
    [Benchmark]
    public Task CaptureMatching() => (_current ?? throw new InvalidOperationException("Setup has not completed."))
        .ReadProjectedColumnTransitionsAsync(
            _currentConnection,
            null,
            _operations,
            _matching,
            true,
            73,
            CancellationToken.None);

    /// <summary>Requires the counterfactual selection to retain actual nonempty generated catalog work.</summary>
    [IterationCleanup(Target = nameof(CaptureFormer))]
    public void VerifyFormer() => MaintenanceAttribution.Require(
        _formerConnection.StatementCount > 0,
        "The former selection no longer performs its prepared catalog capture.");

    /// <summary>Requires every matching candidate to bypass catalog transport.</summary>
    [IterationCleanup(Target = nameof(CaptureMatching))]
    public void VerifyMatching() => MaintenanceAttribution.Require(
        _currentConnection.StatementCount == 0,
        "Matching replay unexpectedly dispatched a transition capture.");

    /// <summary>Releases both independent analyzer and deterministic transport lifetimes outside measurement.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed benchmark services outside measured invocations.</summary>
    /// <param name="disposing">Whether managed resources are eligible for disposal.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            _current?.Dispose();
            _current = null;
            _former?.Dispose();
            _former = null;
            _currentConnection.Dispose();
            _formerConnection.Dispose();
            _context?.Dispose();
            _context = null;
        }
    }

    private static SqlServerSafeMigrationProviderAnalyzer CreateAnalyzer(
        DbContext context
    ) => new(
        context.GetService<IRelationalTypeMappingSource>(),
        context.GetService<ISqlGenerationHelper>());

    private static SafeMigrationProviderAnalysis Analysis(
        SafeMigrationObservedState state
    ) => new(
        state,
        SafeMigrationRepairCapability.None,
        state == SafeMigrationObservedState.Matching,
        "classified_state");
}
