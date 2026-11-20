namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    /// <summary>Resets only root-capture evidence before a new invocation can publish projection authority.</summary>
    /// <param name="operations">The original safe stream that determines dependency qualification needs.</param>
    private void ResetCapturedAnalysis(
        IReadOnlyList<SafeMigrationOperation> operations
    )
    {
        _projectedAlterPhysicalSnapshots.Clear();
        _indexDropPhysicalSnapshots?.Clear();
        _projectedExplicitTimestampDefaults.Clear();
        _projectedColumnSessionEnvironments.Clear();
        _projectedRequiredAddCapsByOperation?.Clear();
        _projectedRequiredAddCapsByAnalysis?.Clear();
        _renamedSourceAnalyses.Clear();
        _renameTargetConflicts.Clear();
        ClearLivePrerequisites();
        _tableNamesIgnoreCase = false;
        _expiredGeneratedDependencyOwners = null;
        _requiresGeneratedDependencyQualification = operations.Any(static operation =>
            operation.Intent is RenameColumnIntent or DropColumnIntent);
    }

    /// <summary>Publishes final physical, prerequisite and rename proofs only after complete classifications.</summary>
    /// <param name="context">The context owning the analysis connection and target model.</param>
    /// <param name="operations">The original complete safe stream.</param>
    /// <param name="captureRenameSources">Whether root-only lifetime proofs and rebound captures are needed.</param>
    /// <param name="catalog">The qualified immutable invocation catalog.</param>
    /// <param name="results">The complete original ordinal classifications.</param>
    /// <param name="cancellationToken">The token canceling actual final proof and source-capture reads.</param>
    /// <returns>Completion after every required projection certificate has been retained.</returns>
    private async Task CompleteAnalysisAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        bool captureRenameSources,
        AnalysisCatalog catalog,
        SafeMigrationProviderAnalysis[] results,
        CancellationToken cancellationToken
    )
    {
        var connection = catalog.Connection;
        var maximumPayloadBytes = catalog.MaximumPayloadBytes;
        var commandTimeout = catalog.CommandTimeout;
        var backfillConstraints = catalog.BackfillConstraints;
        var alterPhysicalSnapshots = catalog.AlterPhysicalSnapshots;
        var columnSessionEnvironment = catalog.ColumnSessionEnvironment;
        var isMariaDb = catalog.IsMariaDb;

        // WHY: The classifier fills an initially null reference array; incomplete native rows must fail closed.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (results.Any(static result => result is null))
        {
            throw new InvalidOperationException(
                "The MySQL SafeMigrations classifier returned an inconsistent row count.");
        }

        // WHY: Same-type Ensure repair can use its metadata-only branch,
        // which does not establish the transition's dependency guards.
        // Refine after batched diagnostics and before reference-bound
        // snapshots are attached, retaining only actual type expansion.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < results.Length; ordinal++)
        {
            var analysis = results[ordinal];
            if (analysis.RepairPreservesLiveValueDomain)
            {
                results[ordinal] = analysis.Differences.All(static difference =>
                    !StringComparer.Ordinal.Equals(difference.Facet, "column_store_type"))
                    ? analysis.WithRepairPreservesLiveValueDomain(false)
                    : analysis.WithRepairPreservesPhysicalKeys();
            }
        }

        AttachProjectedAlterPhysicalSnapshots(operations, results, alterPhysicalSnapshots);
        AttachProjectedColumnSessionEnvironment(operations, results, columnSessionEnvironment);
        if (captureRenameSources)
        {
            await CaptureLivePrerequisitesAsync(
                context,
                operations,
                results,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            await CaptureMultiValuedIndexDropProofsAsync(
                context,
                operations,
                results,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            await CaptureForeignKeyReplacementProofsAsync(
                context,
                operations,
                results,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            CaptureGeneratedColumnSourceProofs(operations, results, isMariaDb);
            CompleteGeneratedIndexIdentityFacts(operations, results);
        }

        if (captureRenameSources)
        {
            await ReadMemberRenameTargetEvidenceAsync(
                connection,
                operations,
                results,
                _currentDatabase,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            await CaptureRenameSourceAnalysesAsync(
                context,
                operations,
                results,
                backfillConstraints,
                cancellationToken);
        }
    }
}
