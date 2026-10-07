namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    private readonly Dictionary<SafeMigrationOperation, CheckPredicateProof> _checkPredicateProofs = [];
    private readonly HashSet<(string Schema, string Table)> _checkPredicateUnknownTables = [];

    /// <summary>Retains guarded FALSE-only integer or empty-table opaque CHECK evidence.</summary>
    /// <param name="operations">The immutable source operation stream.</param>
    /// <param name="analyses">The corresponding complete guarded classifications.</param>
    internal void CaptureProjectedCheckPredicates(
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationProviderAnalysis[] analyses
    )
    {
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var operation = operations[ordinal];
            if (operation.Intent is EnsureCheckConstraintIntent
                && analyses[ordinal].ObservedState == SafeMigrationObservedState.Missing)
            {
                // WHY: Missing proves SELECT plus no FALSE integer row, or an
                // empty table for opaque predicates. Lossless ALTER retains
                // operand identities and values, including table emptiness.
                _checkPredicateProofs[operation] = new CheckPredicateProof(analyses[ordinal], false);
            }
        }
    }

    /// <summary>Captures fresh replacement truth without assuming a same-name local CHECK was dropped.</summary>
    /// <param name="connection">The metadata-visible analysis connection.</param>
    /// <param name="transaction">The caller's analysis transaction.</param>
    /// <param name="operations">The immutable ordered operation stream.</param>
    /// <param name="analyses">The corresponding guarded live classifications.</param>
    /// <param name="commandTimeout">The active command timeout.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task completing after bounded replacement row proofs are captured.</returns>
    internal async Task ReadProjectedCheckReplacementsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var localDrops = new HashSet<TransitionDependency>();
        var candidates = new List<SafeMigrationOperation>();

        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var operation = operations[ordinal];
            if (operation.Intent is DropCheckConstraintIntent drop
                && analyses[ordinal].ObservedState == SafeMigrationObservedState.Matching)
            {
                localDrops.Add(new TransitionDependency("check", drop.Schema ?? "dbo", drop.Table, drop.Name));
            }
            else if (operation.Intent is EnsureCheckConstraintIntent check
                && analyses[ordinal].ObservedState is SafeMigrationObservedState.Matching
                    or SafeMigrationObservedState.Different
                && localDrops.Contains(new TransitionDependency("check", check.Definition.Schema ?? "dbo",
                    check.Definition.Table, check.Definition.Name)))
            {
                candidates.Add(operation);
            }
        }

        var uniqueCandidates = candidates.Distinct().ToArray();
        var proofs = new SafeMigrationProviderAnalysis[uniqueCandidates.Length];

        for (var offset = 0; offset < uniqueCandidates.Length;
            offset += SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture)
        {
            var count = Math.Min(SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture,
                uniqueCandidates.Length - offset);

            var plans = uniqueCandidates.Skip(offset).Take(count).Select(operation =>
                _catalogSqlBuilder.BuildCheckPredicateCapturePlan((EnsureCheckConstraintIntent)operation.Intent))
                .ToArray();

            await ReadCatalogCaptureAsync(connection, transaction, commandTimeout, plans, offset, proofs,
                cancellationToken);
        }

        for (var ordinal = 0; ordinal < uniqueCandidates.Length; ordinal++)
        {
            CaptureProjectedCheckReplacement(uniqueCandidates[ordinal], proofs[ordinal]);
        }
    }

    /// <summary>Retains a complete replacement result, including FALSE rows and failed operand prerequisites.</summary>
    /// <param name="operation">The replacement operation with an initially occupied name.</param>
    /// <param name="analysis">The guarded local-only candidate classification.</param>
    internal void CaptureProjectedCheckReplacement(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
    {
        if (analysis.ObservedState is not (SafeMigrationObservedState.Different or SafeMigrationObservedState.Matching))
        {
            _checkPredicateProofs[operation] = new CheckPredicateProof(analysis, true);
        }
    }

    private SafeMigrationProviderAnalysis QualifyProjectedCheckPredicate(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
    {
        if (operation.Intent is not EnsureCheckConstraintIntent check
            || analysis.IsInvariantUnsupported || analysis.ObservedState == SafeMigrationObservedState.Matching
            || !_checkPredicateProofs.TryGetValue(operation, out var proof))
        {
            return analysis;
        }

        var key = (Schema: check.Definition.Schema ?? "dbo", check.Definition.Table);
        if (_transitionDataChangedGlobally
            || _checkPredicateUnknownTables.Contains(key)
            || analysis.Code == "projected_data_state_unknown")
        {
            // WHY: A named drop can make Core's catalog projection Missing,
            // but it cannot prove replacement row truth after DML or changed
            // operand identity. Missing is not an empty-table certificate.
            return analysis.ObservedState == SafeMigrationObservedState.Missing
                ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.PrerequisiteMissing,
                    SafeMigrationRepairCapability.None, false, "projected_data_state_unknown")
                    { IsOpaqueProjectionUnknown = true, RequiresLiveDataProof = true }
                : analysis;
        }

        if (proof.RequiresLocalDrop && !_acceptedTransitionDrops.Contains(ResolveTransitionDropIdentity(
            new TransitionDependency("check", key.Schema, key.Table, check.Definition.Name))))
        {
            return analysis;
        }

        return analysis.Code == "projected_structure_state_unknown"
            || proof.RequiresLocalDrop && analysis.ObservedState == SafeMigrationObservedState.Missing
                ? proof.Analysis : analysis;
    }

    private void ObserveProjectedCheckPredicate(
        SafeMigrationOperation operation,
        SafeMigrationDecision decision
    )
    {
        if (!decision.ShouldExecute)
        {
            return;
        }

        switch (operation.Intent)
        {
            case AlterColumnIntent { OldDefinition: not null } column
                when _catalogSqlBuilder.IsSupportedIntegerWidening(column.OldDefinition, column.Definition):
                break;
            case AlterColumnIntent column:
                _checkPredicateUnknownTables.Add((column.Schema ?? "dbo", column.Table));
                break;
            case DropColumnIntent column:
                _checkPredicateUnknownTables.Add((column.Schema ?? "dbo", column.Table));
                break;
            case RenameColumnIntent column:
                _checkPredicateUnknownTables.Add((column.Schema ?? "dbo", column.Table));
                break;
            case DropTableIntent table:
                _checkPredicateUnknownTables.Add((table.Schema ?? "dbo", table.Table));
                break;
            case RenameTableIntent table:
                _checkPredicateUnknownTables.Add((table.Schema ?? "dbo", table.Name));
                _checkPredicateUnknownTables.Add((table.NewSchema ?? table.Schema ?? "dbo",
                    table.NewName ?? table.Name));
                break;
        }
    }

    private sealed record CheckPredicateProof(
        SafeMigrationProviderAnalysis Analysis,
        bool RequiresLocalDrop
    );
}
