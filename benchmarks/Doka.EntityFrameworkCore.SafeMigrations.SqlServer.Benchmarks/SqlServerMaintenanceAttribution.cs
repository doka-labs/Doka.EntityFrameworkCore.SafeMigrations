namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Measures exact event-aware topology shapes independently of native server or network latency.</summary>
internal static class SqlServerMaintenanceAttribution
{
    /// <summary>Records graph-size and mutation-prefix attribution with exact expected acceptance sequences.</summary>
    /// <param name="outputPath">The owned raw-evidence file.</param>
    /// <returns>Zero after every graph oracle and measurement completes.</returns>
    internal static int Run(
        string outputPath
    )
    {
        var results = new List<object>();
        foreach (var count in new[] { 8, 64, 256 })
        {
            foreach (var action in new[]
                     {
                         ReferentialAction.Cascade, ReferentialAction.SetNull, ReferentialAction.SetDefault,
                     })
            {
                foreach (var delete in new[] { true, false })
                {
                    foreach (var shape in new[] { "chain", "star", "diamond", "cycle" })
                    {
                        var graph = Create(count, action, delete, shape);
                        var expected = shape is "chain" or "star";
                        var observed = SqlServerProjectedDependencyGraph.HasValidTopology(graph);
                        MaintenanceAttribution.Require(
                            observed == expected,
                            "The topology attribution graph does not have the expected event-aware result.");

                        // WHY: Separate immutable prefixes model add/drop/replay calls without caching any
                        // topology across physical graph mutations or conflating sparse ids with ordinals.
                        var prefixes = new[]
                        {
                            graph
                                .Take(count - 2)
                                .ToArray(),
                            graph,
                            graph
                                .Take(count - 2)
                                .ToArray(),
                            graph,
                        };

                        var mutationResults = prefixes
                            .Select(SqlServerProjectedDependencyGraph.HasValidTopology)
                            .ToArray();
                        MaintenanceAttribution.Require(
                            mutationResults.SequenceEqual([true, expected, true, expected]),
                            "The topology mutation fixture lost its negative or restoration step.");

                        results.Add(
                            new
                            {
                                Id = "PERF-04",
                                Shape = shape,
                                Edges = graph.Length,
                                SparsePhysicalIds = true,
                                Action = action.ToString(),
                                Event = delete ? "delete" : "update",
                                Expected = expected,
                                MutationResults = mutationResults,
                                Samples = MaintenanceAttribution.Measure(
                                    () => SqlServerProjectedDependencyGraph.HasValidTopology(graph) ? 1 : 0,
                                    32),
                                MutationSamples = MaintenanceAttribution.Measure(
                                    () =>
                                    {
                                        var checksum = 0;
                                        foreach (var prefix in prefixes)
                                        {
                                            checksum = (checksum * 2)
                                                + (SqlServerProjectedDependencyGraph.HasValidTopology(prefix) ? 1 : 0);
                                        }

                                        return checksum;
                                    },
                                    16),
                            });
                    }
                }
            }
        }

        SqlServerProjectedDependencyGraph.ForeignKeyEdge[] nonCascading =
        [
            new(12345, 12345, ReferentialAction.NoAction, ReferentialAction.NoAction),
        ];

        MaintenanceAttribution.Require(
            SqlServerProjectedDependencyGraph.HasValidTopology(nonCascading),
            "A noncascading self-edge must not become a topology cycle.");

        return MaintenanceAttribution.Write(outputPath, "SQL Server client topology only", results);
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
            var principal = shape == "star" ? 1 : index + 1;
            var dependent = index + 2;
            var onDelete = delete && (index == 0 || action == ReferentialAction.Cascade)
                ? action
                : ReferentialAction.NoAction;

            var onUpdate = !delete
                ? index == 0 ? action : ReferentialAction.Cascade
                : action != ReferentialAction.Cascade && index > 0
                    ? ReferentialAction.Cascade
                    : ReferentialAction.NoAction;

            if (shape == "diamond"
                && index == count - 1)
            {
                principal = 1;
                dependent = count;
                onDelete = delete ? ReferentialAction.Cascade : ReferentialAction.NoAction;
                onUpdate = delete ? ReferentialAction.NoAction : ReferentialAction.Cascade;
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
