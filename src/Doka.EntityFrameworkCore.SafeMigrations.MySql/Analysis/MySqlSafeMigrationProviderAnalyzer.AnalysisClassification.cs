namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Assembles and reads bounded classifications without changing immutable source ordinals.</summary>
    /// <param name="connection">The actual open analysis connection.</param>
    /// <param name="catalog">The qualified invocation transport limits.</param>
    /// <param name="window">The current operation window and its already bound proof slots.</param>
    /// <param name="results">The invocation-owned original ordinal output slots.</param>
    /// <param name="cancellationToken">The token canceling batch assembly and actual native reads.</param>
    /// <returns>Completion after all unresolved window slots have been classified.</returns>
    private async Task AnalyzeClassificationBatchesAsync(
        DbConnection connection,
        AnalysisCatalog catalog,
        AnalysisWindow window,
        SafeMigrationProviderAnalysis[] results,
        CancellationToken cancellationToken
    )
    {
        var operationWindow = window.Operations;
        var plans = window.Plans;
        var shortCircuitStates = window.ShortCircuitStates;
        var dataProbeResults = window.DataProbeResults;
        var operationOffset = window.OperationOffset;
        var maximumPayloadBytes = catalog.MaximumPayloadBytes;
        var commandTimeout = catalog.CommandTimeout;
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);

        // WHY: Local classifications do not divide the immutable live
        // snapshot. Pack the unresolved work while retaining the
        // original operation identities for later ordered projection.
        var workOrder = SafeMigrationCatalogWorkOrder.Create(
            operationWindow.Length,
            ordinal => shortCircuitStates[ordinal] is null,
            cancellationToken);

        var workIndex = 0;
        var captureDropCompleteness = plans.Any(static plan =>
            plan.DropLeavesNoGeneratedColumnsExpression is not null);

        while (workIndex < workOrder.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);
            Exception? batchFailure = null;
            try
            {
                var submittedOrdinals = new List<int>(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
                    * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch);

                var batchParameterCount = 0;
                var batchPayloadBytes = 0;
                while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                    && workIndex < workOrder.Length)
                {
                    var command = batch.CreateCommand();
                    var parameterizer = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
                    var selections = new List<string>(
                        Math.Min(
                            SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                            workOrder.Length - workIndex));

                    var sqlBytes = trailerBytes;
                    while (workIndex < workOrder.Length
                        && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                    {
                        var ordinal = workOrder[workIndex];
                        var checkpoint = parameterizer.Capture();
                        var plan = plans[ordinal];
                        MySqlDataProbeResult? dataProbeResult = plan.DataProbe is null
                            ? null
                            : dataProbeResults[ordinal]
                            ?? throw new InvalidOperationException(
                                "The MySQL narrowing probe returned no classification result.");

                        var selection = BuildClassificationSelection(
                            plan,
                            dataProbeResult,
                            parameterizer,
                            operationOffset + ordinal,
                            captureDropCompleteness);

                        var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                            + (selections.Count == 0
                                ? 0
                                : separatorBytes);

                        var statementPayload = sqlBytes + selectionBytes + parameterizer.Utf8PayloadBytes;
                        var prospectiveBatchParameters = batchParameterCount + parameterizer.Count;
                        var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                        if (SafeMigrationCatalogQueryLimits.Exceeded(
                            prospectiveBatchParameters,
                            prospectiveBatchPayload,
                            maximumPayloadBytes))
                        {
                            parameterizer.Rollback(checkpoint);
                            if (selections.Count == 0)
                            {
                                if (batch.Count == 1)
                                {
                                    batch.RemoveLastCommand(command);

                                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                        operationOffset + ordinal,
                                        prospectiveBatchParameters,
                                        prospectiveBatchPayload);
                                }
                            }

                            break;
                        }

                        selections.Add(selection);
                        submittedOrdinals.Add(operationOffset + ordinal);
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

                    batchParameterCount += parameterizer.Count;
                    batchPayloadBytes += sqlBytes + parameterizer.Utf8PayloadBytes;
                }

                await ReadAnalysisAsync(
                    batch,
                    results,
                    plans,
                    operationWindow,
                    dataProbeResults,
                    operationOffset,
                    submittedOrdinals,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                batchFailure = exception;

                throw;
            }
            finally
            {
                await SafeMigrationAnalysisCleanup.DisposeAsync(
                    batch,
                    batchFailure,
                    "SafeMigrations.AnalysisBatchDisposeException");
            }
        }
    }

    /// <summary>Keeps window-local plans and proof slots bound to their original operation ordinals.</summary>
    /// <param name="Operations">The current bounded operation window.</param>
    /// <param name="Plans">Captured runtime plans in that exact window order.</param>
    /// <param name="ShortCircuitStates">Qualified local prerequisite outcomes.</param>
    /// <param name="DataProbeResults">Bound narrowing and transition proof results.</param>
    /// <param name="OperationOffset">The window's first full-stream ordinal.</param>
    private readonly record struct AnalysisWindow(
        SafeMigrationOperation[] Operations,
        MySqlSafeMigrationRuntimePlan[] Plans,
        MySqlShortCircuitState?[] ShortCircuitStates,
        MySqlDataProbeResult?[] DataProbeResults,
        int OperationOffset
    );
}
