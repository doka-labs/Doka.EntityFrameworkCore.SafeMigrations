namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies immutable predecessor snapshots and records allocation evidence without timing gates.</summary>
/// <param name="output">The report-only allocation output.</param>
public sealed class SqlServerComputedDropCaptureAllocationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// Unchanged prefixes share immutable storage; a later predecessor never mutates an earlier capture.
    /// </summary>
    [Fact]
    public void UnchangedPredecessorPrefixReusesImmutableSnapshot()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        SafeMigrationOperation[] operations =
        [
            DropIndex("IX_L"), DropColumn("L"), DropColumn("R"), DropIndex("IX_L"), DropColumn("M"),
        ];

        // Act
        Prepare(analyzer, operations);
        var captured = Captures(analyzer);

        // Assert
        Assert.Same(captured[operations[1]], captured[operations[2]]);
        Assert.NotSame(captured[operations[2]], captured[operations[4]]);
        Assert.Equal(0, Assert.Single(captured[operations[1]]));
        Assert.Equal(3, Assert.Single(captured[operations[4]]));
    }

    /// <summary>
    /// Measures candidate preparation alone, excluding EF setup and input creation; bytes are informational.
    /// </summary>
    [Fact]
    public void CandidatePreparationAllocationEvidenceIsInformational()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        const int indexes = 512;
        const int drops = 1024;
        var operations = new List<SafeMigrationOperation>(indexes + drops);
        for (var index = 0; index < indexes; index++)
        {
            operations.Add(DropIndex("IX_" + index.ToString(CultureInfo.InvariantCulture)));
        }

        for (var index = 0; index < drops; index++)
        {
            operations.Add(DropColumn("field_" + index.ToString(CultureInfo.InvariantCulture)));
        }

        var allocations = new long[3];
        var prepare = Preparation(analyzer);
        prepare(operations);

        // Act
        for (var sample = 0; sample < allocations.Length; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            prepare(operations);
            allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var captured = Captures(analyzer);

        // Assert
        Assert.Equal(drops, captured.Count);
        Assert.All(captured.Values, snapshot => Assert.Equal(indexes, snapshot.Length));
        output.WriteLine("candidate-capture indexes={0} drops={1} samples-bytes={2}",
            indexes, drops, string.Join(",", allocations));
    }

    /// <summary>Interleaved owners retain only their own predecessors, not growing global snapshots.</summary>
    [Fact]
    public void ConstraintPrefixesRetainOnlyOwnerLocalImmutableStorage()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        const int owners = 512;
        var operations = new List<SafeMigrationOperation>(owners * 2 + 1);
        for (var owner = 0; owner < owners; owner++)
        {
            var table = "owner_" + owner.ToString(CultureInfo.InvariantCulture);
            operations.Add(new SafeMigrationOperation(new DropCheckConstraintIntent("CK_L", table),
                SafeMigrationPolicy.ThrowIfDifferent));
            operations.Add(new SafeMigrationOperation(new DropColumnIntent("L", table),
                SafeMigrationPolicy.ThrowIfDifferent));
        }

        operations.Add(new SafeMigrationOperation(new DropColumnIntent("R", "owner_0"),
            SafeMigrationPolicy.ThrowIfDifferent));
        var method = typeof(SqlServerSafeMigrationProviderAnalyzer).GetMethod("PrepareColumnDropConstraintCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The constraint predecessor preparation is unavailable.");

        var prepare = method.CreateDelegate<Action<IReadOnlyList<SafeMigrationOperation>>>(analyzer);
        var allocations = new long[3];
        prepare(operations);

        // Act
        for (var sample = 0; sample < allocations.Length; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            prepare(operations);
            allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var field = typeof(SqlServerSafeMigrationProviderAnalyzer).GetField("_columnDropConstraintCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The constraint predecessor capture is unavailable.");

        var captures = (System.Collections.IDictionary)(field.GetValue(analyzer)
            ?? throw new InvalidOperationException("The constraint predecessor capture is empty."));

        // Assert
        Assert.Equal(owners + 1, captures.Count);
        Assert.Same(captures[operations[1]], captures[operations[^1]]);
        for (var owner = 0; owner < owners; owner++)
        {
            var snapshot = captures[operations[owner * 2 + 1]]
                ?? throw new InvalidOperationException("An owner snapshot is missing.");

            var ordinals = snapshot.GetType().GetProperty("Ordinals",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The immutable ordinal storage is missing.");

            Assert.Equal(owner * 2, Assert.Single(Assert.IsType<int[]>(ordinals.GetValue(snapshot))));
        }

        output.WriteLine("constraint-capture owners={0} samples-bytes={1}", owners, string.Join(",", allocations));
    }

    /// <summary>Records native FK routing cost without using load-dependent elapsed time as a test gate.</summary>
    [Fact]
    public void NativeForeignKeyRoutingEvidenceIsInformational()
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var graph = new SqlServerProjectedDependencyGraph(static (_, _) => true);
        const int owners = 512;
        var operations = new List<SafeMigrationOperation>(owners * 2);
        for (var owner = 0; owner < owners; owner++)
        {
            var suffix = owner.ToString(CultureInfo.InvariantCulture);
            var child = "child_" + suffix;
            var parent = "parent_" + suffix;
            BindNativeGraph(graph, child, parent, owner * 2 + 1, owner * 2 + 2, owner + 1);
            operations.Add(new SafeMigrationOperation(new DropForeignKeyIntent("FK_L", child),
                SafeMigrationPolicy.ThrowIfDifferent));
            operations.Add(new SafeMigrationOperation(new DropColumnIntent("L", parent),
                SafeMigrationPolicy.ThrowIfDifferent));
        }

        var graphField = typeof(SqlServerSafeMigrationProviderAnalyzer).GetField("_dependencyGraph",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The physical FK graph is unavailable.");

        graphField.SetValue(analyzer, graph);
        var method = typeof(SqlServerSafeMigrationProviderAnalyzer).GetMethod("PrepareColumnDropConstraintCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native predecessor capture is unavailable.");

        var prepare = method.CreateDelegate<Action<IReadOnlyList<SafeMigrationOperation>>>(analyzer);
        prepare(operations);
        var allocations = new long[3];
        var elapsedTicks = new long[3];

        // Act
        for (var sample = 0; sample < allocations.Length; sample++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            prepare(operations);
            elapsedTicks[sample] = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }

        var field = typeof(SqlServerSafeMigrationProviderAnalyzer).GetField("_columnDropConstraintCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The owner-local FK capture is unavailable.");

        var captured = (System.Collections.IDictionary)(field.GetValue(analyzer)
            ?? throw new InvalidOperationException("The owner-local FK capture is empty."));

        // Assert
        Assert.Equal(owners, captured.Count);
        for (var owner = 0; owner < owners; owner++)
        {
            var snapshot = captured[operations[owner * 2 + 1]]
                ?? throw new InvalidOperationException("The native FK endpoint route is missing.");

            var ordinals = snapshot.GetType().GetProperty("Ordinals",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The exact routed predecessor is missing.");

            Assert.Equal(owner * 2, Assert.Single(Assert.IsType<int[]>(ordinals.GetValue(snapshot))));
        }

        output.WriteLine("native-FK-capture owners={0} bytes={1} ticks={2} frequency={3}", owners,
            string.Join(",", allocations), string.Join(",", elapsedTicks), System.Diagnostics.Stopwatch.Frequency);
    }

    private static void BindNativeGraph(SqlServerProjectedDependencyGraph graph, string child, string parent,
        int dependent, int principal, int identity)
    {
        var type = typeof(SqlServerProjectedDependencyGraph);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var tableType = type.GetNestedType("TableKey", System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native table identity key is unavailable.");

        var foreignType = type.GetNestedType("ForeignKeyKey", System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native FK identity key is unavailable.");

        var tables = (System.Collections.IDictionary)(type.GetField("_tables", flags)?.GetValue(graph)
            ?? throw new InvalidOperationException("The native owner inventory is unavailable."));

        tables.Add(Activator.CreateInstance(tableType, "dbo", child)
            ?? throw new InvalidOperationException("The dependent native key is unavailable."), dependent);
        tables.Add(Activator.CreateInstance(tableType, "dbo", parent)
            ?? throw new InvalidOperationException("The principal native key is unavailable."), principal);
        var names = (System.Collections.IDictionary)(type.GetField("_foreignKeyNames", flags)?.GetValue(graph)
            ?? throw new InvalidOperationException("The native FK name binding is unavailable."));

        names.Add(Activator.CreateInstance(foreignType, dependent, "FK_L")
            ?? throw new InvalidOperationException("The native FK physical key is unavailable."), identity);
        var edges = (Dictionary<int, SqlServerProjectedDependencyGraph.ForeignKeyEdge>)(
            type.GetField("_foreignKeys", flags)?.GetValue(graph)
            ?? throw new InvalidOperationException("The native FK endpoints are unavailable."));

        edges.Add(identity, new SqlServerProjectedDependencyGraph.ForeignKeyEdge(dependent, principal,
            ReferentialAction.NoAction, ReferentialAction.NoAction, 1));
    }

    /// <summary>Observes accepted-owner routing without making elapsed time or bytes a pass criterion.</summary>
    /// <param name="owners">The number of independent native CHECK/source owners.</param>
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    public void AcceptedRemovalObserverEvidenceIsInformational(int owners)
    {
        // Arrange
        using var context = Context();
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = new SafeMigrationOperation[owners * 2];
        var native = new SafeMigrationProviderAnalysis[operations.Length];
        for (var owner = 0; owner < owners; owner++)
        {
            var table = "owner_" + owner.ToString(CultureInfo.InvariantCulture);
            operations[owner * 2] = new SafeMigrationOperation(new DropCheckConstraintIntent("CK_L", table),
                SafeMigrationPolicy.ThrowIfDifferent);
            operations[owner * 2 + 1] = new SafeMigrationOperation(new DropColumnIntent("L", table),
                SafeMigrationPolicy.ThrowIfDifferent);
            native[owner * 2] = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None, false, "classified_matching");
            native[owner * 2 + 1] = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
                SafeMigrationRepairCapability.None, false, "column_drop_candidate_dependency");
        }

        var prepare = typeof(SqlServerSafeMigrationProviderAnalyzer).GetMethod("PrepareColumnDropConstraintCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The ordinary predecessor capture is unavailable.");

        prepare.CreateDelegate<Action<IReadOnlyList<SafeMigrationOperation>>>(analyzer)(operations);
        analyzer.CaptureProjectedComputedDropRefusals(operations, native);
        var matching = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Matching,
            SafeMigrationRepairCapability.None, false, "classified_matching");
        var decision = SafeMigrationDecisionPlanner.Plan(SafeMigrationOperationKind.DropCheckConstraint,
            matching.ObservedState, SafeMigrationPolicy.ThrowIfDifferent);
        var allocations = new long[3];
        var ticks = new long[3];
        var columns = new ObserverColumns();

        // Act
        for (var sample = 0; sample < allocations.Length; sample++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var owner = 0; owner < owners; owner++)
            {
                analyzer.ObserveAcceptedOperation(operations[owner * 2], matching, matching, decision);
            }

            ticks[sample] = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        }

        var results = operations.Where(static operation => operation.Intent is DropColumnIntent)
            .Select(operation => analyzer.ValidateProjectedOperation(operation, matching, columns)).ToArray();

        // Assert
        Assert.Equal(owners, results.Length);
        Assert.All(results, static result => Assert.Equal("projected_column_drop_safe", result.Code));
        var routesField = typeof(SqlServerSafeMigrationProviderAnalyzer).GetField("_nativeColumnDropOwnerRoutes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The owner-local proof routes are unavailable.");

        var routes = (System.Collections.IDictionary)(routesField.GetValue(analyzer)
            ?? throw new InvalidOperationException("The owner-local proof routes are empty."));

        var proofsField = typeof(SqlServerSafeMigrationProviderAnalyzer).GetField("_nativeComputedDropRefusals",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The original native proofs are unavailable.");

        var proofs = (System.Collections.IDictionary)(proofsField.GetValue(analyzer)
            ?? throw new InvalidOperationException("The original native proofs are empty."));

        Assert.Equal(owners, routes.Count);
        foreach (System.Collections.DictionaryEntry owner in routes)
        {
            var members = Assert.IsAssignableFrom<System.Collections.IList>(owner.Value);
            var member = Assert.IsAssignableFrom<System.Runtime.CompilerServices.ITuple>(Assert.Single(members));
            Assert.Same(proofs[Assert.IsType<SafeMigrationOperation>(member[0])], member[1]);
        }

        output.WriteLine("accepted-observer owners={0} bytes={1} ticks={2} frequency={3}", owners,
            string.Join(",", allocations), string.Join(",", ticks), System.Diagnostics.Stopwatch.Frequency);
    }

    private sealed class ObserverColumns : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(string table, string? schema, string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition)
        {
            definition = null;

            return false;
        }
    }

    private static SafeMigrationOperation DropIndex(string name)
        => new(new DropIndexIntent(name, "nodes"), SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationOperation DropColumn(string name)
        => new(new DropColumnIntent(name, "nodes"), SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationDbContext Context()
        => new("Server=127.0.0.1,1433;Database=offline_computed_drop;User ID=sa;"
            + "Password=unused;TrustServerCertificate=True");

    private static void Prepare(SqlServerSafeMigrationProviderAnalyzer analyzer,
        IReadOnlyList<SafeMigrationOperation> operations)
        => Preparation(analyzer)(operations);

    private static Action<IReadOnlyList<SafeMigrationOperation>> Preparation(
        SqlServerSafeMigrationProviderAnalyzer analyzer
    )
    {
        var method = typeof(SqlServerSafeMigrationProviderAnalyzer).GetMethod("PrepareComputedDropIndexCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The predecessor preparation is unavailable.");


        return method.CreateDelegate<Action<IReadOnlyList<SafeMigrationOperation>>>(analyzer);
    }

    private static Dictionary<SafeMigrationOperation, int[]> Captures(SqlServerSafeMigrationProviderAnalyzer analyzer)
    {
        var field = typeof(SqlServerSafeMigrationProviderAnalyzer).GetField("_computedDropIndexCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The predecessor capture is unavailable.");

        return (Dictionary<SafeMigrationOperation, int[]>)(field.GetValue(analyzer)
            ?? throw new InvalidOperationException("The predecessor capture is empty."));
    }
}
