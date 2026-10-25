namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    /// <summary>
    /// Reuses captured column evidence within certified index-removal and non-data transition lifetimes.
    /// </summary>
    private bool CanReuseColumnProofAfterIndexDrops(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        bool allowUntouchedSource = false
    )
    {
        var key = new TableKey(intent.Table, intent.Schema);
        if ((_projectedIndexDropOnlyTables?.Contains(key) != true
                && !(allowUntouchedSource && !_projectedStructurallyModifiedTables.Contains(key)))
            || _projectedColumnAnalyzer is not { } provider
            || _hasOpaqueProviderPostcondition
            || _hasOpaqueSqlPostcondition
            || _providerDataMutationVersion != 0
            || HasUnanalyzedDataChanges(intent.Table, intent.Schema)
            || _projectedMissingTables.Contains(key)
            || IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
            || _projectedUnknownPhysicalKeys.Contains(key)
            || (_prerequisites.TryGetValue(key, out var prerequisites) && prerequisites.NewlyCreated)
            || IsProjectedColumnMissing(intent.Table, intent.Schema, intent.Definition.Name)
            || IsProjectedColumnUnknown(intent.Table, intent.Schema, intent.Definition.Name)
            || (_projectedChangedColumns.TryGetValue(key, out var changed) && changed.Contains(intent.Definition.Name))
            || HasProjectedAlterForeignKeyDependency(intent)
            || (liveAnalysis.ObservedState != SafeMigrationObservedState.Matching
                && liveAnalysis is not
                {
                    ObservedState: SafeMigrationObservedState.Different,
                    RepairCapability: SafeMigrationRepairCapability.Safe,
                    RepairMutatesData: false,
                }))
        {
            return false;
        }

        if (TryGetProjectedColumnDefinition(intent.Table, intent.Schema, intent.Definition.Name, out var accepted))
        {
            // WHY: An authored old definition is not authority to replace an
            // accepted newer source, even if the immutable live analysis is safe.
            var expected = liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                ? intent.Definition
                : intent.OldDefinition;

            if (expected is null
                || !provider.AreProjectedColumnSourcesEquivalent(accepted, expected))
            {
                return false;
            }
        }

        return provider.CanReuseLiveColumnProofAfterIndexDrops(intent, liveAnalysis, this);
    }
}
