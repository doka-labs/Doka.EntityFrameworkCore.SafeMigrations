namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Selects a provider-neutral action from operation kind, observed state,
/// policy and proven repair capability without performing I/O.
/// </summary>
public static class SafeMigrationDecisionPlanner
{
    /// <summary>Selects the deterministic action for one classified operation.</summary>
    /// <param name="operationKind">The SafeMigrations operation family.</param>
    /// <param name="observedState">The provider-classified live state.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <param name="repairCapability">The provider-proven repair capability.</param>
    /// <returns>The deterministic provider-neutral decision.</returns>
    public static SafeMigrationDecision Plan(
        SafeMigrationOperationKind operationKind,
        SafeMigrationObservedState observedState,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability = SafeMigrationRepairCapability.None
    )
    {
        Validate(operationKind, observedState, policy, repairCapability);

        return observedState switch
        {
            SafeMigrationObservedState.Unsupported => Decision(SafeMigrationAction.RejectUnsupported, "unsupported"),
            SafeMigrationObservedState.DataBlocked => Decision(SafeMigrationAction.RejectDataBlocked, "data_blocked"),
            _ when IsDrop(operationKind) => PlanDrop(observedState),
            _ when IsRename(operationKind) => PlanRename(observedState),
            _ when operationKind == SafeMigrationOperationKind.AlterColumn => PlanAlter(
                observedState,
                policy,
                repairCapability),
            _ => PlanEnsure(observedState, policy, repairCapability),
        };
    }

    private static SafeMigrationDecision PlanEnsure(
        SafeMigrationObservedState observedState,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability
    ) => observedState switch
    {
        SafeMigrationObservedState.Missing => Decision(SafeMigrationAction.Apply, "missing_apply"),
        SafeMigrationObservedState.Matching => Decision(SafeMigrationAction.NoOp, "matching_noop"),
        SafeMigrationObservedState.Different => policy switch
        {
            SafeMigrationPolicy.ExistenceOnly => Decision(SafeMigrationAction.NoOp, "existing_existence_noop"),
            SafeMigrationPolicy.ThrowIfDifferent => Decision(SafeMigrationAction.RejectDifferent, "different_reject"),
            SafeMigrationPolicy.RepairIfSafe when repairCapability == SafeMigrationRepairCapability.Safe => Decision(
                SafeMigrationAction.Repair,
                "different_repair"),
            SafeMigrationPolicy.RepairIfSafe => Decision(
                SafeMigrationAction.RejectDifferent,
                "different_no_safe_repair"),
            _ => throw new UnreachableException(),
        },
        _ => throw new UnreachableException(),
    };

    private static SafeMigrationDecision PlanDrop(
        SafeMigrationObservedState observedState
    ) => observedState switch
    {
        SafeMigrationObservedState.Missing => Decision(SafeMigrationAction.NoOp, "missing_noop"),
        SafeMigrationObservedState.Matching => Decision(SafeMigrationAction.Apply, "existing_drop"),
        SafeMigrationObservedState.Different => Decision(SafeMigrationAction.RejectDifferent, "wrong_object_kind"),
        _ => throw new UnreachableException(),
    };

    private static SafeMigrationDecision PlanRename(
        SafeMigrationObservedState observedState
    ) => observedState switch
    {
        SafeMigrationObservedState.Missing => Decision(SafeMigrationAction.NoOp, "source_missing_noop"),
        SafeMigrationObservedState.Matching => Decision(SafeMigrationAction.Apply, "source_exists_rename"),
        SafeMigrationObservedState.Different => Decision(SafeMigrationAction.RejectDifferent, "rename_target_conflict"),
        _ => throw new UnreachableException(),
    };

    private static SafeMigrationDecision PlanAlter(
        SafeMigrationObservedState observedState,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability
    ) => observedState switch
    {
        SafeMigrationObservedState.Missing => Decision(SafeMigrationAction.RejectDifferent, "alter_target_missing"),
        SafeMigrationObservedState.Matching => Decision(SafeMigrationAction.NoOp, "matching_noop"),
        SafeMigrationObservedState.Different when policy == SafeMigrationPolicy.RepairIfSafe
            && repairCapability == SafeMigrationRepairCapability.Safe => Decision(
                SafeMigrationAction.Repair,
                "different_repair"),
        SafeMigrationObservedState.Different => Decision(SafeMigrationAction.RejectDifferent, "alter_not_approved"),
        _ => throw new UnreachableException(),
    };

    private static bool IsDrop(
        SafeMigrationOperationKind kind
    ) => kind is SafeMigrationOperationKind.DropSchema
        or SafeMigrationOperationKind.DropTable
        or SafeMigrationOperationKind.DropColumn
        or SafeMigrationOperationKind.DropIndex
        or SafeMigrationOperationKind.DropPrimaryKey
        or SafeMigrationOperationKind.DropUniqueConstraint
        or SafeMigrationOperationKind.DropCheckConstraint
        or SafeMigrationOperationKind.DropForeignKey;

    private static bool IsRename(
        SafeMigrationOperationKind kind
    ) => kind is SafeMigrationOperationKind.RenameTable
        or SafeMigrationOperationKind.RenameColumn
        or SafeMigrationOperationKind.RenameIndex;

    private static SafeMigrationDecision Decision(
        SafeMigrationAction action,
        string code
    ) => new(action, code);

    private static void Validate(
        SafeMigrationOperationKind operationKind,
        SafeMigrationObservedState observedState,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability
    )
    {
        if (!Enum.IsDefined(operationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(operationKind));
        }

        if (!Enum.IsDefined(observedState))
        {
            throw new ArgumentOutOfRangeException(nameof(observedState));
        }

        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (!Enum.IsDefined(repairCapability))
        {
            throw new ArgumentOutOfRangeException(nameof(repairCapability));
        }
    }
}
