namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private readonly HashSet<SafeMigrationOperation> _ddlRowDependentOperations = [];
    private SqlServerDdlRowEffectRisk _ddlRowEffectRisk;
    private bool _ddlRowFreshnessInvalidated;
    private bool _hasEnabledDmlTriggers;
    private bool _freshnessInvalidatedByDml;
    private int? _ddlRowOriginOrdinal;
    private int? _currentProjectedOperationOrdinal;

    /// <inheritdoc />
    public void SetCurrentOperationOrdinal(int operationOrdinal)
        => _currentProjectedOperationOrdinal = operationOrdinal;

    /// <summary>Captures invocation-local DDL-trigger risk for both rows and physical metadata.</summary>
    /// <param name="risk">The observed trigger or unproved metadata visibility that prevents an absence certificate.</param>
    internal void CaptureProjectedDdlRowEffects(
        SqlServerDdlRowEffectRisk risk
    ) => _ddlRowEffectRisk = risk;

    /// <summary>Captures globally enabled DML triggers from the metadata-visible database snapshot.</summary>
    /// <param name="hasEnabledTriggers">Whether any SQL or CLR table/view trigger can execute.</param>
    internal void CaptureProjectedDmlEffects(bool hasEnabledTriggers) => _hasEnabledDmlTriggers = hasEnabledTriggers;

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
        if (_hasEnabledDmlTriggers && decision.ShouldExecute && operation.Intent is ModelManagedDataIntent)
        {
            InvalidateProjectedTriggerFreshness(fromDml: true);

            return;
        }

        if (_ddlRowEffectRisk != SqlServerDdlRowEffectRisk.None
            && decision.ShouldExecute
            && operation.Intent is not ModelManagedDataIntent)
        {
            // WHY: Database and server DDL triggers, including extended-property
            // events, can mutate unrelated rows and create or replace physical
            // dependencies. The named drop's own postcondition certifies only
            // that name's absence, not global row or schema freshness. NoOp
            // emits no structural or stamping DDL.
            InvalidateProjectedTriggerFreshness(fromDml: false);
        }
    }

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis? ValidateCapturedProjection(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        // WHY: Neutral projection has matching and removed-owner shortcuts.
        // Trigger-created objects are absent from that immutable snapshot, so
        // freshness must be checked before any such shortcut can certify it.
        return _ddlRowFreshnessInvalidated
            ? QualifyProjectedDdlFreshness(operation, liveAnalysis) : null;
    }

    private void ObserveProviderDdlRowEffects(
        MigrationOperation operation
    )
    {
        if (_hasEnabledDmlTriggers && (operation is InsertDataOperation { Values.Length: > 0 }
            or UpdateDataOperation { KeyValues.Length: > 0 } or DeleteDataOperation { KeyValues.Length: > 0 }
            || operation is AlterColumnOperation { OldColumn.IsNullable: true, IsNullable: false } alteration
                && (alteration.DefaultValue is not null || alteration.DefaultValueSql is not null)))
        {
            // WHY: Dynamic SQL in a DML trigger can mutate unrelated metadata.
            // A global presence witness also covers child triggers invoked by
            // FK cascades, which an owner-only lookup would miss. Empty EF data
            // arrays emit no DML commands and cannot execute a trigger. EF's
            // nullable-to-NOT NULL/default ALTER emits a backfill UPDATE even
            // if no row needs changing, which still invokes UPDATE triggers.
            // Source: Microsoft CREATE TRIGGER, introduction and "Optimize DML triggers":
            // https://learn.microsoft.com/en-us/sql/t-sql/statements/create-trigger-transact-sql?view=sql-server-ver17
            InvalidateProjectedTriggerFreshness(fromDml: true);

            return;
        }

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
            InvalidateProjectedTriggerFreshness(fromDml: false);
        }
    }

    private void InvalidateProjectedTriggerFreshness(bool fromDml)
    {
        _ddlRowFreshnessInvalidated = true;
        _freshnessInvalidatedByDml = fromDml;
        _ddlRowOriginOrdinal = _currentProjectedOperationOrdinal;
        _transitionDataChangedGlobally = true;
    }

    private SafeMigrationProviderAnalysis QualifyProjectedDdlFreshness(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
    {
        if (!_ddlRowFreshnessInvalidated || analysis.IsInvariantUnsupported
            || analysis.ObservedState == SafeMigrationObservedState.Unsupported
            || analysis.IsOpaqueProjectionUnknown && analysis.Code != "projected_data_state_unknown")
        {
            return analysis;
        }

        var rowDependent = operation.Intent is ModelManagedDataIntent
            || analysis.ObservedState != SafeMigrationObservedState.Matching
                && _ddlRowDependentOperations.Contains(operation)
                && (operation.Intent is not AlterColumnIntent { OldDefinition: not null } column
                    || !_catalogSqlBuilder.IsSupportedIntegerWidening(column.OldDefinition, column.Definition));

        // WHY: Read-only preflight cannot execute preceding DDL or infer its trigger
        // effects. Missing, matching, different and dependency truth can all
        // change; only invariant authoring/permission rejections remain final.
        // Attribute uncertainty to that DDL, never invented SQL. Runtime still
        // validates the actual physical and row contract before this operation.
        return new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationRepairCapability.None, false,
            _freshnessInvalidatedByDml
                ? rowDependent ? "projected_dml_trigger_data_unknown" : "projected_dml_trigger_structure_unknown"
                : _ddlRowEffectRisk == SqlServerDdlRowEffectRisk.EnabledTrigger
                ? rowDependent ? "projected_ddl_trigger_data_unknown" : "projected_ddl_trigger_structure_unknown"
                : rowDependent ? "projected_ddl_visibility_data_unknown" : "projected_ddl_visibility_structure_unknown")
        {
            RequiresLiveDataProof = rowDependent,
            ProviderDeferredOriginOrdinal = _ddlRowOriginOrdinal,
            IsModelManagedProjectionUnknown = operation.Intent is ModelManagedDataIntent,
        };
    }
}

/// <summary>Separates visible enabled DDL triggers from an unavailable row-and-structure absence certificate.</summary>
internal enum SqlServerDdlRowEffectRisk
{
    /// <summary>Complete database and server visibility proved no enabled DDL trigger.</summary>
    None,
    /// <summary>At least one visible enabled DDL trigger can mutate unrelated rows and physical metadata.</summary>
    EnabledTrigger,
    /// <summary>Filtered database or server metadata cannot certify the absence of enabled triggers.</summary>
    VisibilityUnproven,
}
