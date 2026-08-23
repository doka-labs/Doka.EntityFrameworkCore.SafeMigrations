namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    [Fact]
    public void SchemaProjection_PreservesTheProviderClassification()
    {
        SafeMigrationIntent[] intents = [new EnsureSchemaIntent("app"), new DropSchemaIntent("app"),];

        var projection = new SafeMigrationPreflightProjection();

        foreach (var intent in intents)
        {
            var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
            var live = new SafeMigrationProviderAnalysis(
                SafeMigrationObservedState.Matching,
                SafeMigrationRepairCapability.None,
                postconditionSatisfied: true,
                "provider_schema_state");

            var projected = projection.Project(operation, live);
            var decision = SafeMigrationDecisionPlanner.Plan(
                intent.Kind,
                projected.ObservedState,
                operation.Policy,
                projected.RepairCapability);

            projection.Observe(operation, projected, decision);

            Assert.Same(live, projected);
        }
    }
}
