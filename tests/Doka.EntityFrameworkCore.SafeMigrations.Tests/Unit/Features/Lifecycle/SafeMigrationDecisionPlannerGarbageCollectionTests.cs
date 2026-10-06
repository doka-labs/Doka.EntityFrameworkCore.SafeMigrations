namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Isolates process-wide collections from concurrent allocation measurements.</summary>
[CollectionDefinition(nameof(SafeMigrationDecisionPlannerGarbageCollectionDefinition), DisableParallelization = true)]
public sealed class SafeMigrationDecisionPlannerGarbageCollectionDefinition;

/// <summary>Verifies action-only planning without relying on surviving runtime metadata caches.</summary>
[Collection(nameof(SafeMigrationDecisionPlannerGarbageCollectionDefinition))]
public sealed class SafeMigrationDecisionPlannerGarbageCollectionTests
{
    /// <summary>Remains allocation-free after one or repeated full collections discard weak runtime caches.</summary>
    /// <param name="collectionCount">The number of independently collected measurement windows.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ActionOnly_DoesNotAllocateAfterGarbageCollection(
        int collectionCount
    )
    {
        // Arrange
        _ = SafeMigrationDecisionPlanner.PlanAction(
            SafeMigrationOperationKind.EnsureColumn, SafeMigrationObservedState.Different,
            SafeMigrationPolicy.RepairIfSafe, SafeMigrationRepairCapability.Safe);

        // Act
        var result = MeasureAfterCollections(collectionCount);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, result.Action);
        Assert.Equal(0, result.Bytes);
    }

    /// <summary>Measures only synchronous planner calls after each forced collection completes.</summary>
    /// <param name="collectionCount">The number of independently collected measurement windows.</param>
    /// <returns>The last action and the sum of planner allocations across all windows.</returns>
    private static (SafeMigrationAction Action, long Bytes) MeasureAfterCollections(
        int collectionCount
    )
    {
        var action = default(SafeMigrationAction);
        var bytes = 0L;

        for (var collection = 0; collection < collectionCount; collection++)
        {
            // WHY: Runtime enum metadata can disappear despite warming the planner.
            // Isolate process-wide GC from other tests and exclude collection work
            // from the allocation window rather than relaxing the zero-byte contract.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var index = 0; index < 1000; index++)
            {
                action = SafeMigrationDecisionPlanner.PlanAction(
                    SafeMigrationOperationKind.EnsureColumn, SafeMigrationObservedState.Different,
                    SafeMigrationPolicy.RepairIfSafe, SafeMigrationRepairCapability.Safe);
            }

            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return (action, bytes);
    }
}
