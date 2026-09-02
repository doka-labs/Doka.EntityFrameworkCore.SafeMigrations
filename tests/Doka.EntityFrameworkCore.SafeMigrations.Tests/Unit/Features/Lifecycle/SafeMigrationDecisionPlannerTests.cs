namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationDecisionPlannerTests
{
    [Fact]
    public void Planner_ProducesExpectedDecisionForEveryDefinedInputCombination()
    {
        foreach (var kind in Enum.GetValues<SafeMigrationOperationKind>())
        {
            foreach (var state in Enum.GetValues<SafeMigrationObservedState>())
            {
                foreach (var policy in Enum.GetValues<SafeMigrationPolicy>())
                {
                    foreach (var repair in Enum.GetValues<SafeMigrationRepairCapability>())
                    {
                        var expected = ExpectedDecision(kind, state, policy, repair);
                        var decision = SafeMigrationDecisionPlanner.Plan(kind, state, policy, repair);

                        Assert.Equal(expected.Action, decision.Action);
                        Assert.Equal(expected.Code, decision.Code);
                    }
                }
            }
        }
    }

    [Fact]
    public void TerminalFailureStates_AlwaysReject()
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
                Assert.Equal(
                    SafeMigrationAction.RejectPrerequisiteMissing,
                    SafeMigrationDecisionPlanner.Plan(kind, SafeMigrationObservedState.PrerequisiteMissing, policy)
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

    [Fact]
    public void Planner_RejectsUndefinedOperationKind()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => SafeMigrationDecisionPlanner.Plan(
            (SafeMigrationOperationKind)(-1),
            SafeMigrationObservedState.Missing,
            SafeMigrationPolicy.ExistenceOnly));

        Assert.Equal("operationKind", exception.ParamName);
    }

    [Fact]
    public void Planner_RejectsUndefinedObservedState()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => SafeMigrationDecisionPlanner.Plan(
            SafeMigrationOperationKind.EnsureTable,
            (SafeMigrationObservedState)(-1),
            SafeMigrationPolicy.ExistenceOnly));

        Assert.Equal("observedState", exception.ParamName);
    }

    [Fact]
    public void Planner_RejectsUndefinedPolicy()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => SafeMigrationDecisionPlanner.Plan(
            SafeMigrationOperationKind.EnsureTable,
            SafeMigrationObservedState.Missing,
            (SafeMigrationPolicy)(-1)));

        Assert.Equal("policy", exception.ParamName);
    }

    [Fact]
    public void Planner_RejectsUndefinedRepairCapability()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => SafeMigrationDecisionPlanner.Plan(
            SafeMigrationOperationKind.EnsureTable,
            SafeMigrationObservedState.Missing,
            SafeMigrationPolicy.ExistenceOnly,
            (SafeMigrationRepairCapability)(-1)));

        Assert.Equal("repairCapability", exception.ParamName);
    }

    private static (SafeMigrationAction Action, string Code) ExpectedDecision(
        SafeMigrationOperationKind operationKind,
        SafeMigrationObservedState observedState,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability
    ) => observedState switch
    {
        SafeMigrationObservedState.Missing when IsDrop(operationKind) => (SafeMigrationAction.NoOp, "missing_noop"),
        SafeMigrationObservedState.Missing when IsRename(operationKind) =>
            (SafeMigrationAction.NoOp, "source_missing_noop"),
        SafeMigrationObservedState.Missing when operationKind == SafeMigrationOperationKind.AlterColumn =>
            (SafeMigrationAction.RejectDifferent, "alter_target_missing"),
        SafeMigrationObservedState.Missing when operationKind == SafeMigrationOperationKind.UpdateModelManagedData =>
            (SafeMigrationAction.RejectPrerequisiteMissing, "missing_model_managed_row"),
        SafeMigrationObservedState.Missing when operationKind == SafeMigrationOperationKind.DeleteModelManagedData =>
            (SafeMigrationAction.NoOp, "missing_noop"),
        SafeMigrationObservedState.Missing => (SafeMigrationAction.Apply, "missing_apply"),
        SafeMigrationObservedState.Matching when IsDrop(operationKind) => (SafeMigrationAction.Apply, "existing_drop"),
        SafeMigrationObservedState.Matching when IsRename(operationKind) =>
            (SafeMigrationAction.Apply, "source_exists_rename"),
        SafeMigrationObservedState.Matching => (SafeMigrationAction.NoOp, "matching_noop"),
        SafeMigrationObservedState.Different when IsDrop(operationKind) =>
            (SafeMigrationAction.RejectDifferent, "wrong_object_kind"),
        SafeMigrationObservedState.Different when IsRename(operationKind) =>
            (SafeMigrationAction.RejectDifferent, "rename_target_conflict"),
        SafeMigrationObservedState.Different when
            operationKind == SafeMigrationOperationKind.AlterColumn
            && policy == SafeMigrationPolicy.RepairIfSafe
            && repairCapability == SafeMigrationRepairCapability.Safe =>
            (SafeMigrationAction.Repair, "different_repair"),
        SafeMigrationObservedState.Different when operationKind == SafeMigrationOperationKind.AlterColumn =>
            (SafeMigrationAction.RejectDifferent, "alter_not_approved"),
        SafeMigrationObservedState.Different when IsModelManagedData(operationKind) =>
            (SafeMigrationAction.RejectDifferent, "different_reject"),
        SafeMigrationObservedState.Different when policy == SafeMigrationPolicy.ExistenceOnly =>
            (SafeMigrationAction.NoOp, "existing_existence_noop"),
        SafeMigrationObservedState.Different when policy == SafeMigrationPolicy.ThrowIfDifferent =>
            (SafeMigrationAction.RejectDifferent, "different_reject"),
        SafeMigrationObservedState.Different when repairCapability == SafeMigrationRepairCapability.Safe =>
            (SafeMigrationAction.Repair, "different_repair"),
        SafeMigrationObservedState.Different => (SafeMigrationAction.RejectDifferent, "different_no_safe_repair"),
        SafeMigrationObservedState.Unsupported => (SafeMigrationAction.RejectUnsupported, "unsupported"),
        SafeMigrationObservedState.DataBlocked => (SafeMigrationAction.RejectDataBlocked, "data_blocked"),
        SafeMigrationObservedState.PrerequisiteMissing => (SafeMigrationAction.RejectPrerequisiteMissing,
            "prerequisite_missing"),
        SafeMigrationObservedState.TransitionReady
            when operationKind is SafeMigrationOperationKind.UpdateModelManagedData
                or SafeMigrationOperationKind.DeleteModelManagedData =>
            (SafeMigrationAction.Apply, "transition_ready_apply"),
        SafeMigrationObservedState.TransitionReady =>
            (SafeMigrationAction.RejectUnsupported, "transition_state_invalid"),
        _ => throw new ArgumentOutOfRangeException(nameof(observedState)),
    };

    private static bool IsDrop(
        SafeMigrationOperationKind operationKind
    ) => operationKind is SafeMigrationOperationKind.DropSchema
        or SafeMigrationOperationKind.DropTable
        or SafeMigrationOperationKind.DropColumn
        or SafeMigrationOperationKind.DropIndex
        or SafeMigrationOperationKind.DropPrimaryKey
        or SafeMigrationOperationKind.DropUniqueConstraint
        or SafeMigrationOperationKind.DropCheckConstraint
        or SafeMigrationOperationKind.DropForeignKey;

    private static bool IsRename(
        SafeMigrationOperationKind operationKind
    ) => operationKind is SafeMigrationOperationKind.RenameTable
        or SafeMigrationOperationKind.RenameColumn
        or SafeMigrationOperationKind.RenameIndex;

    private static bool IsModelManagedData(
        SafeMigrationOperationKind operationKind
    ) => operationKind is SafeMigrationOperationKind.EnsureModelManagedData
        or SafeMigrationOperationKind.UpdateModelManagedData
        or SafeMigrationOperationKind.DeleteModelManagedData;
}
