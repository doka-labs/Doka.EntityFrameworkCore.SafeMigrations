namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    private static void Apply(
        SafeMigrationPreflightProjection projection,
        SafeMigrationIntent intent
    )
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));
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
            });
        projection.Observe(operation, analysis, decision);
    }

    private static void AssertMatching(
        SafeMigrationPreflightProjection projection,
        SafeMigrationIntent intent
    )
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
    }

    private static SafeMigrationProviderAnalysis Live(
        SafeMigrationObservedState state
    ) => new(state, SafeMigrationRepairCapability.None, postconditionSatisfied: false, "test_live");

    private static ExpectedColumnDefinition Column(
        string name
    ) => new(name, typeof(int), isNullable: false, storeType: "int");
}
