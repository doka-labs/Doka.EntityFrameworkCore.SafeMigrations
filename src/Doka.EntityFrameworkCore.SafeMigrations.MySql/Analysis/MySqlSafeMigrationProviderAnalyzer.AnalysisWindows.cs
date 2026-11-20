namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Classifies bounded operation windows against one immutable invocation catalog.</summary>
    /// <param name="context">The context owning the actual connection and target model.</param>
    /// <param name="operations">The immutable full stream whose ordinals remain stable.</param>
    /// <param name="catalog">Qualified full-stream contracts shared by every bounded window.</param>
    /// <param name="results">The invocation-owned output slots indexed by original ordinal.</param>
    /// <param name="cancellationToken">The token canceling real metadata and data probes.</param>
    /// <returns>Completion after every window has published its complete classifications.</returns>
    private async Task AnalyzeOperationWindowsAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        AnalysisCatalog catalog,
        SafeMigrationProviderAnalysis[] results,
        CancellationToken cancellationToken
    )
    {
        var connection = catalog.Connection;
        var maximumPayloadBytes = catalog.MaximumPayloadBytes;
        var commandTimeout = catalog.CommandTimeout;
        var backfillConstraints = catalog.BackfillConstraints;
        var expectedUniqueIndexes = catalog.ExpectedUniqueIndexes;
        var expectedTableConstraints = catalog.ExpectedTableConstraints;
        var indexEnvironments = catalog.IndexEnvironments;
        var primaryKeyReplacements = catalog.PrimaryKeyReplacements;
        var snapshotEligible = catalog.IsMariaDb;
        Dictionary<MySqlDataProbeContract, MySqlDataProbeResult> dataProbeCache = [];
        Dictionary<MySqlDataProbeIdentity, bool> blockingDataCache = [];

        var operationOffset = 0;
        foreach (var operationWindow in operations.Chunk(
            SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
        {
            var plans = CapturePlans(
                operationWindow,
                context.Model,
                expectedUniqueIndexes,
                expectedTableConstraints,
                backfillConstraints,
                primaryKeyReplacements: primaryKeyReplacements);

            AttachIndexPhysicalEnvironments(operationWindow, plans, indexEnvironments);
            AttachGeneratedIndexIdentityCapture(operationWindow, plans);
            var shortCircuitStates = await FindPrerequisiteShortCircuitStatesAsync(
                connection,
                plans,
                maximumPayloadBytes,
                commandTimeout,
                operationOffset,
                cancellationToken);

            CaptureGeneratedIndexIdentityFacts(operationWindow, plans);

            // WHY: Explicit qualifiers keep live reads, including a foreign key's principal.
            // Unqualified operations use local catalog rows plus incoming foreign keys owned
            // by other databases; those dependencies must not disappear from the snapshot.
            var bindable = new bool[operationWindow.Length];
            var bindableCount = 0;

            // WHY: Keep window-local plans and result slots aligned with their original operation ordinals.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var ordinal = 0; ordinal < operationWindow.Length; ordinal++)
            {
                bindable[ordinal] = shortCircuitStates[ordinal] is null
                    && !plans[ordinal].IsStaticallyUnsupported
                    && !MySqlSafeMigrationCatalogSqlBuilder.HasDatabaseQualifier(operationWindow[ordinal].Intent);

                if (bindable[ordinal])
                {
                    bindableCount++;
                }
            }

            // WHY: Copying the catalog costs a fixed set of statements, so it only pays once a
            // window classifies at least a full statement's worth of operations against it. A
            // migration of a handful of operations stays on the live views, where the copy would
            // be pure overhead rather than a saving.
            var useSnapshot = snapshotEligible
                && bindableCount >= SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;

            await using var catalogSnapshot = useSnapshot
                ? await MySqlCatalogSnapshot.TryCreateAsync(connection, commandTimeout, cancellationToken)
                : null;

            if (useSnapshot && catalogSnapshot is null)
            {
                // WHY: An unavailable snapshot capability applies to this analysis session.
                // Do not repeat the same failed DDL for every capture window.
                snapshotEligible = false;
            }

            try
            {
                if (catalogSnapshot is not null)
                {
                    // WHY: Keep window-local plans and result slots aligned with their original operation ordinals.
                    // ReSharper disable once ForCanBeConvertedToForeach
                    // ReSharper disable once LoopCanBeConvertedToQuery
                    for (var ordinal = 0; ordinal < plans.Length; ordinal++)
                    {
                        if (bindable[ordinal])
                        {
                            plans[ordinal] = plans[ordinal] with { CatalogRelations = catalogSnapshot.Resolve };
                        }
                    }
                }

                await BindAnalysisProofsAsync(
                    connection,
                    catalog,
                    plans,
                    shortCircuitStates,
                    operationOffset,
                    cancellationToken);

                var dataProbeResults = await ResolveDataProbeResultsAsync(
                    connection,
                    operationWindow,
                    plans,
                    shortCircuitStates,
                    dataProbeCache,
                    blockingDataCache,
                    context.Model,
                    expectedUniqueIndexes,
                    expectedTableConstraints,
                    backfillConstraints,
                    maximumPayloadBytes,
                    commandTimeout,
                    operationOffset,
                    cancellationToken);

                // WHY: Keep window-local plans and result slots aligned with their original operation ordinals.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var localOrdinal = 0; localOrdinal < operationWindow.Length; localOrdinal++)
                {
                    if (shortCircuitStates[localOrdinal] is { } shortCircuitState)
                    {
                        results[operationOffset + localOrdinal] = ShortCircuitAnalysis(
                            shortCircuitState,
                            plans[localOrdinal],
                            operationWindow[localOrdinal].Intent);
                    }
                }

                await AnalyzeClassificationBatchesAsync(
                    connection,
                    catalog,
                    new AnalysisWindow(
                        operationWindow,
                        plans,
                        shortCircuitStates,
                        dataProbeResults,
                        operationOffset),
                    results,
                    cancellationToken);

                await PopulateColumnDiagnosticsAsync(
                    connection,
                    operationWindow,
                    results,
                    context.Model,
                    expectedUniqueIndexes,
                    expectedTableConstraints,
                    backfillConstraints,
                    maximumPayloadBytes,
                    commandTimeout,
                    operationOffset,
                    cancellationToken);

                CaptureProjectedRequiredAddPlans(operationWindow, plans);

                operationOffset += operationWindow.Length;
            }
            catch (Exception exception)
            {
                if (catalogSnapshot is not null)
                {
                    // WHY: Normal disposal may fail after a timeout has closed the connection.
                    // Release owned copies without hiding the original analysis failure.
                    await catalogSnapshot.DisposeAfterFailureAsync(exception);
                }

                throw;
            }
        }
    }

    /// <summary>Binds independent prerequisite proofs before any state-dependent row classification.</summary>
    /// <param name="connection">The open invocation-owned connection.</param>
    /// <param name="catalog">The qualified transport budgets and command timeout.</param>
    /// <param name="plans">Window-local captured plans with stable ordinal bindings.</param>
    /// <param name="shortCircuitStates">Previously proven prerequisite rejections in the same window.</param>
    /// <param name="operationOffset">The window's first original operation ordinal.</param>
    /// <param name="cancellationToken">The token canceling the actual proof reads.</param>
    /// <returns>Completion after every independent proof family has been bound in its original order.</returns>
    private async Task BindAnalysisProofsAsync(
        DbConnection connection,
        AnalysisCatalog catalog,
        MySqlSafeMigrationRuntimePlan[] plans,
        MySqlShortCircuitState?[] shortCircuitStates,
        int operationOffset,
        CancellationToken cancellationToken
    )
    {
        var maximumPayloadBytes = catalog.MaximumPayloadBytes;
        var commandTimeout = catalog.CommandTimeout;
        await FindRequiredColumnAddFactsAsync(
            connection,
            plans,
            shortCircuitStates,
            maximumPayloadBytes,
            commandTimeout,
            operationOffset,
            cancellationToken);

        await FindBinaryDefaultProofsAsync(
            connection,
            plans,
            shortCircuitStates,
            maximumPayloadBytes,
            commandTimeout,
            operationOffset,
            cancellationToken);

        await FindIdentifierReferenceProofsAsync(
            connection,
            plans,
            shortCircuitStates,
            maximumPayloadBytes,
            commandTimeout,
            operationOffset,
            cancellationToken);

        await FindStateEvaluationGuardStatesAsync(
            connection,
            plans,
            shortCircuitStates,
            maximumPayloadBytes,
            commandTimeout,
            operationOffset,
            cancellationToken);
    }
}
