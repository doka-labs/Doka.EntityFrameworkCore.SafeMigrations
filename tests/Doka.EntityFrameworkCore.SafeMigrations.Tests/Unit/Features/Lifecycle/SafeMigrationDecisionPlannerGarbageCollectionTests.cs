namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Isolates forced process-wide collections from concurrent tests.</summary>
[CollectionDefinition(nameof(SafeMigrationDecisionPlannerGarbageCollectionDefinition), DisableParallelization = true)]
public sealed class SafeMigrationDecisionPlannerGarbageCollectionDefinition;

/// <summary>Verifies action-only planning without relying on surviving runtime metadata caches.</summary>
[Collection(nameof(SafeMigrationDecisionPlannerGarbageCollectionDefinition))]
public sealed class SafeMigrationDecisionPlannerGarbageCollectionTests
{
    /// <summary>Preserves actions after one or repeated full collections discard weak runtime caches.</summary>
    /// <param name="collectionCount">The number of independently collected planning windows.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ActionOnly_PreservesRepairAfterGarbageCollection(
        int collectionCount
    )
    {
        // Arrange
        var initial = SafeMigrationDecisionPlanner.PlanAction(
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationObservedState.Different,
            SafeMigrationPolicy.RepairIfSafe,
            SafeMigrationRepairCapability.Safe);

        // Act
        var actions = PlanAfterCollections(collectionCount);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, initial);
        Assert.All(actions, action => Assert.Equal(SafeMigrationAction.Repair, action));
    }

    /// <summary>Captures synchronous planner results after each forced collection completes.</summary>
    /// <param name="collectionCount">The number of independently collected planning windows.</param>
    /// <returns>Every planned action after collection.</returns>
    private static SafeMigrationAction[] PlanAfterCollections(
        int collectionCount
    )
    {
        var actions = new SafeMigrationAction[collectionCount * 1000];

        for (var collection = 0; collection < collectionCount; collection++)
        {
            // WHY: Runtime enum metadata can disappear after an earlier planning call.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            for (var index = 0; index < 1000; index++)
            {
                actions[collection * 1000 + index] = SafeMigrationDecisionPlanner.PlanAction(
                    SafeMigrationOperationKind.EnsureColumn,
                    SafeMigrationObservedState.Different,
                    SafeMigrationPolicy.RepairIfSafe,
                    SafeMigrationRepairCapability.Safe);
            }
        }

        return actions;
    }
}
