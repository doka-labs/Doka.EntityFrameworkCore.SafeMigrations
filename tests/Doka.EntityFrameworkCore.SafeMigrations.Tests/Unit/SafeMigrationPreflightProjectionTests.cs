namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Exercises ordered physical lifetimes, identity aliases and data-proof invalidation without executing database DDL.
/// </summary>
public sealed partial class SafeMigrationPreflightProjectionTests
{
    private static void Apply(
        SafeMigrationPreflightProjection projection,
        SafeMigrationIntent intent
    )
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var liveAnalysis = Live(SafeMigrationObservedState.Missing);
        var analysis = projection.Project(operation, liveAnalysis);
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        Assert.DoesNotContain(
            decision.Action,
            new[]
            {
                SafeMigrationAction.RejectDifferent,
                SafeMigrationAction.RejectUnsupported,
                SafeMigrationAction.RejectDataBlocked,
                SafeMigrationAction.RejectPrerequisiteMissing,
            });

        projection.Observe(operation, liveAnalysis, analysis, decision);
    }

    private static void ObserveAccepted(
        SafeMigrationPreflightProjection projection,
        SafeMigrationIntent intent,
        SafeMigrationObservedState liveState
    ) => ObserveAccepted(
        projection,
        intent,
        liveState,
        liveState == SafeMigrationObservedState.Matching
            ? intent.ObjectName
            : null);

    private static void ObserveAccepted(
        SafeMigrationPreflightProjection projection,
        SafeMigrationIntent intent,
        SafeMigrationObservedState liveState,
        string? matchedObjectName
    )
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var liveAnalysis = Live(liveState, matchedObjectName);
        var analysis = projection.Project(operation, liveAnalysis);
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        Assert.Contains(decision.Action, new[] { SafeMigrationAction.Apply, SafeMigrationAction.NoOp });
        projection.Observe(operation, liveAnalysis, analysis, decision);
    }

    private static SafeMigrationProviderAnalysis Live(
        SafeMigrationObservedState state,
        string? matchedObjectName = null
    ) => new(state, SafeMigrationRepairCapability.None, postconditionSatisfied: false, "test_live")
    {
        MatchedObjectName = matchedObjectName,
        RenameTargetExists = false,
    };

    private static ExpectedColumnDefinition Column(
        string name
    ) => new(name, typeof(int), isNullable: false, storeType: "int");
}
