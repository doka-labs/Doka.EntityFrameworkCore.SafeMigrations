namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Measures exact event-aware topology shapes independently of native server or network latency.</summary>
[MemoryDiagnoser]
public class SqlServerMaintenanceAttribution
{
    private bool _expected;
    private SqlServerProjectedDependencyGraph.ForeignKeyEdge[] _graph = [];
    private SqlServerProjectedDependencyGraph.ForeignKeyEdge[][] _prefixes = [];

    /// <summary>Gets or sets the sparse graph edge cardinality.</summary>
    [Params(8, 64, 256)]
    public int EdgeCount { get; set; }

    /// <summary>Gets or sets the initial cascading event action.</summary>
    [Params(ReferentialAction.Cascade, ReferentialAction.SetNull, ReferentialAction.SetDefault)]
    public ReferentialAction Action { get; set; }

    /// <summary>Gets or sets whether the initial event is DELETE rather than UPDATE.</summary>
    [Params(true, false)]
    public bool Delete { get; set; }

    /// <summary>Gets or sets the independent positive or negative topology shape.</summary>
    [Params("chain", "star", "diamond", "cycle")]
    public string Shape { get; set; } = "chain";

    /// <summary>Constructs immutable graph snapshots and verifies all topology oracles before timing.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _graph = Create(EdgeCount, Action, Delete, Shape);
        _expected = Shape is "chain" or "star";

        // WHY: Immutable prefixes model physical add/drop/replay without caching topology across mutations.
        _prefixes =
        [
            _graph
                .Take(EdgeCount - 2)
                .ToArray(),
            _graph, _graph
                .Take(EdgeCount - 2)
                .ToArray(),
            _graph,
        ];

        VerifyTopology();

        SqlServerProjectedDependencyGraph.ForeignKeyEdge[] nonCascading =
        [
            new(12345, 12345, ReferentialAction.NoAction, ReferentialAction.NoAction),
        ];

        MaintenanceAttribution.Require(
            SqlServerProjectedDependencyGraph.HasValidTopology(nonCascading),
            "A noncascading self-edge must not become a topology cycle.");

        var output = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var eventName = Delete
                ? "delete"
                : "update";

            var count = EdgeCount.ToString(CultureInfo.InvariantCulture);
            var name = $"sqlserver-topology-{count}-{Action}-{eventName}-{Shape}.json";

            // WHY: Structural acceptance evidence remains source-labeled and separate from BDN measurements.
            _ = MaintenanceAttribution.Write(
                Path.Combine(output, name),
                "SQL Server client topology only",
                [
                    new
                    {
                        Id = "PERF-04",
                        Shape,
                        Edges = _graph.Length,
                        SparsePhysicalIds = true,
                        Action = Action.ToString(),
                        Event = eventName,
                        Expected = _expected,
                        MutationResults = _prefixes
                            .Select(SqlServerProjectedDependencyGraph.HasValidTopology)
                            .ToArray(),
                    },
                ]);
        }
    }

    /// <summary>Checks the complete event-aware graph without fixture construction.</summary>
    /// <returns>Whether the sparse graph has an accepted topology.</returns>
    [Benchmark]
    public bool ValidateTopology() => SqlServerProjectedDependencyGraph.HasValidTopology(_graph);

    /// <summary>Checks the add/drop/replay sequence without sharing cached topology.</summary>
    /// <returns>An order-sensitive checksum of the four topology results.</returns>
    [Benchmark]
    public int ValidateMutationPrefixes()
    {
        var checksum = 0;

        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var prefix in _prefixes)
        {
            checksum = checksum * 2
                + (SqlServerProjectedDependencyGraph.HasValidTopology(prefix)
                    ? 1
                    : 0);
        }

        return checksum;
    }

    /// <summary>Verifies positive, negative and restoration results after the measured workload.</summary>
    [GlobalCleanup]
    public void Cleanup() => VerifyTopology();

    private void VerifyTopology()
    {
        MaintenanceAttribution.Require(
            ValidateTopology() == _expected,
            "The topology attribution graph does not have the expected event-aware result.");

        MaintenanceAttribution.Require(
            _prefixes
                .Select(SqlServerProjectedDependencyGraph.HasValidTopology)
                .SequenceEqual([true, _expected, true, _expected]),
            "The topology mutation fixture lost its negative or restoration step.");
    }

    /// <summary>Builds sparse-id graphs, retaining DELETE-to-UPDATE transitions for SET NULL/DEFAULT.</summary>
    /// <param name="count">The edge cardinality.</param>
    /// <param name="action">The first cascading event action.</param>
    /// <param name="delete">Whether the first event is DELETE rather than UPDATE.</param>
    /// <param name="shape">The independent positive or negative topology shape.</param>
    /// <returns>The prebuilt graph with sparse physical identities.</returns>
    private static SqlServerProjectedDependencyGraph.ForeignKeyEdge[] Create(
        int count,
        ReferentialAction action,
        bool delete,
        string shape
    )
    {
        var graph = new SqlServerProjectedDependencyGraph.ForeignKeyEdge[count];
        for (var index = 0; index < graph.Length; index++)
        {
            var principal = shape == "star"
                ? 1
                : index + 1;

            var dependent = index + 2;
            var onDelete = delete && (index == 0 || action == ReferentialAction.Cascade)
                ? action
                : ReferentialAction.NoAction;

            var onUpdate = !delete
                ? index == 0
                    ? action
                    : ReferentialAction.Cascade
                : action != ReferentialAction.Cascade && index > 0
                    ? ReferentialAction.Cascade
                    : ReferentialAction.NoAction;

            if (shape == "diamond"
                && index == count - 1)
            {
                principal = 1;
                dependent = count;
                onDelete = delete
                    ? ReferentialAction.Cascade
                    : ReferentialAction.NoAction;

                onUpdate = delete
                    ? ReferentialAction.NoAction
                    : ReferentialAction.Cascade;
            }
            else if (shape == "cycle"
                && index == count - 1)
            {
                dependent = 1;
            }

            graph[index] = new SqlServerProjectedDependencyGraph.ForeignKeyEdge(
                dependent * 4099,
                principal * 4099,
                onDelete,
                onUpdate);
        }

        return graph;
    }
}
