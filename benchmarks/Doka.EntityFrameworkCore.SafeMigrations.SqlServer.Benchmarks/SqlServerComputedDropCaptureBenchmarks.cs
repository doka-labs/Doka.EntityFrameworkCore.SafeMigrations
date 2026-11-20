namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Measures the original owner-local computed-DROP capture shapes without test-owned sampling.</summary>
[MemoryDiagnoser]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class SqlServerComputedDropCaptureBenchmarks : IDisposable
{
    private SqlServerSafeMigrationProviderAnalyzer? _analyzer;
    private SqlServerGenerationContext? _context;
    private SafeMigrationDecision? _decision;
    private SafeMigrationProviderAnalysis? _matching;
    private SafeMigrationOperation[] _operations = [];
    private int _owners;
    private Action<IReadOnlyList<SafeMigrationOperation>>? _prepare;

    /// <summary>Gets or sets the original independent capture family and its fixed cardinality.</summary>
    [Params(
        "index-prefix",
        "constraint-prefix",
        "foreign-key-route",
        "accepted-observer-512",
        "accepted-observer-1024")]
    public string Workload { get; set; } = "index-prefix";

    private SqlServerSafeMigrationProviderAnalyzer Analyzer
        => _analyzer ?? throw new InvalidOperationException("Setup has not completed.");

    /// <summary>Releases the managed provider lifetimes owned by this benchmark instance.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Constructs the immutable operation stream and qualifies its exact owner routes before timing.</summary>
    [GlobalSetup]
    public void Setup()
    {
        if (Workload is not ("index-prefix"
            or "constraint-prefix"
            or "foreign-key-route"
            or "accepted-observer-512"
            or "accepted-observer-1024"))
        {
            throw new ArgumentOutOfRangeException(nameof(Workload), "The computed DROP capture workload is unknown.");
        }

        _context = new SqlServerGenerationContext();
        _analyzer = new SqlServerSafeMigrationProviderAnalyzer(
            _context.GetService<IRelationalTypeMappingSource>(),
            _context.GetService<ISqlGenerationHelper>());

        _matching = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None,
            false,
            "classified_matching");

        _owners = Workload == "accepted-observer-1024"
            ? 1024
            : 512;

        _operations = CreateOperations();
        var methodName = Workload == "index-prefix"
            ? "PrepareComputedDropIndexCandidates"
            : "PrepareColumnDropConstraintCandidates";

        _prepare = (typeof(SqlServerSafeMigrationProviderAnalyzer).GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The capture preparation is unavailable."))
            .CreateDelegate<Action<IReadOnlyList<SafeMigrationOperation>>>(Analyzer);

        if (Workload.StartsWith("accepted-observer-", StringComparison.Ordinal))
        {
            _prepare(_operations);
            var native = _operations
                .Select(static operation => operation.Intent is DropColumnIntent
                    ? new SafeMigrationProviderAnalysis(
                        SafeMigrationObservedState.Different,
                        SafeMigrationRepairCapability.None,
                        false,
                        "column_drop_candidate_dependency")
                    : new SafeMigrationProviderAnalysis(
                        SafeMigrationObservedState.Matching,
                        SafeMigrationRepairCapability.None,
                        false,
                        "classified_matching"))
                .ToArray();

            Analyzer.CaptureProjectedComputedDropRefusals(_operations, native);
            _decision = SafeMigrationDecisionPlanner.Plan(
                SafeMigrationOperationKind.DropCheckConstraint,
                _matching.ObservedState,
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        _ = Capture();
        ValidateCapture();
    }

    /// <summary>Captures only the prepared predecessor stream or its accepted-removal observations.</summary>
    /// <returns>The exact number of captured column-DROP owners.</returns>
    [Benchmark]
    public int Capture()
    {
        if (_decision is not null)
        {
            var matching = _matching ?? throw new InvalidOperationException("Setup has not completed.");
            for (var owner = 0; owner < _owners; owner++)
            {
                Analyzer.ObserveAcceptedOperation(_operations[owner * 2], matching, matching, _decision);
            }

            return _owners;
        }

        (_prepare ?? throw new InvalidOperationException("Setup has not completed."))(_operations);

        return Workload switch
        {
            "index-prefix" => 1024,
            "constraint-prefix" => _owners + 1,
            _ => _owners,
        };
    }

    /// <summary>Checks final proof ownership outside measurement and releases the managed provider services.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        try
        {
            ValidateCapture();
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Releases managed benchmark services without running measured work.</summary>
    /// <param name="disposing">Whether managed resources are eligible for disposal.</param>
    protected virtual void Dispose(
        bool disposing
    )
    {
        if (disposing)
        {
            _analyzer?.Dispose();
            _analyzer = null;
            _context?.Dispose();
            _context = null;
        }
    }

    /// <summary>Recreates every original fixture cardinality without sharing construction with measurement.</summary>
    private SafeMigrationOperation[] CreateOperations()
    {
        if (Workload == "index-prefix")
        {
            return Enumerable
                .Range(0, 512)
                .Select(static index => new SafeMigrationOperation(
                    new DropIndexIntent("IX_" + index.ToString(CultureInfo.InvariantCulture), "nodes"),
                    SafeMigrationPolicy.ThrowIfDifferent))
                .Concat(Enumerable
                    .Range(0, 1024)
                    .Select(static index => new SafeMigrationOperation(
                        new DropColumnIntent("field_" + index.ToString(CultureInfo.InvariantCulture), "nodes"),
                        SafeMigrationPolicy.ThrowIfDifferent)))
                // WHY: Fluent terminal materialization preserves the existing snapshot and enumeration contract.
                // ReSharper disable once UseCollectionExpression
                .ToArray();
        }

        var operations = new List<SafeMigrationOperation>(_owners * 2 + 1);
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        for (var owner = 0; owner < _owners; owner++)
        {
            var suffix = owner.ToString(CultureInfo.InvariantCulture);
            var table = "owner_" + suffix;
            var foreign = Workload == "foreign-key-route";
            if (foreign)
            {
                BindNativeGraph(graph, "child_" + suffix, "parent_" + suffix, owner);
            }

            operations.Add(new SafeMigrationOperation(
                foreign
                    ? new DropForeignKeyIntent("FK_L", "child_" + suffix)
                    : new DropCheckConstraintIntent("CK_L", table),
                SafeMigrationPolicy.ThrowIfDifferent));

            operations.Add(new SafeMigrationOperation(
                new DropColumnIntent("L",
                    foreign
                        ? "parent_" + suffix
                        : table),
                SafeMigrationPolicy.ThrowIfDifferent));
        }

        if (Workload == "constraint-prefix")
        {
            operations.Add(new SafeMigrationOperation(
                new DropColumnIntent("R", "owner_0"),
                SafeMigrationPolicy.ThrowIfDifferent));
        }

        if (Workload == "foreign-key-route")
        {
            Field("_dependencyGraph")
                .SetValue(Analyzer, graph);
        }

        // WHY: Fluent terminal materialization preserves the existing snapshot and enumeration contract.
        // ReSharper disable once UseCollectionExpression
        return operations.ToArray();
    }

    /// <summary>Checks ordinal identities, local-owner reuse and accepted proof-reference ownership.</summary>
    private void ValidateCapture()
    {
        if (Workload == "index-prefix")
        {
            var captures = (Dictionary<SafeMigrationOperation, int[]>)Value("_computedDropIndexCandidates");
            MaintenanceAttribution.Require(captures.Count == 1024, "The index-prefix drop count changed.");
            MaintenanceAttribution.Require(
                captures.Values.All(static value => value.Length == 512),
                "The index-prefix predecessor count changed.");

            return;
        }

        if (_decision is not null)
        {
            var columns = new ObserverColumns();
            var matching = _matching ?? throw new InvalidOperationException("Setup has not completed.");
            MaintenanceAttribution.Require(
                _operations
                    .Where(static operation => operation.Intent is DropColumnIntent)
                    .All(operation => Analyzer.ValidateProjectedOperation(operation, matching, columns)
                            .Code
                        == "projected_column_drop_safe"),
                "An accepted predecessor did not authorize its exact column DROP.");

            var routes = (IDictionary)Value("_nativeColumnDropOwnerRoutes");
            var proofs = (IDictionary)Value("_nativeComputedDropRefusals");
            MaintenanceAttribution.Require(routes.Count == _owners, "The accepted owner route count changed.");
            foreach (DictionaryEntry route in routes)
            {
                var members = (IList)(route.Value ?? throw new InvalidOperationException("An owner route is missing."));
                MaintenanceAttribution.Require(members.Count == 1, "An accepted owner route is not singular.");
                var member = (ITuple)(members[0] ?? throw new InvalidOperationException("An owner proof is missing."));
                var operation = member[0] as SafeMigrationOperation
                    ?? throw new InvalidOperationException("An accepted owner operation is missing.");

                MaintenanceAttribution.Require(
                    ReferenceEquals(proofs[operation], member[1]),
                    "An accepted route lost its original proof identity.");
            }

            return;
        }

        var captured = (IDictionary)Value("_columnDropConstraintCandidates");
        MaintenanceAttribution.Require(
            captured.Count
            == _owners
            + (Workload == "constraint-prefix"
                ? 1
                : 0),
            "The owner-local predecessor count changed.");

        for (var owner = 0; owner < _owners; owner++)
        {
            var snapshot = captured[_operations[owner * 2 + 1]]
                ?? throw new InvalidOperationException("An owner snapshot is missing.");

            var ordinals = (int[])(snapshot
                    .GetType()
                    .GetProperty("Ordinals", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(snapshot)
                ?? throw new InvalidOperationException("The immutable ordinal storage is missing."));

            MaintenanceAttribution.Require(
                ordinals.Length == 1 && ordinals[0] == owner * 2,
                "The exact owner-local predecessor ordinal changed.");
        }

        if (Workload == "constraint-prefix")
        {
            MaintenanceAttribution.Require(
                ReferenceEquals(captured[_operations[1]], captured[_operations[^1]]),
                "An unchanged owner prefix lost immutable snapshot reuse.");
        }
    }

    /// <summary>Reads existing invocation-owned capture state only for untimed structural qualification.</summary>
    private object Value(
        string name
    ) => Field(name)
            .GetValue(Analyzer)
        ?? throw new InvalidOperationException("The capture state is unavailable.");

    /// <summary>Resolves the exact existing private capture field outside measured work.</summary>
    private static FieldInfo Field(
        string name
    ) => typeof(SqlServerSafeMigrationProviderAnalyzer).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The capture field is unavailable.");

    /// <summary>Installs the exact native FK identity and endpoints used by the original 512-owner fixture.</summary>
    private static void BindNativeGraph(
        SqlServerProjectedDependencyGraph graph,
        string child,
        string parent,
        int owner
    )
    {
        var type = typeof(SqlServerProjectedDependencyGraph);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var tableType = type.GetNestedType("TableKey", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native table key is unavailable.");

        var foreignType = type.GetNestedType("ForeignKeyKey", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native FK key is unavailable.");

        var tables = (IDictionary)(type
                .GetField("_tables", flags)
                ?.GetValue(graph)
            ?? throw new InvalidOperationException("The native owner inventory is unavailable."));

        var dependent = owner * 2 + 1;
        var principal = owner * 2 + 2;
        tables.Add(Activator.CreateInstance(tableType, "dbo", child)
            ?? throw new InvalidOperationException("The dependent table key is unavailable."),
            dependent);

        tables.Add(Activator.CreateInstance(tableType, "dbo", parent)
            ?? throw new InvalidOperationException("The principal table key is unavailable."),
            principal);

        var names = (IDictionary)(type
                .GetField("_foreignKeyNames", flags)
                ?.GetValue(graph)
            ?? throw new InvalidOperationException("The native FK names are unavailable."));

        names.Add(Activator.CreateInstance(foreignType, dependent, "FK_L")
            ?? throw new InvalidOperationException("The native FK identity is unavailable."),
            owner + 1);

        var edges = (Dictionary<int, SqlServerProjectedDependencyGraph.ForeignKeyEdge>)(
            type
                .GetField("_foreignKeys", flags)
                ?.GetValue(graph)
            ?? throw new InvalidOperationException("The native FK endpoints are unavailable."));

        edges.Add(owner + 1,
            new SqlServerProjectedDependencyGraph.ForeignKeyEdge(
                dependent,
                principal,
                ReferentialAction.NoAction,
                ReferentialAction.NoAction,
                1));
    }

    private sealed class ObserverColumns : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        )
        {
            definition = null;

            return false;
        }
    }
}
