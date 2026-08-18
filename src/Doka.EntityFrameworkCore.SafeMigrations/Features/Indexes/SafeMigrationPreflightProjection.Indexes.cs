namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Definition.Table, intent.Definition.Schema, out var table)
        ? AnalyzeDefinition(
            table.Indexes,
            intent.Definition.Name,
            intent.Definition,
            SafeMigrationDefinitionEquivalence.Index)
        : liveAnalysis;

    private SafeMigrationProviderAnalysis Project(
        DropIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table)
        ? Analysis(
            table.Indexes.ContainsKey(intent.Name)
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Missing)
        : liveAnalysis;

    private SafeMigrationProviderAnalysis Project(
        RenameIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table)
        ? Analysis(
            !table.Indexes.ContainsKey(intent.Name) ? SafeMigrationObservedState.Missing :
            table.Indexes.ContainsKey(intent.NewName) ? SafeMigrationObservedState.Different :
            SafeMigrationObservedState.Matching)
        : liveAnalysis;

    private void Observe(
        EnsureIndexIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.Indexes[intent.Definition.Name] = intent.Definition;
        }
    }

    private void Observe(
        DropIndexIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.Indexes.Remove(intent.Name);
        }
    }

    private void Observe(
        RenameIndexIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.RenameIndex(intent.Name, intent.NewName);
        }
    }

    private sealed partial class ProjectedTable
    {
        public void RenameIndex(
            string source,
            string target
        )
        {
            if (Indexes.Remove(source, out var index))
            {
                Indexes[target] = Copy(index, name: target);
            }
        }
    }
}
