namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer
{
    private HashSet<DropIndexIntent>? _columnPreservingIndexDrops;
    private bool _indexDropCatalogReadable;
    private bool _columnConstraintCatalogReadable;

    /// <inheritdoc />
    public bool PreservesColumnSourcesAfterIndexDrop(
        DropIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
        && _columnPreservingIndexDrops?.Contains(intent) == true
        && CanRetainIndexDropColumnProofs();

    /// <inheritdoc />
    public bool CanReuseLiveColumnProofAfterIndexDrops(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    ) => CanRetainIndexDropColumnProofs()
        && !liveAnalysis.RequiresLiveDataProof
        && (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
            || liveAnalysis is
            {
                ObservedState: SafeMigrationObservedState.Different,
                RepairCapability: SafeMigrationRepairCapability.Safe
            })
        && PostgreSqlSafeMigrationCatalogSqlBuilder.IsIntegerWidening(intent)
        && (!columns.TryGetProjectedColumn(intent.Table, intent.Schema, intent.Definition.Name, out var projected)
            || SafeMigrationDefinitionEquivalence.Column(projected, intent.OldDefinition!));

    /// <summary>Retains source evidence only inside the captured schema and callback-free native lifetime.</summary>
    private bool CanRetainIndexDropColumnProofs()
        // WHY: DROP INDEX and unchanged-nullability widening emit no row writes.
        // Installed DML triggers are harmless here; a preceding reachable write
        // is still fenced by its recorded trigger origin.
        => _eventTriggerRisk == PostgreSqlTriggerRisk.None
            && _triggerOriginOrdinal is null
            && _schemaIdentityOriginOrdinal is null
            && !_generatedIndexHasOpaqueBoundary;

    /// <summary>Consumes the provider-private role marker without changing public diagnostic classification.</summary>
    private void CaptureColumnPreservingIndexDrop(
        SafeMigrationIntent intent,
        SafeMigrationObservedState state,
        ref string code
    )
    {
        if (code != PostgreSqlSafeMigrationCatalogSqlBuilder.OrdinaryIndexDropProofCode)
        {
            return;
        }

        if (intent is not DropIndexIntent drop
            || state != SafeMigrationObservedState.Matching)
        {
            throw new InvalidOperationException("PostgreSQL returned invalid ordinary index-drop evidence.");
        }

        (_columnPreservingIndexDrops ??= []).Add(drop);
        code = ClassificationCode(state);
    }
}
