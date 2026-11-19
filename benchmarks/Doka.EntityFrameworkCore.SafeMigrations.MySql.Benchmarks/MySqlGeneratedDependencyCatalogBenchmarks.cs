namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks;

/// <summary>Measures the original thousand-key dependency catalog using BDN-owned allocation sampling.</summary>

// WHY: BenchmarkDotNet's generated isolated harness derives from the benchmark type.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class MySqlGeneratedDependencyCatalogBenchmarks
{
    private const int DropCount = 1000;
    private MySqlGeneratedColumnDependencyContractCatalog? _lastCatalog;
    private List<MigrationOperation> _operations = [];

    /// <summary>Creates the exact distinct operation keys and ambiguous owner outside measured execution.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var operations = new List<MigrationOperation>(DropCount + 3)
        {
            Safe(new EnsureTableIntent(
                new ExpectedTableDefinition("rows", [Ordinary("value"), Ordinary("other")]),
                SafeMigrationTableMode.StrictDefinition)),
        };

        for (var ordinal = 0; ordinal < DropCount; ordinal++)
        {
            operations.Add(Safe(new DropColumnIntent($"retired_{ordinal}", "rows")));
        }

        operations.Add(Safe(new EnsureTableIntent(
            new ExpectedTableDefinition("ambiguous", [Ordinary("VALUE"), Ordinary("value")]),
            SafeMigrationTableMode.StrictDefinition)));

        operations.Add(Safe(new DropColumnIntent("retired", "ambiguous")));
        _operations = operations;
        _lastCatalog = MySqlGeneratedColumnDependencyContractCatalog.Create(_operations);
        VerifyIteration();
    }

    /// <summary>Constructs the real catalog; all identity verification remains outside this timed method.</summary>
    /// <returns>The actual catalog consumed by BenchmarkDotNet without publishing an internal shipping type.</returns>
    [Benchmark]
    public object Create()
    {
        _lastCatalog = MySqlGeneratedColumnDependencyContractCatalog.Create(_operations);

        return _lastCatalog;
    }

    /// <summary>Verifies shared empties, distinct keys, ambiguous ownership and reference-only lookup.</summary>
    [IterationCleanup]
    public void VerifyIteration()
    {
        var catalog = _lastCatalog ?? throw new InvalidOperationException("The measured catalog was not constructed.");
        var first = catalog.Get((SafeMigrationOperation)_operations[1]);
        if (first is null
            || !first.HasOnlyParsedExpressions)
        {
            throw new InvalidOperationException("The generated catalog lost its parsed empty contract.");
        }

        for (var ordinal = 1; ordinal <= DropCount; ordinal++)
        {
            if (!ReferenceEquals(first, catalog.Get((SafeMigrationOperation)_operations[ordinal])))
            {
                throw new InvalidOperationException("The generated catalog lost a distinct operation key.");
            }
        }

        var ambiguous = catalog.Get((SafeMigrationOperation)_operations[^1]);
        if (ambiguous is null
            || ReferenceEquals(first, ambiguous)
            || ambiguous.HasOnlyParsedExpressions
            || catalog.Get(Safe(new DropColumnIntent("retired_0", "rows"))) is not null)
        {
            throw new InvalidOperationException(
                "The generated catalog merged ambiguous or nonidentical operation keys.");
        }
    }

    private static ExpectedColumnDefinition Ordinary(
        string name
    ) => new(name, typeof(int), true, "int");

    private static SafeMigrationOperation Safe(
        SafeMigrationIntent intent
    ) => new(intent, SafeMigrationPolicy.ThrowIfDifferent);
}
