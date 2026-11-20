namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Pins every defined policy-table input and invalid-enum rejection against an independently declared decision oracle.
/// </summary>
public sealed class SafeMigrationDecisionPlannerTests
{
    /// <summary>Matches the independent planner oracle for every defined input tuple.</summary>
    /// <param name="kind">The operation family.</param>
    /// <param name="state">The observed catalog state.</param>
    /// <param name="policy">The authored migration policy.</param>
    /// <param name="repair">The separately established repair capability.</param>
    [Theory]
    [MemberData(nameof(DefinedInputCombinations))]
    public void Planner_ProducesExpectedDecisionForEveryDefinedInputCombination(
        SafeMigrationOperationKind kind,
        SafeMigrationObservedState state,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repair
    )
    {
        // Arrange
        var expected = ExpectedDecision(kind, state, policy, repair);

        // Act
        var decision = SafeMigrationDecisionPlanner.Plan(kind, state, policy, repair);

        // Assert
        Assert.Equal(expected.Action, decision.Action);
        Assert.Equal(expected.Code, decision.Code);
    }

    /// <summary>Rejects each terminal failure independently of operation family and policy.</summary>
    /// <param name="kind">The operation family.</param>
    /// <param name="policy">The authored migration policy.</param>
    /// <param name="state">The terminal failure state.</param>
    /// <param name="expected">The exact required rejection action.</param>
    [Theory]
    [MemberData(nameof(TerminalFailureInputs))]
    public void TerminalFailureStates_AlwaysReject(
        SafeMigrationOperationKind kind,
        SafeMigrationPolicy policy,
        SafeMigrationObservedState state,
        SafeMigrationAction expected
    )
    {
        // Arrange: The theory supplies the terminal state and exact expected action.

        // Act
        var decision = SafeMigrationDecisionPlanner.Plan(kind, state, policy);

        // Assert
        Assert.Equal(expected, decision.Action);
    }

    /// <summary>Requires both an explicit repair policy and independently proven repair capability.</summary>
    /// <param name="policy">The authored migration policy.</param>
    /// <param name="repair">The established repair capability.</param>
    /// <param name="expected">The exact planner action.</param>
    [Theory]
    [InlineData(SafeMigrationPolicy.RepairIfSafe, SafeMigrationRepairCapability.Safe, SafeMigrationAction.Repair)]
    [InlineData(SafeMigrationPolicy.RepairIfSafe,
        SafeMigrationRepairCapability.None,
        SafeMigrationAction.RejectDifferent)]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent,
        SafeMigrationRepairCapability.Safe,
        SafeMigrationAction.RejectDifferent)]
    public void RepairRequiresExplicitPolicyAndProvenCapability(
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repair,
        SafeMigrationAction expected
    )
    {
        // Arrange
        const SafeMigrationOperationKind kind = SafeMigrationOperationKind.EnsureColumn;
        const SafeMigrationObservedState state = SafeMigrationObservedState.Different;

        // Act
        var decision = SafeMigrationDecisionPlanner.Plan(kind, state, policy, repair);

        // Assert
        Assert.Equal(expected, decision.Action);
    }

    /// <summary>Enumerates every originally covered planner tuple as a discoverable independent case.</summary>
    /// <returns>The complete operation, state, policy and repair Cartesian product.</returns>
    public static IEnumerable<object[]> DefinedInputCombinations()
    {
        foreach (var kind in Enum.GetValues<SafeMigrationOperationKind>())
        {
            foreach (var state in Enum.GetValues<SafeMigrationObservedState>())
            {
                foreach (var policy in Enum.GetValues<SafeMigrationPolicy>())
                {
                    foreach (var repair in Enum.GetValues<SafeMigrationRepairCapability>())
                    {
                        yield return [kind, state, policy, repair];
                    }
                }
            }
        }
    }

    /// <summary>Enumerates the complete terminal-failure matrix with exact rejection actions.</summary>
    /// <returns>Every operation and policy paired with each terminal failure and expected rejection.</returns>
    public static IEnumerable<object[]> TerminalFailureInputs()
    {
        foreach (var kind in Enum.GetValues<SafeMigrationOperationKind>())
        {
            foreach (var policy in Enum.GetValues<SafeMigrationPolicy>())
            {
                yield return
                [
                    kind, policy, SafeMigrationObservedState.Unsupported,
                    SafeMigrationAction.RejectUnsupported,
                ];

                yield return
                [
                    kind, policy, SafeMigrationObservedState.DataBlocked,
                    SafeMigrationAction.RejectDataBlocked,
                ];

                yield return
                [
                    kind, policy, SafeMigrationObservedState.PrerequisiteMissing,
                    SafeMigrationAction.RejectPrerequisiteMissing,
                ];
            }
        }
    }

    /// <summary>Rejects an undefined operationKind before producing a decision.</summary>
    [Fact]
    public void Planner_RejectsUndefinedOperationKind()
    {
        // Arrange: The operation below supplies one invalid enum component.

        // Act
        var exception = Record.Exception(() => SafeMigrationDecisionPlanner.Plan(
            (SafeMigrationOperationKind)(-1),
            SafeMigrationObservedState.Missing,
            SafeMigrationPolicy.ExistenceOnly));

        // Assert
        Assert.Equal("operationKind",
            Assert.IsType<ArgumentOutOfRangeException>(exception)
                .ParamName);
    }

    /// <summary>Rejects an undefined observedState before producing a decision.</summary>
    [Fact]
    public void Planner_RejectsUndefinedObservedState()
    {
        // Arrange: The operation below supplies one invalid enum component.

        // Act
        var exception = Record.Exception(() => SafeMigrationDecisionPlanner.Plan(
            SafeMigrationOperationKind.EnsureTable,
            (SafeMigrationObservedState)(-1),
            SafeMigrationPolicy.ExistenceOnly));

        // Assert
        Assert.Equal("observedState",
            Assert.IsType<ArgumentOutOfRangeException>(exception)
                .ParamName);
    }

    /// <summary>Rejects an undefined policy before producing a decision.</summary>
    [Fact]
    public void Planner_RejectsUndefinedPolicy()
    {
        // Arrange: The operation below supplies one invalid enum component.

        // Act
        var exception = Record.Exception(() => SafeMigrationDecisionPlanner.Plan(
            SafeMigrationOperationKind.EnsureTable,
            SafeMigrationObservedState.Missing,
            (SafeMigrationPolicy)(-1)));

        // Assert
        Assert.Equal("policy",
            Assert.IsType<ArgumentOutOfRangeException>(exception)
                .ParamName);
    }

    /// <summary>Rejects an undefined repairCapability before producing a decision.</summary>
    [Fact]
    public void Planner_RejectsUndefinedRepairCapability()
    {
        // Arrange: The operation below supplies one invalid enum component.

        // Act
        var exception = Record.Exception(() => SafeMigrationDecisionPlanner.Plan(
            SafeMigrationOperationKind.EnsureTable,
            SafeMigrationObservedState.Missing,
            SafeMigrationPolicy.ExistenceOnly,
            (SafeMigrationRepairCapability)(-1)));

        // Assert
        Assert.Equal("repairCapability",
            Assert.IsType<ArgumentOutOfRangeException>(exception)
                .ParamName);
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
