namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    private async Task ReadAnalysisAsync(
        SafeMigrationCatalogBatch batch,
        SafeMigrationProviderAnalysis[] results,
        PostgreSqlSafeMigrationRuntimePlan?[] plans,
        PostgreSqlDataProbeResult?[] dataProbeResults,
        IReadOnlyList<SafeMigrationOperation> operations,
        int operationOffset,
        IReadOnlyList<int> submittedOrdinals,
        CancellationToken cancellationToken
    )
    {
        var consumed = 0;
        await batch.ForEachResultSetAsync(
            async (reader, callbackCancellationToken) =>
            {
                if (reader.FieldCount != 10)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL SafeMigrations classifier returned an unexpected result shape.");
                }

                while (await reader.ReadAsync(callbackCancellationToken))
                {
                    var ordinal = reader.GetInt32(0);
                    SafeMigrationCatalogWorkOrder.ValidateResultOrdinal(ordinal, submittedOrdinals, ref consumed);
                    if (results[ordinal] is not null)
                    {
                        throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations classifier returned an already resolved ordinal.");
                    }

                    var state = ParseState(reader.GetString(1));
                    var repairCapability = reader.GetBoolean(3)
                        ? SafeMigrationRepairCapability.Safe
                        : SafeMigrationRepairCapability.None;

                    var plan = plans[ordinal - operationOffset]
                        ?? throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations classifier has no runtime plan for its result ordinal.");

                    var code = reader.IsDBNull(4)
                        ? state == SafeMigrationObservedState.Unsupported
                            ? plan.UnsupportedCode ?? "classified_unsupported"
                            : ClassificationCode(state)
                        : reader.GetString(4);

                    var emptyGeneratedDropSet = DecodeGeneratedDropCompleteness(
                        operations[ordinal].Intent,
                        state,
                        ref code);

                    CaptureColumnPreservingIndexDrop(operations[ordinal].Intent, state, ref code);

                    var evidence = plan.ModelManagedRowEvidenceExpression is null
                        ? null
                        : SafeMigrationModelManagedDataEvidence.Parse(
                            reader.GetString(5),
                            plan.ModelManagedRowCount,
                            plan.ModelManagedDependencyCountsExpression is null
                                ? string.Empty
                                : reader.GetString(6),
                            plan.ModelManagedDependencyCount,
                            "PostgreSQL");

                    IReadOnlyList<SafeMigrationFacetDifference> differences;
                    if (state == SafeMigrationObservedState.Different
                        && plan.DifferentDifference is not null)
                    {
                        // WHY: Model-managed values stay outside SQL diagnostics;
                        // the provider reports only the bounded mismatch category.
                        differences = [plan.DifferentDifference];
                    }
                    else
                    {
                        differences = state is not (SafeMigrationObservedState.Different
                                or SafeMigrationObservedState.DataBlocked
                                or SafeMigrationObservedState.Unsupported)
                            || reader.IsDBNull(7)
                                ? []
                                : SafeMigrationFacetDifferenceParser.Parse(reader.GetString(7), "PostgreSQL");
                    }

                    var operationalImpact = state == SafeMigrationObservedState.DataBlocked
                        || (state == SafeMigrationObservedState.Different
                            && repairCapability == SafeMigrationRepairCapability.Safe)
                            ? plan.RepairOperationalImpact
                            : SafeMigrationOperationalImpact.NotApplicable;

                    // WHY: A validated NO INHERIT parent can match even though
                    // its descendants need a fresh row proof. Diagnostics and
                    // attnotnull alone cannot convey that eligibility.
                    var requiresNullabilityDataProof = reader.GetBoolean(9);

                    var analysis = new SafeMigrationProviderAnalysis(
                        state,
                        repairCapability,
                        reader.GetBoolean(2),
                        code,
                        operationalImpact,
                        differences)
                    {
                        IsInvariantUnsupported = state == SafeMigrationObservedState.Unsupported
                            && plan.IsStaticallyUnsupported,
                        MatchedObjectName = state == SafeMigrationObservedState.Matching
                            && !reader.IsDBNull(8)
                                ? reader.GetString(8)
                                : null,
                        ModelManagedDataEvidence = evidence,
                        DropLeavesNoGeneratedColumns = emptyGeneratedDropSet,

                        // WHY: Every VARCHAR transition carries a probe template,
                        // but widening is catalog-only. Only a catalog-confirmed
                        // narrowing or nullable-to-required result becomes stale
                        // after projected DML.
                        RequiresLiveDataProof = dataProbeResults[ordinal]?.IsRequired == true
                            || requiresNullabilityDataProof,
                    };

                    results[ordinal] = analysis;
                }
            },
            cancellationToken);

        SafeMigrationCatalogWorkOrder.ValidateCompletion(consumed, submittedOrdinals);
    }

    private static SafeMigrationObservedState ParseState(
        string state
    ) => state switch
    {
        "missing" => SafeMigrationObservedState.Missing,
        "matching" => SafeMigrationObservedState.Matching,
        "different" => SafeMigrationObservedState.Different,
        "unsupported" => SafeMigrationObservedState.Unsupported,
        "data_blocked" => SafeMigrationObservedState.DataBlocked,
        "prerequisite_missing" => SafeMigrationObservedState.PrerequisiteMissing,
        "transition_ready" => SafeMigrationObservedState.TransitionReady,
        _ => throw new InvalidOperationException("The PostgreSQL SafeMigrations classifier returned an unknown state."),
    };

    private static string ClassificationCode(
        SafeMigrationObservedState state
    ) => state switch
    {
        SafeMigrationObservedState.Missing => "classified_missing",
        SafeMigrationObservedState.Matching => "classified_matching",
        SafeMigrationObservedState.Different => "classified_different",
        SafeMigrationObservedState.Unsupported => "classified_unsupported",
        SafeMigrationObservedState.DataBlocked => "classified_data_blocked",
        SafeMigrationObservedState.PrerequisiteMissing => "classified_prerequisite_missing",
        SafeMigrationObservedState.TransitionReady => "classified_transition_ready",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
