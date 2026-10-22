namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler
{
    /// <summary>Classifies state and repair eligibility using the same operation-local row proofs.</summary>
    private static MySqlSafeMigrationSetupFragment BuildStateEvaluationAssignment(
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues,
        string? matchingPrecondition
    )
    {
        var stateExpression = runtimePlan.DataProbe is null
            ? runtimePlan.RenderPreparedStateExpression(renderedParameterValues)
            : runtimePlan.RenderPreparedStateExpression(
                renderedParameterValues,
                "@doka_sm_data_blocked",
                "@doka_sm_transition_eligible");

        var repairPrecondition = runtimePlan.DataProbe is null
            ? runtimePlan.RenderPreparedRepairPrecondition(renderedParameterValues)
            : runtimePlan.RenderPreparedRepairPrecondition(
                renderedParameterValues,
                "@doka_sm_data_blocked",
                "@doka_sm_transition_eligible");

        var canRepair = runtimePlan.RepairCapability == SafeMigrationRepairCapability.Safe;
        var statement = canRepair
            ? "SELECT (" + stateExpression + ") INTO @doka_sm_state"
            : "SELECT ("
            + stateExpression
            + "), COALESCE(("
            + repairPrecondition
            + "), FALSE) INTO @doka_sm_state, @doka_sm_repair_ok";

        // WHY: Only Different consumes repair eligibility. A separate SET uses the completed
        // classification without undefined same-statement variable ordering. Runtime repair
        // predicates contain catalog reads and already materialized proofs, never target-row SQL.
        return BuildHexAssignment(
            "SET @doka_sm_sql = CASE WHEN @doka_sm_state IS NULL THEN CONVERT(0x",
            statement,
            " USING utf8mb4) ELSE 'DO 0' END;" + EvaluatePreparedStatementSql,
            repairPrecondition: canRepair ? repairPrecondition : null,
            matchingPrecondition: matchingPrecondition);
    }

    /// <summary>Classifies an exact column match before considering a data-dependent transition.</summary>
    private static MySqlSafeMigrationSetupFragment BuildTransitionEligibilityAssignment(
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues,
        string? matchingPrecondition
    )
    {
        const string prefix = "SET @doka_sm_transition_eligible = CASE WHEN @doka_sm_state IS NULL THEN COALESCE((";
        const string suffix = "), FALSE) ELSE FALSE END;";
        var transition = runtimePlan.RenderPreparedTransitionInvariantExpression(renderedParameterValues);

        // WHY: Keep constituent strings until grouping so classification and transition are
        // copied directly into the final transport string, not an intermediate assignment.
        return MySqlSafeMigrationSetupFragment.Compose(
            matchingPrecondition is null ? string.Empty : MatchingStatePrefix,
            matchingPrecondition ?? string.Empty,
            matchingPrecondition is null ? string.Empty : MatchingStateSuffix,
            prefix,
            transition,
            suffix);
    }

    private static MySqlSafeMigrationSetupFragment BuildDataProbeRequiredAssignment(
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues
    ) => MySqlSafeMigrationSetupFragment.Compose(
        "SET @doka_sm_data_probe_required = CASE WHEN @doka_sm_transition_eligible THEN COALESCE((",
        runtimePlan.RenderPreparedDataProbeRequiredExpression(renderedParameterValues),
        "), FALSE) ELSE FALSE END, @doka_sm_data_blocked = FALSE;");

    private static MySqlSafeMigrationSetupFragment BuildDataProbeEvaluationAssignment(
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues
    )
    {
        var statement = "SELECT COALESCE(("
            + runtimePlan.RenderPreparedDataProbeBlockedExpression(renderedParameterValues)
            + "), FALSE) INTO @doka_sm_data_blocked";

        // WHY: PREPARE resolves target identifiers before execution. Materialize
        // the bounded row query only after the catalog proved this is a varchar
        // narrowing transition on an existing column.
        return BuildHexAssignment(
            "SET @doka_sm_sql = CASE WHEN @doka_sm_data_probe_required THEN CONVERT(0x",
            statement,
            " USING utf8mb4) ELSE 'DO 0' END;" + EvaluatePreparedStatementSql);
    }

    /// <summary>Materializes the shared NULL proof only for a physically eligible nullable column.</summary>
    private static MySqlSafeMigrationSetupFragment BuildNullabilityDataProbeEvaluationAssignment(
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues,
        string? matchingPrecondition
    )
    {
        var statement = "SELECT COALESCE(("
            + runtimePlan.RenderPreparedNullabilityDataProbeBlockedExpression(renderedParameterValues)
            + "), FALSE) INTO @doka_sm_nullability_blocked";

        const string prefix = "SET @doka_sm_column_repair_eligible = CASE WHEN @doka_sm_state IS NULL "
            + "THEN COALESCE((";

        // WHY: Separate materialization avoids undefined user-variable evaluation
        // order within SELECT. Nested CASE keeps identifier-dependent SQL behind
        // the existing database, table, column and physical-eligibility guards.
        return BuildHexAssignment(
            prefix,
            statement,
            " USING utf8mb4) ELSE 'DO 0' END ELSE 'DO 0' END;" + EvaluatePreparedStatementSql,
            (runtimePlan.RenderPreparedNullabilityColumnExpression(renderedParameterValues),
                runtimePlan.RenderPreparedNullabilityRepairInvariantExpression(renderedParameterValues)),
            matchingPrecondition: matchingPrecondition);
    }

    private static MySqlSafeMigrationSetupFragment BuildGuardEvaluationAssignment(
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues
    )
    {
        var statement = "SELECT CASE WHEN NOT COALESCE(("
            + runtimePlan.RenderPreparedStateEvaluationGuardExpression(renderedParameterValues)
            + "), FALSE) THEN ("
            + runtimePlan.RenderPreparedStateEvaluationGuardFailureExpression(renderedParameterValues)
            + ") ELSE NULL END INTO @doka_sm_state";

        // PREPARE performs identifier resolution. Materialize the data-reading
        // guard only after the catalog-only prerequisite has succeeded.
        return BuildHexAssignment(
            "SET @doka_sm_sql = CASE WHEN @doka_sm_state IS NULL THEN CONVERT(0x",
            statement,
            " USING utf8mb4) ELSE 'DO 0' END;" + EvaluatePreparedStatementSql);
    }

    private static string BuildInitialLazyStateAssignment(
        bool preserveExistingState
    ) => preserveExistingState
        ? "SET @doka_sm_state = CASE WHEN @doka_sm_state IS NOT NULL THEN @doka_sm_state "
        + "WHEN NOT @doka_sm_prerequisite_ok THEN 'prerequisite_missing' "
        + "ELSE NULL END, @doka_sm_repair_ok = FALSE;"
        : "SET @doka_sm_state = CASE WHEN NOT @doka_sm_prerequisite_ok THEN 'prerequisite_missing' "
        + "ELSE NULL END, @doka_sm_repair_ok = FALSE;";

    /// <summary>Defers body encoding until the final setup group is written.</summary>
    /// <param name="applyDdl">The provider-rendered apply statement.</param>
    /// <param name="repairDdl">The separately guarded safe repair statement, when available.</param>
    /// <param name="prepareInSetup">Whether the immediate PREPARE belongs to this owned setup assignment.</param>
    /// <returns>The exact assignment and optional control suffix, never the guarded body execution.</returns>
    private static MySqlSafeMigrationSetupFragment BuildPreparedSqlAssignment(
        string applyDdl,
        string? repairDdl,
        bool prepareInSetup = false
    )
    {
        var suffix = prepareInSetup ? PreparedSqlAssignmentSuffix + PrepareStatementSql : PreparedSqlAssignmentSuffix;

        // WHY: PREPARE still receives one SQL string. Retaining the source values lets the
        // compactor encode into that final string without an intermediate hexadecimal assignment.
        return MySqlSafeMigrationSetupFragment.Compose(
            PreparedApplySqlPrefix,
            MySqlSafeMigrationSetupFragment.Part.Hex(applyDdl),
            repairDdl is null ? string.Empty : PreparedRepairSqlPrefix,
            MySqlSafeMigrationSetupFragment.Part.Hex(repairDdl ?? string.Empty),
            suffix);
    }

    /// <summary>Defers a prepared assignment with optional operation-local NULL-proof eligibility.</summary>
    /// <param name="prefix">The literal assignment prefix.</param>
    /// <param name="statement">The statement encoded as hexadecimal UTF-8.</param>
    /// <param name="suffix">The assignment suffix and immediate prepared controls.</param>
    /// <param name="nullabilityEligibility">The separately rendered physical predicates, when required.</param>
    /// <param name="repairPrecondition">The catalog-only repair predicate evaluated after classification.</param>
    /// <param name="matchingPrecondition">The exact match classified before the remaining evaluation.</param>
    /// <returns>The immutable SQL pieces without concatenated predicate intermediates.</returns>
    private static MySqlSafeMigrationSetupFragment BuildHexAssignment(
        string prefix,
        string statement,
        string suffix,
        (string NullableColumn, string RepairInvariant)? nullabilityEligibility = null,
        string? repairPrecondition = null,
        string? matchingPrecondition = null
    )
    {
        const string nullabilityPrefix = "), FALSE) ELSE FALSE END, @doka_sm_nullability_blocked = FALSE;"
            + "SET @doka_sm_sql = CASE WHEN @doka_sm_state IS NULL THEN CASE WHEN COALESCE((";

        const string eligibilitySuffix = ") AND @doka_sm_column_repair_eligible, FALSE) THEN CONVERT(0x";
        const string repairPrefix = "SET @doka_sm_repair_ok = CASE WHEN @doka_sm_state = 'different' "
            + "THEN COALESCE((";

        const string repairSuffix = "), FALSE) ELSE FALSE END;";

        // WHY: The physical predicates, matching classification and dependent repair statement
        // retain their exact order. Only their storage changes; no row proof or catalog result
        // escapes this operation, and the original logical fragment count still gates admission.
        return MySqlSafeMigrationSetupFragment.Compose(
            matchingPrecondition is null ? string.Empty : MatchingStatePrefix,
            matchingPrecondition ?? string.Empty,
            matchingPrecondition is null ? string.Empty : MatchingStateSuffix,
            prefix,
            nullabilityEligibility?.RepairInvariant ?? string.Empty,
            nullabilityEligibility is null ? string.Empty : nullabilityPrefix,
            nullabilityEligibility?.NullableColumn ?? string.Empty,
            nullabilityEligibility is null ? string.Empty : eligibilitySuffix,
            MySqlSafeMigrationSetupFragment.Part.Hex(statement),
            suffix,
            repairPrecondition is null ? string.Empty : repairPrefix,
            repairPrecondition ?? string.Empty,
            repairPrecondition is null ? string.Empty : repairSuffix);
    }
}
