namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Measures projection allocations using prepared operations independently of provider latency.</summary>
/// <param name="output">The diagnostic output retaining the observed allocation count.</param>
public sealed class SafeMigrationProjectionAllocationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Accepted column streams retain exact evidence with bounded per-column storage.</summary>
    [Fact]
    public void AcceptedColumnsKeepPreparedProjectionAllocationsBounded()
    {
        // Arrange
        const int count = 1_000;
        var operations = Enumerable.Range(0, count)
            .Select(index => new SafeMigrationOperation(
                new EnsureColumnIntent(
                    "items", new ExpectedColumnDefinition($"value_{index}", typeof(int), true, "int")),
                SafeMigrationPolicy.ThrowIfDifferent))
            .ToArray();

        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Missing, SafeMigrationRepairCapability.None, false, "live_missing");

        var decision = SafeMigrationDecisionPlanner.Plan(
            SafeMigrationOperationKind.EnsureColumn, live.ObservedState, SafeMigrationPolicy.ThrowIfDifferent);

        _ = ProjectColumns(operations, live, decision);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        var matching = ProjectColumns(operations, live, decision);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        output.WriteLine($"Prepared column projection: {allocated} bytes for {count} accepted/matching columns.");
        Assert.Equal(count, matching);
        // WHY: Leave headroom for runtime bookkeeping, but detect the previous
        // per-column object, closure, iterator and result-code allocations.
        Assert.InRange(allocated, 0, 450_000);
    }

    private static int ProjectColumns(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis live,
        SafeMigrationDecision decision
    )
    {
        var projection = new SafeMigrationPreflightProjection();
        var matching = 0;
        foreach (var operation in operations)
        {
            projection.Observe(operation, live, live, decision);
            matching += projection.Project(operation, live).ObservedState == SafeMigrationObservedState.Matching
                ? 1 : 0;
        }

        return matching;
    }
}
