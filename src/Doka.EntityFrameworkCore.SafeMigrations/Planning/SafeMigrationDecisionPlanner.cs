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
        var decision = PlanValue(operationKind, observedState, policy, repairCapability);

        return new SafeMigrationDecision(decision.Action, decision.Code);
    }

    /// <summary>Selects the canonical action without allocating an unused public decision result.</summary>
    /// <param name="operationKind">The SafeMigrations operation family.</param>
    /// <param name="observedState">The provider-classified live state.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <param name="repairCapability">The provider-proven repair capability.</param>
    /// <returns>The same action and validation contract as <see cref="Plan"/>.</returns>
    internal static SafeMigrationAction PlanAction(
        SafeMigrationOperationKind operationKind,
        SafeMigrationObservedState observedState,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability = SafeMigrationRepairCapability.None
    ) => PlanValue(operationKind, observedState, policy, repairCapability).Action;

    // WHY: SQL renderers consume only the action. Both entry points share one
    // value table, so avoiding their short-lived result objects cannot diverge
    // from public decision codes or retain any operation or live evidence.
    private static (SafeMigrationAction Action, string Code) PlanValue(
        SafeMigrationOperationKind operationKind,
        SafeMigrationObservedState observedState,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability
    )
    {
        var operationFamily = ClassifyOperation(operationKind);

        Validate(operationKind, observedState, policy, repairCapability);

        return observedState switch
        {
            SafeMigrationObservedState.Missing => PlanMissing(operationFamily),
            SafeMigrationObservedState.Matching => PlanMatching(operationFamily),
            SafeMigrationObservedState.Different => PlanDifferent(operationFamily, policy, repairCapability),
            SafeMigrationObservedState.Unsupported => Decision(SafeMigrationAction.RejectUnsupported, "unsupported"),
            SafeMigrationObservedState.DataBlocked => Decision(SafeMigrationAction.RejectDataBlocked, "data_blocked"),
            SafeMigrationObservedState.PrerequisiteMissing => Decision(
                SafeMigrationAction.RejectPrerequisiteMissing,
                "prerequisite_missing"),
            SafeMigrationObservedState.TransitionReady => PlanTransitionReady(operationFamily),
            _ => throw new UnreachableException(),
        };
    }

    private static (SafeMigrationAction Action, string Code) PlanMissing(
        OperationFamily operationFamily
    ) => operationFamily switch
    {
        OperationFamily.Ensure => Decision(SafeMigrationAction.Apply, "missing_apply"),
        OperationFamily.Drop => Decision(SafeMigrationAction.NoOp, "missing_noop"),
        OperationFamily.Rename => Decision(SafeMigrationAction.NoOp, "source_missing_noop"),
        OperationFamily.Alter => Decision(SafeMigrationAction.RejectDifferent, "alter_target_missing"),
        OperationFamily.ModelManagedEnsure => Decision(SafeMigrationAction.Apply, "missing_apply"),
        OperationFamily.ModelManagedUpdate => Decision(
            SafeMigrationAction.RejectPrerequisiteMissing,
            "missing_model_managed_row"),
        OperationFamily.ModelManagedDelete => Decision(SafeMigrationAction.NoOp, "missing_noop"),
        _ => throw new UnreachableException(),
    };

    private static (SafeMigrationAction Action, string Code) PlanMatching(
        OperationFamily operationFamily
    ) => operationFamily switch
    {
        OperationFamily.Ensure => Decision(SafeMigrationAction.NoOp, "matching_noop"),
        OperationFamily.Drop => Decision(SafeMigrationAction.Apply, "existing_drop"),
        OperationFamily.Rename => Decision(SafeMigrationAction.Apply, "source_exists_rename"),
        OperationFamily.Alter => Decision(SafeMigrationAction.NoOp, "matching_noop"),
        OperationFamily.ModelManagedEnsure
            or OperationFamily.ModelManagedUpdate
            or OperationFamily.ModelManagedDelete => Decision(SafeMigrationAction.NoOp, "matching_noop"),
        _ => throw new UnreachableException(),
    };

    private static (SafeMigrationAction Action, string Code) PlanDifferent(
        OperationFamily operationFamily,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability
    ) => operationFamily switch
    {
        OperationFamily.Ensure => PlanDifferentEnsure(policy, repairCapability),
        OperationFamily.Drop => Decision(SafeMigrationAction.RejectDifferent, "wrong_object_kind"),
        OperationFamily.Rename => Decision(SafeMigrationAction.RejectDifferent, "rename_target_conflict"),
        OperationFamily.Alter when policy == SafeMigrationPolicy.RepairIfSafe
            && repairCapability == SafeMigrationRepairCapability.Safe => Decision(
                SafeMigrationAction.Repair,
                "different_repair"),
        OperationFamily.Alter => Decision(SafeMigrationAction.RejectDifferent, "alter_not_approved"),
        OperationFamily.ModelManagedEnsure
            or OperationFamily.ModelManagedUpdate
            or OperationFamily.ModelManagedDelete => Decision(
                SafeMigrationAction.RejectDifferent,
                "different_reject"),
        _ => throw new UnreachableException(),
    };

    private static (SafeMigrationAction Action, string Code) PlanDifferentEnsure(
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability
    ) => policy switch
    {
        SafeMigrationPolicy.ExistenceOnly => Decision(SafeMigrationAction.NoOp, "existing_existence_noop"),
        SafeMigrationPolicy.ThrowIfDifferent => Decision(SafeMigrationAction.RejectDifferent, "different_reject"),
        SafeMigrationPolicy.RepairIfSafe when repairCapability == SafeMigrationRepairCapability.Safe => Decision(
            SafeMigrationAction.Repair,
            "different_repair"),
        SafeMigrationPolicy.RepairIfSafe => Decision(SafeMigrationAction.RejectDifferent, "different_no_safe_repair"),
        _ => throw new UnreachableException(),
    };

    private static (SafeMigrationAction Action, string Code) PlanTransitionReady(
        OperationFamily operationFamily
    ) => operationFamily is OperationFamily.ModelManagedUpdate or OperationFamily.ModelManagedDelete
        ? Decision(SafeMigrationAction.Apply, "transition_ready_apply")
        : Decision(SafeMigrationAction.RejectUnsupported, "transition_state_invalid");

    private static OperationFamily ClassifyOperation(
        SafeMigrationOperationKind operationKind
    ) => operationKind switch
    {
        SafeMigrationOperationKind.EnsureSchema
            or SafeMigrationOperationKind.EnsureTable
            or SafeMigrationOperationKind.EnsureColumn
            or SafeMigrationOperationKind.EnsureIndex
            or SafeMigrationOperationKind.EnsurePrimaryKey
            or SafeMigrationOperationKind.EnsureUniqueConstraint
            or SafeMigrationOperationKind.EnsureCheckConstraint
            or SafeMigrationOperationKind.EnsureForeignKey => OperationFamily.Ensure,
        SafeMigrationOperationKind.EnsureModelManagedData => OperationFamily.ModelManagedEnsure,
        SafeMigrationOperationKind.UpdateModelManagedData => OperationFamily.ModelManagedUpdate,
        SafeMigrationOperationKind.DeleteModelManagedData => OperationFamily.ModelManagedDelete,
        SafeMigrationOperationKind.DropSchema
            or SafeMigrationOperationKind.DropTable
            or SafeMigrationOperationKind.DropColumn
            or SafeMigrationOperationKind.DropIndex
            or SafeMigrationOperationKind.DropPrimaryKey
            or SafeMigrationOperationKind.DropUniqueConstraint
            or SafeMigrationOperationKind.DropCheckConstraint
            or SafeMigrationOperationKind.DropForeignKey => OperationFamily.Drop,
        SafeMigrationOperationKind.RenameTable
            or SafeMigrationOperationKind.RenameColumn
            or SafeMigrationOperationKind.RenameIndex => OperationFamily.Rename,
        SafeMigrationOperationKind.AlterColumn => OperationFamily.Alter,
        _ => throw new ArgumentOutOfRangeException(nameof(operationKind)),
    };

    private static (SafeMigrationAction Action, string Code) Decision(
        SafeMigrationAction action,
        string code
    ) => (action, code);

    private static void Validate(
        SafeMigrationOperationKind operationKind,
        SafeMigrationObservedState observedState,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability
    )
    {
        // WHY: Enum.IsDefined can rebuild collectible runtime metadata after GC,
        // allocating even after warmup. Named members preserve the exact contract
        // without depending on cache lifetime or contiguous enum values.
        if (observedState is not (SafeMigrationObservedState.Missing
            or SafeMigrationObservedState.Matching
            or SafeMigrationObservedState.Different
            or SafeMigrationObservedState.Unsupported
            or SafeMigrationObservedState.DataBlocked
            or SafeMigrationObservedState.PrerequisiteMissing
            or SafeMigrationObservedState.TransitionReady))
        {
            throw new ArgumentOutOfRangeException(nameof(observedState));
        }

        if (policy is not (SafeMigrationPolicy.ExistenceOnly
            or SafeMigrationPolicy.ThrowIfDifferent
            or SafeMigrationPolicy.RepairIfSafe))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (repairCapability is not (SafeMigrationRepairCapability.None or SafeMigrationRepairCapability.Safe))
        {
            throw new ArgumentOutOfRangeException(nameof(repairCapability));
        }

    }

    private enum OperationFamily
    {
        Ensure,
        Drop,
        Rename,
        Alter,
        ModelManagedEnsure,
        ModelManagedUpdate,
        ModelManagedDelete,
    }
}
