namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks;

/// <summary>Reports runtime generation and exact column-certificate reuse on both native engine variants.</summary>
public class MySqlAllocationBenchmarks : IDisposable
{
    private const int ProofReuseCount = 100;
    private DbContext _context = null!;
    private IMigrationsSqlGenerator _generator = null!;
    private IModel _model = null!;
    private MigrationOperation[] _operations = null!;
    private SafeMigrationPreflightProjection _projection = null!;
    private SafeMigrationProviderAnalysis _proof = null!;
    private SafeMigrationOperation _relaxation = null!;

    /// <summary>Gets or sets whether the native catalog contract is MariaDB rather than MySQL.</summary>
    [Params(false, true)]
    public bool IsMariaDb { get; set; }

    /// <summary>Releases native provider service ownership.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates immutable inputs and provider services without opening a connection.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql(
                "Server=127.0.0.1;Port=1;User ID=benchmark;Password=benchmark;Database=benchmark;"
                + "Allow User Variables=true",
                IsMariaDb
                    ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
                    : MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations()
            .Options;

        _context = new DbContext(options);
        _generator = _context.GetService<IMigrationsSqlGenerator>();
        _model = _context.Model;
        _operations = Enumerable
            .Range(0, 128)
            .Select(static MigrationOperation (index) => new SafeMigrationOperation(
                new EnsureColumnIntent(
                    "items",
                    new ExpectedColumnDefinition(
                        "value_" + index.ToString(CultureInfo.InvariantCulture),
                        typeof(int),
                        true,
                        "int")),
                SafeMigrationPolicy.ThrowIfDifferent))
            .ToArray();

        var analyzer = _context.GetService<ISafeMigrationProviderAnalyzer>();
        _projection = new SafeMigrationPreflightProjection(
            providerOperationProjection: (ISafeMigrationProviderOperationProjection)analyzer,
            objectIdentityNormalizer: (ISafeMigrationProviderObjectIdentityNormalizer)analyzer,
            projectedColumnAnalyzer: (ISafeMigrationProjectedColumnAnalyzer)analyzer);

        var drop = new SafeMigrationOperation(
            new DropForeignKeyIntent("fk_reference", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var observed = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None,
            false,
            "accepted_operation")
        {
            MatchedObjectName = "fk_reference",
        };

        _projection.Observe(
            drop,
            observed,
            observed,
            SafeMigrationDecisionPlanner.Plan(
                drop.Intent.Kind,
                observed.ObservedState,
                drop.Policy,
                observed.RepairCapability));

        _relaxation = new SafeMigrationOperation(
            new AlterColumnIntent(
                "items",
                new ExpectedColumnDefinition("reference_id", typeof(Guid), true, "char(36)"),
                new ExpectedColumnDefinition("reference_id", typeof(Guid), false, "char(36)")),
            SafeMigrationPolicy.RepairIfSafe);

        _proof = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            false,
            "source_bound_column_proof");

        var projected = _projection.Project(_relaxation, _proof);
        if (!ReferenceEquals(_proof, projected)
            || projected.RepairCapability != SafeMigrationRepairCapability.Safe
            || GenerateRuntimeColumns() != _operations.Length
            || ReuseColumnCertificate() != ProofReuseCount)
        {
            throw new InvalidOperationException(
                "The MySQL allocation workload did not retain its functional contract.");
        }
    }

    /// <summary>Generates the exact 128-column runtime workload retained by the SQL fingerprint tests.</summary>
    /// <returns>The generated command count.</returns>
    [Benchmark]
    public int GenerateRuntimeColumns() => _generator.Generate(_operations, _model)
        .Count;

    /// <summary>Reuses the independent live proof after an accepted foreign-key-only removal.</summary>
    /// <returns>The number of projections retaining the original proof object.</returns>
    [Benchmark(OperationsPerInvoke = ProofReuseCount)]
    public int ReuseColumnCertificate()
    {
        // WHY: Projection alone preserves the captured source; observing a repair would measure a different state path.
        var retained = 0;
        for (var iteration = 0; iteration < ProofReuseCount; iteration++)
        {
            retained += ReferenceEquals(_proof, _projection.Project(_relaxation, _proof))
                ? 1
                : 0;
        }

        return retained;
    }

    /// <summary>Releases the owned offline provider context after measurement.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Releases managed resources owned by this fixture.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            // WHY: Disposal also supports incomplete setup; null! does not initialize the context.
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            _context?.Dispose();
        }
    }
}
