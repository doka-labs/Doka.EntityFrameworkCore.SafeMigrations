namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Definition.Table, intent.Definition.Schema, out var table)
        ? AnalyzeDefinition(
            table.ForeignKeys,
            intent.Definition.Name,
            intent.Definition,
            SafeMigrationDefinitionEquivalence.ForeignKey)
        : liveAnalysis;

    private SafeMigrationProviderAnalysis Project(
        DropForeignKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table)
        ? Analysis(
            table.ForeignKeys.ContainsKey(intent.Name)
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Missing)
        : liveAnalysis;

    private void Observe(
        EnsureForeignKeyIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.ForeignKeys[intent.Definition.Name] = intent.Definition;
        }
    }

    private void Observe(
        DropForeignKeyIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.ForeignKeys.Remove(intent.Name);
        }
    }
}
