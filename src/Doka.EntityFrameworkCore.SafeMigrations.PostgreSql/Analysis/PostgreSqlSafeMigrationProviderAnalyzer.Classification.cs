namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    /// <summary>Classifies original operation identities using bounded windows and source-bound proofs.</summary>
    /// <param name="connection">The invocation-owned open analysis connection.</param>
    /// <param name="operations">The original ordered immutable safe-operation stream.</param>
    /// <param name="commandTimeout">The caller's native command budget.</param>
    /// <param name="cancellationToken">The actual discovery, capture and transport cancellation token.</param>
    /// <returns>One native classification at every original operation ordinal.</returns>
    private async Task<SafeMigrationProviderAnalysis[]> ClassifyOperationsAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await CaptureCurrentSchemaIdentityAsync(connection, operations, commandTimeout, cancellationToken);
        await CaptureProjectedEventTriggerEffectsAsync(connection, commandTimeout, cancellationToken);
        var shortCircuitStates = await FindShortCircuitStatesAsync(
            connection,
            operations,
            commandTimeout,
            cancellationToken);

        var expectedTableConstraints = PostgreSqlSafeMigrationAnalysisConstraintCatalog.Create(
            _analysisOperationStream ?? operations,
            _schemaIdentityMismatchOrdinals,
            _currentSchema);

        var dataProbeResults = await ResolveDataProbeResultsAsync(
            connection,
            operations,
            shortCircuitStates,
            commandTimeout,
            cancellationToken);

        var results = new SafeMigrationProviderAnalysis[operations.Count];
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        var operationOffset = 0;
        foreach (var operationWindow in operations.Chunk(
            SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // WHY: Map window-local ordinals back to their original analysis slots.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var localOrdinal = 0; localOrdinal < operationWindow.Length; localOrdinal++)
            {
                if (shortCircuitStates[operationOffset + localOrdinal] is { } shortCircuitState)
                {
                    results[operationOffset + localOrdinal] = ShortCircuitAnalysis(
                        operationOffset + localOrdinal,
                        shortCircuitState,
                        operationWindow[localOrdinal].Intent);
                }
            }

            // WHY: Keep expensive runtime plans capture-bounded rather
            // than retaining the complete migration's SQL. The full
            // transition catalogs and data proofs above remain shared.
            var plans = new PostgreSqlSafeMigrationRuntimePlan?[operationWindow.Length];

            // WHY: Create evaluates this predicate synchronously and never
            // retains it; later window-offset changes cannot reach it.
            var workOrder = SafeMigrationCatalogWorkOrder.Create(
                operationWindow.Length,

                // ReSharper disable once AccessToModifiedClosure
                ordinal => shortCircuitStates[operationOffset + ordinal] is null,
                cancellationToken);

            var window = new ClassificationWindow(
                operationOffset,
                plans,
                workOrder,
                expectedTableConstraints,
                dataProbeResults);

            var workIndex = 0;
            while (workIndex < workOrder.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);
                Exception? batchFailure = null;

                try
                {
                    var submittedOrdinals = CaptureClassificationBatch(
                        batch,
                        operationWindow,
                        in window,
                        ref workIndex,
                        separatorBytes,
                        trailerBytes);

                    await ReadAnalysisAsync(
                        batch,
                        results,
                        plans,
                        dataProbeResults,
                        operations,
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

            operationOffset += operationWindow.Length;
        }

        // WHY: Array initialization leaves null slots until each original ordinal is
        // classified. Nullable annotations cannot prove complete result ownership.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (results.Any(static result => result is null))
        {
            throw new InvalidOperationException(
                "The PostgreSQL SafeMigrations classifier returned an inconsistent row count.");
        }

        return results;
    }
}
