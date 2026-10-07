namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private readonly HashSet<SafeMigrationOperation> _ddlRowDependentOperations = [];
    private SqlServerDdlRowEffectRisk _ddlRowEffectRisk;
    private bool _ddlRowFreshnessInvalidated;
    private int? _ddlRowOriginOrdinal;
    private int? _currentProjectedOperationOrdinal;

    /// <inheritdoc />
    public void SetCurrentOperationOrdinal(int operationOrdinal)
        => _currentProjectedOperationOrdinal = operationOrdinal;

    /// <summary>Captures invocation-local DDL-trigger risk, including incomplete server metadata visibility.</summary>
    /// <param name="risk">The observed trigger or unproved metadata visibility that prevents an absence certificate.</param>
    internal void CaptureProjectedDdlRowEffects(
        SqlServerDdlRowEffectRisk risk
    ) => _ddlRowEffectRisk = risk;

    /// <summary>Retains compact row-proof ownership from an already built classifier.</summary>
    /// <param name="operation">The immutable source operation.</param>
    /// <param name="plan">The existing guarded classifier; it is not rebuilt or retained.</param>
    internal void CaptureProjectedDdlRowDependency(
        SafeMigrationOperation operation,
        SqlServerSafeMigrationRuntimePlan plan
    )
    {
        if (plan.RequiresLiveDataProof || operation.Intent is ModelManagedDataIntent
            or EnsurePrimaryKeyIntent or EnsureUniqueConstraintIntent or EnsureForeignKeyIntent
            || operation.Intent is EnsureIndexIntent { Definition.Unique: true })
        {
            _ddlRowDependentOperations.Add(operation);
        }
    }

    private void ObserveProjectedDdlRowEffects(
        SafeMigrationOperation operation,
        SafeMigrationDecision decision
    )
    {
        if (_ddlRowEffectRisk != SqlServerDdlRowEffectRisk.None
            && decision.ShouldExecute
            && operation.Intent is not ModelManagedDataIntent)
        {
            // WHY: Database and server DDL triggers, including extended-property
            // events, can mutate unrelated rows. An accepted named drop is not
            // evidence that captured NULL, duplicate, orphan, or CHECK truth
            // survived. NoOp emits no structural or stamping DDL.
            _ddlRowFreshnessInvalidated = true;
            _ddlRowOriginOrdinal = _currentProjectedOperationOrdinal;
            _transitionDataChangedGlobally = true;
        }
    }

    private void ObserveProviderDdlRowEffects(
        MigrationOperation operation
    )
    {
        // WHY: EF compares raw schemas for typed renames. An unspecified
        // target schema may emit TRANSFER with a versioned model, so only
        // exact raw equality proves that neither rename nor transfer occurs.
        if (operation is RenameTableOperation table
                && (table.NewName is null || table.NewName == table.Name) && table.NewSchema == table.Schema
            || operation is RenameSequenceOperation sequence
                && (sequence.NewName is null || sequence.NewName == sequence.Name)
                && sequence.NewSchema == sequence.Schema)
        {
            return;
        }

        if (_ddlRowEffectRisk != SqlServerDdlRowEffectRisk.None
            && operation is CreateTableOperation or AlterTableOperation or DropTableOperation
            or RenameTableOperation or AddColumnOperation or AlterColumnOperation or DropColumnOperation
            or RenameColumnOperation or CreateIndexOperation or DropIndexOperation or RenameIndexOperation
            or AddPrimaryKeyOperation or DropPrimaryKeyOperation or AddUniqueConstraintOperation
            or DropUniqueConstraintOperation or AddCheckConstraintOperation or DropCheckConstraintOperation
            or AddForeignKeyOperation or DropForeignKeyOperation or EnsureSchemaOperation or DropSchemaOperation
            or AlterDatabaseOperation or CreateSequenceOperation or AlterSequenceOperation or RestartSequenceOperation
            or RenameSequenceOperation or DropSequenceOperation)
        {
            // WHY: Core's typed CREATE postcondition normally proves emptiness.
            // A CREATE_TABLE trigger can insert rows before a later safe CHECK.
            // Preserve Core's opaque-SQL deferred origin by qualifying only
            // known executable provider DDL, never every provider operation.
            _ddlRowFreshnessInvalidated = true;
            _ddlRowOriginOrdinal = _currentProjectedOperationOrdinal;
            _transitionDataChangedGlobally = true;
        }
    }

    private SafeMigrationProviderAnalysis QualifyProjectedDdlRowFreshness(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
    {
        if (!_ddlRowFreshnessInvalidated || analysis.IsInvariantUnsupported
            || analysis.ObservedState == SafeMigrationObservedState.Unsupported
            || analysis.IsOpaqueProjectionUnknown && analysis.Code != "projected_data_state_unknown"
            || operation.Intent is not ModelManagedDataIntent
                && (analysis.ObservedState == SafeMigrationObservedState.Matching
                    || !_ddlRowDependentOperations.Contains(operation)))
        {
            return analysis;
        }

        if (analysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                && analysis.Code != "projected_data_state_unknown"
            || analysis.ObservedState == SafeMigrationObservedState.Different
                && operation.Intent is not ModelManagedDataIntent
                && (analysis.RepairCapability != SafeMigrationRepairCapability.Safe
                    || operation.Policy != SafeMigrationPolicy.RepairIfSafe))
        {
            // WHY: DDL-trigger row effects cannot repair missing structural prerequisites
            // or an unapproved metadata difference. Those independent blockers remain real.
            return analysis;
        }

        // WHY: Read-only preflight cannot execute preceding DDL or infer its trigger
        // effects. Attribute the uncertainty to that DDL, not to invented SQL. The
        // runtime guard still validates actual rows immediately before this operation.
        return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false,
            _ddlRowEffectRisk == SqlServerDdlRowEffectRisk.EnabledTrigger
                ? "projected_ddl_trigger_data_unknown"
                : "projected_ddl_visibility_data_unknown")
        {
            RequiresLiveDataProof = true,
            ProviderDeferredOriginOrdinal = _ddlRowOriginOrdinal,
            IsModelManagedProjectionUnknown = operation.Intent is ModelManagedDataIntent,
        };
    }
}

/// <summary>Separates visible enabled DDL triggers from an unavailable absence certificate.</summary>
internal enum SqlServerDdlRowEffectRisk
{
    /// <summary>Complete database and server visibility proved no enabled DDL trigger.</summary>
    None,
    /// <summary>At least one visible enabled DDL trigger can mutate unrelated rows.</summary>
    EnabledTrigger,
    /// <summary>Filtered database or server metadata cannot certify the absence of enabled triggers.</summary>
    VisibilityUnproven,
}
