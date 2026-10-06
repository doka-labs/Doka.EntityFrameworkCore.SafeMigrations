namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedDataTests
{
    /// <summary>An empty evidence cache must not allocate per-row scratch storage before live fallback.</summary>
    /// <param name="kind">The managed operation family.</param>
    /// <param name="invalidated">Whether a preceding write requires deferred classification.</param>
    [Theory]
    [InlineData("ensure", false)]
    [InlineData("update", false)]
    [InlineData("delete", false)]
    [InlineData("ensure", true)]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    public void EmptyManagedEvidenceDoesNotAllocateWithBatchCardinality(
        string kind,
        bool invalidated
    )
    {
        // Arrange
        const int repetitions = 128;
        var projection = new SafeMigrationPreflightProjection();
        var small = ReviewManagedBatch(kind, 1);
        var large = ReviewManagedBatch(kind, 128);
        var live = Live(SafeMigrationObservedState.Matching);

        if (invalidated)
        {
            projection.ObserveDeferredModelManagedMutation();
        }

        _ = MeasureManagedProjection(projection, small, live, repetitions);
        _ = MeasureManagedProjection(projection, large, live, repetitions);

        // Act
        var smallResult = MeasureManagedProjection(projection, small, live, repetitions);
        var largeResult = MeasureManagedProjection(projection, large, live, repetitions);

        // Assert
        var expectedState = invalidated
            ? SafeMigrationObservedState.PrerequisiteMissing
            : SafeMigrationObservedState.Matching;

        Assert.Equal(repetitions * (int)expectedState, smallResult.StateSum);
        Assert.Equal(smallResult.StateSum, largeResult.StateSum);
        // WHY: Compare structural growth, not time or an exact zero-byte result.
        // The allowance tolerates runtime cache refreshes but cannot hide the
        // former 128-element state array allocated on every fallback call.
        Assert.True(largeResult.Bytes <= smallResult.Bytes + repetitions * 256L,
            $"Empty-cache projection allocated {smallResult.Bytes} bytes for single-row batches "
            + $"and {largeResult.Bytes} bytes for 128-row batches.");
    }

    /// <summary>Projected composite FK shapes use the same ordered-column contract as runtime guards.</summary>
    /// <param name="reverseDependent">Whether the frozen dependent columns change order.</param>
    /// <param name="reversePrincipal">Whether the frozen principal columns change order.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ProjectedCompositeDependencyPreservesRuntimeOrdinalContract(
        bool reverseDependent,
        bool reversePrincipal
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var foreignKey = Operation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "fk_composite_roles", "user_roles", ["role_id", "tenant_id"], "roles", ["id", "tenant_id"])));

        SideEffectsObserveAcceptedForeignKey(projection, foreignKey, replayedForeignKey: false);

        var deletion = Operation(new DeleteModelManagedDataIntent(
            "roles", ["id"], ["int"], new object?[,] { { 1 } },
            ["id", "tenant_id"], ["int", "int"], new object?[,] { { 1, 7 } }, schema: null,
            foreignKeys:
            [
                new ExpectedModelManagedDataForeignKeyDefinition(
                    "user_roles",
                    reverseDependent ? ["tenant_id", "role_id"] : ["role_id", "tenant_id"],
                    reversePrincipal ? ["tenant_id", "id"] : ["id", "tenant_id"]),
            ]));

        var live = Live(SafeMigrationObservedState.TransitionReady);

        // Act
        var projected = projection.Project(deletion, live);

        // Assert
        if (reverseDependent || reversePrincipal)
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, projected.ObservedState);
            Assert.Equal("projected_model_managed_dependency_unmodeled", projected.Code);
        }
        else
        {
            Assert.Same(live, projected);
        }
    }

    private static (long Bytes, int StateSum) MeasureManagedProjection(
        SafeMigrationPreflightProjection projection,
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis live,
        int repetitions
    )
    {
        var stateSum = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < repetitions; iteration++)
        {
            stateSum += (int)projection.Project(operation, live).ObservedState;
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before, stateSum);
    }

    private static SafeMigrationOperation ReviewManagedBatch(
        string kind,
        int rowCount
    )
    {
        var keys = new object?[rowCount, 1];
        var values = new object?[rowCount, 2];
        var names = new object?[rowCount, 1];

        for (var row = 0; row < rowCount; row++)
        {
            keys[row, 0] = row + 1;
            values[row, 0] = row + 1;
            values[row, 1] = "administrator";
            names[row, 0] = "administrator";
        }

        return kind switch
        {
            "ensure" => Operation(new EnsureModelManagedDataIntent(
                "roles", ["id"], ["int"], ["id", "name"], ["int", "varchar(64)"],
                values, schema: null, uniqueKeys: null)),
            "update" => Operation(new UpdateModelManagedDataIntent(
                "roles", ["id"], ["int"], keys, ["name"], ["varchar(64)"],
                names, names, schema: null, uniqueKeys: null)),
            "delete" => Operation(new DeleteModelManagedDataIntent(
                "roles", ["id"], ["int"], keys, ["id", "name"], ["int", "varchar(64)"],
                values, schema: null, foreignKeys: null)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
