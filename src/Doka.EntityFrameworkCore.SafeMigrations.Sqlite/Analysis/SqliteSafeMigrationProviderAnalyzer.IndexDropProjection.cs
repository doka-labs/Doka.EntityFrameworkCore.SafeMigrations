namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    /// <inheritdoc />
    public bool PreservesColumnSourcesAfterIndexDrop(
        DropIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        // WHY: Native standalone DROP INDEX changes neither stored columns nor
        // their values. Constraint-backed automatic indexes are not removable
        // through this contract and cannot certify surviving column evidence.
        if (intent.Schema is not null || liveAnalysis.ObservedState != SafeMigrationObservedState.Matching
            || _dropProjectionSnapshot is not { } snapshot
            || !snapshot.Tables.TryGetValue(intent.Table, out var table))
        {
            return false;
        }

        foreach (var index in table.Indexes)
        {
            if (s_identifierComparer.Equals(index.Name, intent.Name))
            {
                return index.Origin == "c";
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool CanReuseLiveColumnProofAfterIndexDrops(
        AlterColumnIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        // WHY: SQLite maps int and long to the same physical INTEGER domain.
        // Preserve only a complete native target match here; existing rebuild
        // qualification still owns same-shape repairs and all artifact guards.
        return intent.Schema is null
            && liveAnalysis is { ObservedState: SafeMigrationObservedState.Matching, PostconditionSatisfied: true }
            && _dropProjectionSnapshot is { } snapshot
            && snapshot.Tables.TryGetValue(intent.Table, out var table)
            && table.Columns.TryGetValue(intent.Definition.Name, out var source)
            && ColumnMatches(source, intent.Definition);
    }
}
