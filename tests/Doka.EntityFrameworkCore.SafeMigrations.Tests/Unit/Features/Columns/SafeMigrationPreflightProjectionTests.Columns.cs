namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    [Fact]
    public void ProjectionProvesRepairOnlyAgainstTheProjectedOldDefinition()
    {
        var projection = new SafeMigrationPreflightProjection();
        var oldColumn = new ExpectedColumnDefinition(
            "value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40);
        var target = new ExpectedColumnDefinition(
            "value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40,
            comment: "canonical");
        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("items", [oldColumn]),
                SafeMigrationTableMode.StrictDefinition));

        var intent = new AlterColumnIntent("items", target, oldColumn);
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.Safe, analysis.RepairCapability);
        Assert.Equal(
            SafeMigrationAction.Repair,
            SafeMigrationDecisionPlanner.Plan(
                    intent.Kind,
                    analysis.ObservedState,
                    operation.Policy,
                    analysis.RepairCapability)
                .Action);
    }
}
