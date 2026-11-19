namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    /// <summary>Captures one bounded batch without executing it or changing original result identities.</summary>
    /// <param name="batch">The caller-owned transport batch receiving qualified native statements.</param>
    /// <param name="operations">The existing bounded owned operation snapshot.</param>
    /// <param name="window">The borrowed operation/proof window whose ordinal mapping remains stable.</param>
    /// <param name="workIndex">The next unresolved window-local work position, advanced only after capture.</param>
    /// <param name="separatorBytes">The existing UTF-8 separator cost used by the aggregate budget.</param>
    /// <param name="trailerBytes">The existing UTF-8 trailer cost used by the aggregate budget.</param>
    /// <returns>The exact ordered original ordinals submitted by this batch.</returns>
    private List<int> CaptureClassificationBatch(
        SafeMigrationCatalogBatch batch,
        SafeMigrationOperation[] operations,
        in ClassificationWindow window,
        ref int workIndex,
        int separatorBytes,
        int trailerBytes
    )
    {
        var operationOffset = window.Offset;
        var plans = window.Plans;
        var workOrder = window.WorkOrder;
        var expectedTableConstraints = window.Constraints;
        var dataProbeResults = window.DataProbes;
        var submittedOrdinals = new List<int>(
            SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
            * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch);

        var batchParameterCount = 0;
        var batchPayloadBytes = 0;

        // WHY: Capture is one rollback-coordinated packing state machine: cursor, parameter
        // checkpoint and statement/batch byte budgets must advance together, never per helper.
        while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
            && workIndex < workOrder.Length)
        {
            var command = batch.CreateCommand();
            using var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
            var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                _typeMappingSource,
                _sqlGenerationHelper,
                parameters.AddString,
                parameters.Add);

            var selections = new List<string>(
                Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                    workOrder.Length - workIndex));

            var sqlBytes = trailerBytes;
            while (workIndex < workOrder.Length
                && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
            {
                var localOrdinal = workOrder[workIndex];
                var ordinal = operationOffset + localOrdinal;
                var operation = operations[localOrdinal]
                    ?? throw new ArgumentException(
                        "The operation batch cannot contain null entries.",
                        nameof(operations));

                var checkpoint = parameters.Capture();
                var plan = builder.Build(
                    operation,
                    expectedTableConstraints.Find(ordinal, operation),
                    includeRuntimeIndexInputGuard: false,
                    includeIndexDropColumnProof: _indexDropCatalogReadable);

                plans[localOrdinal] = plan;
                PostgreSqlDataProbeResult? dataProbeResult = plan.DataProbe is null
                    ? null
                    : dataProbeResults[ordinal]
                    ?? throw new InvalidOperationException(
                        "The PostgreSQL narrowing probe returned no classification result.");

                var selection = plan.RenderAnalysisSelection(
                    ordinal,
                    dataProbeResult is { } evidence
                        ? (evidence.IsBlocked, evidence.IsTransitionEligible)
                        : null);

                var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                    + (selections.Count == 0
                        ? 0
                        : separatorBytes);

                var statementPayload = sqlBytes + selectionBytes + parameters.Utf8PayloadBytes;
                var prospectiveBatchParameters = batchParameterCount + parameters.Count;
                var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                if (SafeMigrationCatalogQueryLimits.Exceeded(
                    prospectiveBatchParameters,
                    prospectiveBatchPayload,
                    SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                {
                    parameters.Rollback(checkpoint);
                    if (selections.Count == 0)
                    {
                        if (batch.Count == 1)
                        {
                            batch.RemoveLastCommand(command);

                            throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                ordinal,
                                prospectiveBatchParameters,
                                prospectiveBatchPayload);
                        }
                    }

                    break;
                }

                selections.Add(selection);
                submittedOrdinals.Add(ordinal);
                sqlBytes += selectionBytes;
                workIndex++;
            }

            if (selections.Count == 0)
            {
                batch.RemoveLastCommand(command);

                break;
            }

            command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                + SafeMigrationCatalogQueryLimits.Trailer;

            batchParameterCount += parameters.Count;
            batchPayloadBytes += sqlBytes + parameters.Utf8PayloadBytes;
        }

        return submittedOrdinals;
    }

    /// <summary>Keeps the existing capture-owned arrays aligned without allocating a heap run-state object.</summary>
    /// <param name="Offset">Its first ordinal in the complete authored stream.</param>
    /// <param name="Plans">The original plan slots populated while statements are captured.</param>
    /// <param name="WorkOrder">The original unresolved local ordinal order.</param>
    /// <param name="Constraints">The already captured complete-stream constraint evidence.</param>
    /// <param name="DataProbes">The source-bound row evidence indexed by original ordinal.</param>
    private readonly record struct ClassificationWindow(
        int Offset,
        PostgreSqlSafeMigrationRuntimePlan?[] Plans,
        int[] WorkOrder,
        PostgreSqlSafeMigrationAnalysisConstraintCatalog Constraints,
        PostgreSqlDataProbeResult?[] DataProbes
    );
}
