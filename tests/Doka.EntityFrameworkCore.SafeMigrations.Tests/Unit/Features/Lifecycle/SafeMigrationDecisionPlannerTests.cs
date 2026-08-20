namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationDecisionPlannerTests
{
    [Fact]
    public void Planner_IsTotalForEveryDefinedInputCombination()
    {
        foreach (var kind in Enum.GetValues<SafeMigrationOperationKind>())
        {
            foreach (var state in Enum.GetValues<SafeMigrationObservedState>())
            {
                foreach (var policy in Enum.GetValues<SafeMigrationPolicy>())
                {
                    foreach (var repair in Enum.GetValues<SafeMigrationRepairCapability>())
                    {
                        var decision = SafeMigrationDecisionPlanner.Plan(kind, state, policy, repair);

                        Assert.True(Enum.IsDefined(decision.Action));
                        Assert.False(string.IsNullOrWhiteSpace(decision.Code));
                    }
                }
            }
        }
    }

    [Fact]
    public void UnsupportedAndDataBlocked_AlwaysReject()
    {
        foreach (var kind in Enum.GetValues<SafeMigrationOperationKind>())
        {
            foreach (var policy in Enum.GetValues<SafeMigrationPolicy>())
            {
                Assert.Equal(
                    SafeMigrationAction.RejectUnsupported,
                    SafeMigrationDecisionPlanner.Plan(kind, SafeMigrationObservedState.Unsupported, policy)
                        .Action);
                Assert.Equal(
                    SafeMigrationAction.RejectDataBlocked,
                    SafeMigrationDecisionPlanner.Plan(kind, SafeMigrationObservedState.DataBlocked, policy)
                        .Action);
            }
        }
    }

    [Fact]
    public void RepairRequiresExplicitPolicyAndProvenCapability()
    {
        Assert.Equal(
            SafeMigrationAction.Repair,
            SafeMigrationDecisionPlanner.Plan(
                    SafeMigrationOperationKind.EnsureColumn,
                    SafeMigrationObservedState.Different,
                    SafeMigrationPolicy.RepairIfSafe,
                    SafeMigrationRepairCapability.Safe)
                .Action);
        Assert.Equal(
            SafeMigrationAction.RejectDifferent,
            SafeMigrationDecisionPlanner.Plan(
                    SafeMigrationOperationKind.EnsureColumn,
                    SafeMigrationObservedState.Different,
                    SafeMigrationPolicy.RepairIfSafe,
                    SafeMigrationRepairCapability.None)
                .Action);
        Assert.Equal(
            SafeMigrationAction.RejectDifferent,
            SafeMigrationDecisionPlanner.Plan(
                    SafeMigrationOperationKind.EnsureColumn,
                    SafeMigrationObservedState.Different,
                    SafeMigrationPolicy.ThrowIfDifferent,
                    SafeMigrationRepairCapability.Safe)
                .Action);
    }
}
