namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationDiagnosticMessages
{
    /// <summary>Explains an assessment using authored shape and actual classified evidence.</summary>
    /// <param name="intent">The known immutable intent, or null when unavailable.</param>
    /// <param name="state">The observed or projected state, or null when no observation was made.</param>
    /// <param name="analysisCode">The classifier's stable code, not SQL or native error text.</param>
    /// <param name="decisionCode">The planner's stable code.</param>
    /// <returns>A constant, payload-free explanation requiring no formatting or I/O.</returns>
    public static SafeMigrationDiagnosticExplanation GetExplanation(
        SafeMigrationIntent? intent,
        SafeMigrationObservedState? state,
        string analysisCode,
        string decisionCode
    )
    {
        ArgumentNullException.ThrowIfNull(analysisCode);
        ArgumentNullException.ThrowIfNull(decisionCode);

        var explanation = GetExplanation(intent?.Kind, state, analysisCode, decisionCode);
        if (state == SafeMigrationObservedState.DataBlocked
            && explanation.ReasonCode == "data_precondition_failed"
            && intent is EnsureColumnIntent ensure
            && !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(ensure.Definition))
        {
            // WHY: Shape proves the lack of a declared replacement, not that the current
            // column is absent. Recovery staging and existing-column repair can also block.
            return RequiredColumnReplacementExplanation();
        }

        return explanation;
    }

    /// <summary>Explains a state without inventing owner metadata or retaining an operation payload.</summary>
    /// <param name="kind">The known safe operation kind, or null for a provider-owned operation.</param>
    /// <param name="state">The classified state, or null when unavailable or deferred.</param>
    /// <param name="analysisCode">The stable classified analysis code.</param>
    /// <param name="decisionCode">The stable planned decision code.</param>
    /// <returns>The established reason or an explicitly unproven fallback.</returns>
    public static SafeMigrationDiagnosticExplanation GetExplanation(
        SafeMigrationOperationKind? kind,
        SafeMigrationObservedState? state,
        string analysisCode,
        string decisionCode
    )
    {
        ArgumentNullException.ThrowIfNull(analysisCode);
        ArgumentNullException.ThrowIfNull(decisionCode);

        if (decisionCode is "runtime_validation_required" or "validate_at_runtime")
        {
            return Explain("runtime_validation_required",
                "Earlier operations invalidated captured evidence. The final decision has not yet been made.",
                "Let the fresh runtime guard classify the ordered state; this entry is not a successful execution.");
        }

        if (analysisCode == "postcondition_superseded")
        {
            return Explain("postcondition_superseded",
                "A later accepted operation supplies the final postcondition for this exact resource.", "");
        }

        if (analysisCode == "provider_owned_not_analyzed")
        {
            return Explain("provider_owned_not_analyzed",
                "This ordinary EF or provider operation was not classified as a safe operation.",
                "Verify its native behavior and independent postconditions; no SafeMigrations approval is implied.");
        }

        if (SpecificExplanation(analysisCode) is { } specific)
        {
            return specific;
        }

        var decisionExplanation = decisionCode switch
        {
            "matching_noop" => Explain("matching_noop",
                "The observed definition already matches the requested contract; no change is planned.", ""),
            "missing_noop" or "source_missing_noop" => Explain("missing_noop",
                "The object to remove or rename is absent; no change is planned.", ""),
            "missing_apply" => Explain("missing_apply",
                "The requested object is absent and its classified prerequisites permit creation.",
                "Execute the migration normally; the runtime guard must still validate the current state."),
            "existing_drop" => Explain("existing_drop",
                "The object is present and the classified removal contract permits dropping it.",
                "Execute the reviewed removal and verify its final postcondition."),
            "source_exists_rename" => Explain("source_exists_rename",
                "The source is present and the classified rename contract permits the transition.",
                "Execute the reviewed rename and verify its final identity."),
            "different_repair" => Explain("different_repair",
                "The definition differs and the provider proved a repair authorized by the explicit policy.",
                "Execute the approved transition; fresh runtime and postcondition checks remain required."),
            "existing_existence_noop" => Explain("existing_existence_noop",
                "The object exists. ExistenceOnly deliberately leaves its differing definition unchanged.",
                "Confirm that ignoring definition differences is the intended policy."),
            "transition_ready_apply" => Explain("transition_ready_apply",
                "The captured model-managed source permits attempting the compare-and-swap transition.",
                "Execute with the existing fresh value and dependency guards; no row values are shown."),
            "alter_target_missing" => Explain("alter_target_missing",
                "The column required for this alteration is absent.",
                "Correct the ordered source state before retrying; an alteration does not create its missing source."),
            "rename_target_conflict" => Explain("rename_target_conflict",
                "The rename target conflicts with the classified source or destination contract.",
                "Inspect both endpoints and author a reviewed identity-preserving transition."),
            "wrong_object_kind" => Explain("wrong_object_kind",
                "The existing object is not the kind required by this removal contract.",
                "Identify the actual object and correct the operation; "
                    + "do not drop it merely because its name matches."),
            "missing_model_managed_row" => Explain("missing_model_managed_row",
                "A captured source row required for the managed transition is absent.",
                "Reconcile source-controlled data without inventing or logging missing row values."),
            "alter_not_approved" or "different_no_safe_repair" or "different_reject" => Explain(
                "definition_change_not_authorized",
                "The classified definition differs and this policy has no proven, authorized repair.",
                "Inspect expected and actual schema facets and author a supported, reviewed transition; "
                    + "do not weaken safety checks just to bypass this rejection."),
            _ => (SafeMigrationDiagnosticExplanation?)null,
        };

        if (decisionExplanation is { } explainedDecision)
        {
            return explainedDecision;
        }

        return state switch
        {
            SafeMigrationObservedState.DataBlocked => Explain("data_precondition_failed",
                "Existing data prevents the requested transition. This classification alone does not identify "
                    + "NULL values, excessive length, duplicates or foreign-key orphans.",
                "Inspect the operation's data precondition and supply an explicit, reviewed data transformation."),
            SafeMigrationObservedState.PrerequisiteMissing => Explain("prerequisite_unproven",
                kind == SafeMigrationOperationKind.EnsureForeignKey
                    ? "The foreign-key endpoints, compatible column storage or usable referenced key are not proven."
                    : "A required owner, member or dependency is absent or cannot be proven from current evidence.",
                "Inspect the named owners and ordered prerequisites before retrying; absence is not assumed."),
            SafeMigrationObservedState.Unsupported => Explain("provider_contract_unsupported",
                "The active provider cannot prove support for this operation contract.",
                "Inspect the specific capability code and supported provider contract; no bypass is authorized."),
            SafeMigrationObservedState.Different => Explain("definition_different",
                "The observed or projected definition differs from the requested contract.",
                "Inspect expected and actual facets and the authored policy before choosing a reviewed transition."),
            SafeMigrationObservedState.Matching => Explain("definition_matching",
                "The observed or projected definition matches; the actual planner action determines execution.", ""),
            SafeMigrationObservedState.Missing => Explain("object_missing",
                "The classifier observed or projected absence; the actual planner action determines execution.", ""),
            SafeMigrationObservedState.TransitionReady => Explain("transition_source_ready",
                "A managed source state was captured; fresh compare-and-swap guards still determine execution.", ""),
            _ => default,
        };
    }

    private static SafeMigrationDiagnosticExplanation? SpecificExplanation(string code) => code switch
    {
        "required_column_backfill_missing" or "required_column_has_rows" => Explain(code,
            "A required column cannot be added to existing rows without an explicitly supplied replacement value.",
            "Add nullable, explicitly backfill the intended values, then make the column required; "
                + "or declare an approved replacement default."),
        "column_nullability_data_blocked" => Explain(code,
            "The selected required-column data check found NULL values incompatible with the target.",
            "Backfill the intended non-NULL values explicitly before making the column required."),
        "column_transition_data_blocked" => Explain(code,
            "A selected data check rejected this column transition; "
                + "the generic code does not identify the exact row defect.",
            "Inspect the target type and data precondition, then author a reviewed lossless transformation."),
        "projected_foreign_key_replacement_data_blocked" => Explain(code,
            "The qualified foreign-key replacement probe found orphaned dependent rows.",
            "Reconcile the dependent rows or their referenced principals before the reviewed foreign-key replacement."),
        "column_primary_key_dependency" => Explain(code,
            "The column remains part of a primary key and cannot accept the requested nullable contract.",
            "Remove or replace the exact owned key in the intended order before relaxing nullability."),
        "column_set_null_dependency" or "projected_foreign_key_set_null_unrepresentable" => Explain(code,
            "A SET NULL foreign-key action conflicts with a required dependent-column contract.",
            "Make the reviewed dependent column nullable or explicitly replace the incompatible referential action."),
        "database_qualifier_mismatch" => Explain(code,
            "An authored database qualifier does not match the supported active database identity.",
            "Correct the authored qualifier or intended connection; no database identity is inferred offline."),
        "default_schema_mismatch" or "schema_identity_mismatch" or "table_identity_mismatch"
            or "principal_table_identity_mismatch" or "schema_resolution_mismatch" => Explain(code,
                "The authored or unqualified owner cannot be bound to the required native schema identity.",
                "Use the intended explicit qualifier and verify the actual owner; do not guess a resolved schema."),
        "column_atomic_add_unavailable" or "column_required_sql_add_unavailable" => Explain(code,
            "The active engine, column shape or session contract cannot prove the required-column addition safe.",
            "Inspect the supported atomic-add contract or author an explicit "
                + "nullable-add/backfill/required transition."),
        "column_atomic_add_guard_conflict" => Explain(code,
            "The required-column staging identity is occupied without its exact owned contract.",
            "Inspect the existing artifact and recovery evidence; its name alone does not authorize removal."),
        "column_atomic_add_check_failed" => Explain(code,
            "The exact owned required-column CHECK rejected the attempted atomic addition. "
                + "Existing rows do not satisfy its required-value contract; no concurrent writer is inferred.",
            "Inspect the retained column state and provide explicit intended values before a guarded retry."),
        "column_atomic_add_resume" => Explain(code,
            "An exact owned required-column staging contract was recognized; current rows still determine completion.",
            "Resume only through the existing fresh NULL and environment guards; "
                + "staging is not proof of an empty table."),
        "projected_required_add_rows_unproven" => Explain(code,
            "The current ordered row state does not prove that this required-column "
                + "addition can populate existing rows.",
            "Obtain an operation-bound current row proof or author an explicit reviewed backfill."),
        "projected_structure_state_unknown" or "projected_column_transition_unproven"
            or "projected_primary_key_replacement_unproven" => Explain(code,
                "Earlier ordered changes left the required structural or transition proof incomplete.",
                "Inspect preceding owner, column and key changes; require a supported current proof before retrying."),
        "projected_data_state_unknown" or "projected_model_managed_data_state_unknown" => Explain(code,
            "Earlier writes invalidated the captured row-safety proof; no current data defect is established.",
            "Require a fresh ordered runtime proof or an explicit, supported data transition."),
        "incoming_foreign_key_key_dependency" or "table_drop_foreign_key_dependency" => Explain(code,
            "A surviving incoming foreign key prevents the requested key or table removal.",
            "Inspect and explicitly remove or replace each exact dependent relationship in the reviewed order."),
        "catalog_metadata_not_visible" or "dependency_catalog_permission" => Explain(code,
            "Effective catalog access cannot establish the metadata or dependency proof required by this operation.",
            "Verify the reviewed least-privilege access contract; SafeMigrations does not grant permissions."),
        "column_alter_write_permission" or "column_alter_read_permission" or "column_nullability_data_unproven"
            or "check_row_data_unproven" => Explain(code,
                "The effective access contract cannot establish the required row read or approved rewrite.",
                "Verify the intended row-access permissions before retrying; no data-safety certificate is inferred."),
        "schema_operations" => Explain(code,
            "The provider does not support this schema or database operation contract.",
            "Use a supported reviewed provider operation; do not reinterpret a database as an ordinary table owner."),
        "column_type_mapping" => Explain(code,
            "The provider has no supported storage-type mapping for this column contract.",
            "Choose a supported explicit storage type and verify the intended model mapping."),
        "column_limit" => Explain(code,
            "The selected physical-layout check found no available column slot for the requested addition.",
            "Review the table's physical column capacity and choose a supported schema transition."),
        "column_fixed_row_limit" => Explain(code,
            "The selected physical-layout check found that the requested fixed-row storage exceeds supported capacity.",
            "Review the physical row layout and choose a supported storage shape before retrying."),
        "column_layout_unproven" => Explain(code,
            "The requested column's physical-layout and capacity contract could not be proven.",
            "Inspect the existing layout and the provider's bounded storage contract; no capacity is assumed."),
        "baseline_transaction_suppressed" => Explain(code,
            "A transaction-suppressed baseline cannot be executed under this provider's guarded transaction contract.",
            "Use a supported transactional EF Core or migration-bundle baseline; do not bypass transaction ownership."),
        "physical_table_unproven" or "table_unproven_facet" or "column_unproven_facet"
            or "index_unproven_facet" or "default_expression_unproven" or "identifier_collation_unproven"
            or "column_widening_storage_unproven" or "check_column_type_unproven" => Explain(code,
                "A physical type, facet, identity or capacity requirement has no supported equivalence proof.",
                "Inspect the qualified provider contract and author an explicitly supported target shape."),
        "postcondition_identity_unproven" => Explain(code,
            "Final metadata cannot prove this operation's historical owner across a later identity-changing boundary.",
            "Verify a reviewed explicitly qualified final-state contract; "
                + "do not infer the earlier owner from today's default."),
        "postcondition_failed" or "doka_sm_postcondition" => Explain(
            "postcondition_failed",
            "The required final condition was not satisfied after execution. Earlier DDL may already have applied.",
            "Inspect the partial migration and retained recovery evidence before retrying; "
                + "no full rollback is implied."),
        "script_unsupported" or "doka_sm_script_unsupported" => Explain("script_unsupported",
            "This provider cannot express the guarded runtime contract in a SQL script.",
            "Execute through EF Core or a migration bundle with the registered SafeMigrations provider."),
        _ => null,
    };

    /// <summary>Obtains the same evidence-based runtime vocabulary used by native and managed renderers.</summary>
    /// <param name="intent">The exact immutable intent associated with the owned guard.</param>
    /// <param name="guardCode">The owned guard category, not an arbitrary provider error message.</param>
    /// <param name="diagnosticReasonCode">The selected classifier's stable reason, or null when unavailable.</param>
    /// <returns>The normalized human explanation without formatting or database access.</returns>
    internal static SafeMigrationDiagnosticExplanation GetRuntimeExplanation(
        SafeMigrationIntent intent,
        string guardCode,
        string? diagnosticReasonCode = null
    )
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(guardCode);

        return GetRuntimeExplanationCore(intent.Kind,
            intent is EnsureColumnIntent column
                && !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(column.Definition),
            guardCode, diagnosticReasonCode);
    }

    /// <summary>Explains an owned failure from payload-free captured command facts.</summary>
    /// <param name="context">The exact immutable identity, policy and required-replacement fact.</param>
    /// <param name="guardCode">The owned guard category, not native error text.</param>
    /// <param name="diagnosticReasonCode">The selected classifier's stable code, when known.</param>
    /// <returns>The same normalized explanation as operation-based formatting.</returns>
    internal static SafeMigrationDiagnosticExplanation GetRuntimeExplanation(
        SafeMigrationRuntimeDiagnosticContext context,
        string guardCode,
        string? diagnosticReasonCode = null
    )
    {
        ArgumentNullException.ThrowIfNull(guardCode);

        if (context.Identity is null)
        {
            throw new ArgumentException(
                "A runtime diagnostic context must contain a captured identity.", nameof(context));
        }

        return GetRuntimeExplanationCore(context.Identity.OperationKind, context.RequiredColumnReplacementUnproven,
            guardCode, diagnosticReasonCode);
    }

    private static SafeMigrationDiagnosticExplanation GetRuntimeExplanationCore(
        SafeMigrationOperationKind? kind,
        bool requiredReplacementUnproven,
        string guardCode,
        string? diagnosticReasonCode
    )
    {
        guardCode = NormalizeGuardCode(guardCode);

        if (guardCode == "doka_sm_postcondition")
        {
            // WHY: A later classifier detail must not erase the execution-phase warning.
            // A failed postcondition never proves that preceding native DDL rolled back.
            return SpecificExplanation(guardCode)!.Value;
        }

        if (guardCode is "doka_sm_identity_scope_in_use" or "doka_sm_identity_acquisition_unproven")
        {
            return Explain(guardCode == "doka_sm_identity_scope_in_use"
                    ? "identity_scope_in_use" : "identity_acquisition_unproven",
                "The migration session cannot establish exclusive ownership of the required identity-insert resource.",
                "Preserve caller-owned session state and inspect recovery status; "
                    + "retry with a clean owned migration session.");
        }

        var state = guardCode switch
        {
            "doka_sm_data_blocked" => SafeMigrationObservedState.DataBlocked,
            "doka_sm_prerequisite_missing" => SafeMigrationObservedState.PrerequisiteMissing,
            "doka_sm_different" => SafeMigrationObservedState.Different,
            "doka_sm_unsupported" or "doka_sm_script_unsupported" => SafeMigrationObservedState.Unsupported,
            "doka_sm_missing" => SafeMigrationObservedState.Missing,
            _ => (SafeMigrationObservedState?)null,
        };

        var decisionCode = guardCode == "doka_sm_missing" ? "alter_target_missing" : guardCode;

        var explanation = GetExplanation(kind, state, diagnosticReasonCode ?? guardCode, decisionCode);
        if (state == SafeMigrationObservedState.DataBlocked
            && explanation.ReasonCode == "data_precondition_failed"
            && requiredReplacementUnproven)
        {
            return RequiredColumnReplacementExplanation();
        }

        return explanation;
    }

    private static string NormalizeGuardCode(string code) => code switch
    {
        // WHY: Accept the former human-readable spelling as input, but emit the
        // existing native category. This does not broaden exception ownership.
        "doka_sm_postcondition_failed" => "doka_sm_postcondition",
        "doka_sm_data_blocked" or "doka_sm_prerequisite_missing" or "doka_sm_different"
            or "doka_sm_unsupported" or "doka_sm_postcondition"
            or "doka_sm_missing" or "doka_sm_script_unsupported" or "doka_sm_identity_scope_in_use"
            or "doka_sm_identity_acquisition_unproven" => code,
        _ => "doka_sm_unknown",
    };

    private static SafeMigrationDiagnosticExplanation Explain(string code, string reason, string nextStep)
        => new(code, reason, nextStep);

    private static SafeMigrationDiagnosticExplanation RequiredColumnReplacementExplanation() => new(
        "required_column_replacement_unproven",
        "Existing data blocks this required-column contract, which declares no replacement for existing rows. "
            + "The generic classification does not establish whether the column is missing or already staged.",
        "Specify the intended value in a reviewed migration, or add nullable, explicitly backfill, "
            + "then make the column required.");
}
