namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Unit;

public sealed class SafeMigrationDecisionPlannerTests
{
    [Fact]
    public void MissingObject_PlansCreateAndExecution()
    {
        var decision = SafeMigrationDecisionPlanner.Plan(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.ThrowIfDifferent),
            SafeMigrationComparisonState.Missing);

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.CreateMissingObject, decision.PlannedAction);
        Assert.True(decision.ShouldExecute);
    }

    [Fact]
    public void MatchingObject_PlansMatchWithoutExecution()
    {
        var decision = SafeMigrationDecisionPlanner.Plan(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Matches);

        Assert.Equal(SafeMigrationExecutionOutcome.Matched, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.None, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void DifferentObject_WithNoneConflictMode_PlansNoOp()
    {
        var decision = SafeMigrationDecisionPlanner.Plan(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.None),
            SafeMigrationComparisonState.Different);

        Assert.Equal(SafeMigrationExecutionOutcome.NoOp, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.None, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void DifferentObject_WithThrowIfDifferent_PlansReject()
    {
        var decision = SafeMigrationDecisionPlanner.Plan(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.ThrowIfDifferent),
            SafeMigrationComparisonState.Different);

        Assert.Equal(SafeMigrationExecutionOutcome.Rejected, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.Reject, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void DifferentObject_WithRepairIfPossibleAndUnsafeRepair_PlansReject()
    {
        var decision = SafeMigrationDecisionPlanner.Plan(
            new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible),
            SafeMigrationComparisonState.Different);

        Assert.Equal(SafeMigrationExecutionOutcome.Rejected, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.Reject, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
    }

    [Fact]
    public void PreflightCreate_DoesNotExecuteDdl()
    {
        var decision = SafeMigrationDecisionPlanner.Plan(
            new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.ThrowIfDifferent,
                PreflightOnly: true),
            SafeMigrationComparisonState.Missing);

        Assert.Equal(SafeMigrationExecutionOutcome.Created, decision.Outcome);
        Assert.Equal(SafeMigrationPlannedAction.CreateMissingObject, decision.PlannedAction);
        Assert.False(decision.ShouldExecute);
    }

}
