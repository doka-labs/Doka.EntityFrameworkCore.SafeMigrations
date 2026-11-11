namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies that action-only rendering shares the public planner's complete contract.</summary>
public sealed class SafeMigrationDecisionPlannerActionTests
{
    /// <summary>Gets every defined policy-table input without relying on a provider-specific subset.</summary>
    public static TheoryData<SafeMigrationOperationKind, SafeMigrationObservedState,
        SafeMigrationPolicy, SafeMigrationRepairCapability> DefinedInputs
    {
        get
        {
            var result = new TheoryData<SafeMigrationOperationKind, SafeMigrationObservedState,
                SafeMigrationPolicy, SafeMigrationRepairCapability>();

            foreach (var kind in Enum.GetValues<SafeMigrationOperationKind>())
            {
                foreach (var state in Enum.GetValues<SafeMigrationObservedState>())
                {
                    foreach (var policy in Enum.GetValues<SafeMigrationPolicy>())
                    {
                        foreach (var repair in Enum.GetValues<SafeMigrationRepairCapability>())
                        {
                            result.Add(kind, state, policy, repair);
                        }
                    }
                }
            }

            return result;
        }
    }

    /// <summary>Uses the same action for every input while retaining fresh public results and stable codes.</summary>
    /// <param name="kind">The defined operation family.</param>
    /// <param name="state">The defined observed state.</param>
    /// <param name="policy">The defined policy.</param>
    /// <param name="repair">The defined repair capability.</param>
    [Theory]
    [MemberData(nameof(DefinedInputs))]
    public void ActionOnly_MatchesPublicDecision(
        SafeMigrationOperationKind kind,
        SafeMigrationObservedState state,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repair
    )
    {
        // Arrange
        var expected = SafeMigrationDecisionPlanner.Plan(kind, state, policy, repair);

        // Act
        var actual = SafeMigrationDecisionPlanner.PlanAction(kind, state, policy, repair);
        var repeated = SafeMigrationDecisionPlanner.Plan(kind, state, policy, repair);

        // Assert
        Assert.Equal(expected.Action, actual);
        Assert.Equal(expected.Action, repeated.Action);
        Assert.Equal(expected.Code, repeated.Code);
        Assert.NotSame(expected, repeated);
    }

    /// <summary>Rejects invalid enum inputs in the original parameter-validation order.</summary>
    /// <param name="kind">The numeric operation kind.</param>
    /// <param name="state">The numeric observed state.</param>
    /// <param name="policy">The numeric policy.</param>
    /// <param name="repair">The numeric repair capability.</param>
    /// <param name="parameter">The first invalid parameter in the canonical validation order.</param>
    [Theory]
    [InlineData(-1, -1, -1, -1, "operationKind")]
    [InlineData(0, -1, -1, -1, "observedState")]
    [InlineData(0, 0, -1, -1, "policy")]
    [InlineData(0, 0, 0, -1, "repairCapability")]
    [InlineData(23, 7, 3, 2, "operationKind")]
    [InlineData(int.MinValue, 0, 0, 0, "operationKind")]
    [InlineData(int.MaxValue, 0, 0, 0, "operationKind")]
    [InlineData(0, 7, 3, 2, "observedState")]
    [InlineData(0, int.MinValue, 0, 0, "observedState")]
    [InlineData(0, int.MaxValue, 0, 0, "observedState")]
    [InlineData(0, 0, 3, 2, "policy")]
    [InlineData(0, 0, int.MinValue, 0, "policy")]
    [InlineData(0, 0, int.MaxValue, 0, "policy")]
    [InlineData(0, 0, 0, 2, "repairCapability")]
    [InlineData(0, 0, 0, int.MinValue, "repairCapability")]
    [InlineData(0, 0, 0, int.MaxValue, "repairCapability")]
    public void ActionOnly_PreservesInvalidInputPrecedence(
        int kind,
        int state,
        int policy,
        int repair,
        string parameter
    )
    {
        // Arrange
        var operationKind = (SafeMigrationOperationKind)kind;
        var observedState = (SafeMigrationObservedState)state;
        var conflictPolicy = (SafeMigrationPolicy)policy;
        var repairCapability = (SafeMigrationRepairCapability)repair;

        // Act
        var publicException = Record.Exception(() =>
            SafeMigrationDecisionPlanner.Plan(operationKind, observedState, conflictPolicy, repairCapability));

        var actionException = Record.Exception(() =>
            SafeMigrationDecisionPlanner.PlanAction(operationKind, observedState, conflictPolicy, repairCapability));

        // Assert
        Assert.Equal(parameter,
            Assert.IsType<ArgumentOutOfRangeException>(publicException)
                .ParamName);

        Assert.Equal(parameter,
            Assert.IsType<ArgumentOutOfRangeException>(actionException)
                .ParamName);
    }

    /// <summary>Repeated public and action-only planning preserve the canonical repair action.</summary>
    [Fact]
    public void ActionOnly_RepeatedPlanningMatchesPublicRepair()
    {
        // Arrange
        const int count = 1000;

        static SafeMigrationAction PublicPlan() => SafeMigrationDecisionPlanner.Plan(
                SafeMigrationOperationKind.EnsureColumn,
                SafeMigrationObservedState.Different,
                SafeMigrationPolicy.RepairIfSafe,
                SafeMigrationRepairCapability.Safe)
            .Action;

        static SafeMigrationAction ActionPlan() => SafeMigrationDecisionPlanner.PlanAction(
            SafeMigrationOperationKind.EnsureColumn,
            SafeMigrationObservedState.Different,
            SafeMigrationPolicy.RepairIfSafe,
            SafeMigrationRepairCapability.Safe);

        // Act
        var publicResult = RepeatPlanning(PublicPlan, count);
        var actionResult = RepeatPlanning(ActionPlan, count);

        // Assert
        Assert.All(publicResult, action => Assert.Equal(SafeMigrationAction.Repair, action));
        Assert.Equal(publicResult, actionResult);
    }

    /// <summary>Captures each repeated synchronous planning result.</summary>
    private static SafeMigrationAction[] RepeatPlanning(
        Func<SafeMigrationAction> planner,
        int count
    )
    {
        var actions = new SafeMigrationAction[count];
        for (var index = 0; index < count; index++)
        {
            actions[index] = planner();
        }

        return actions;
    }
}
