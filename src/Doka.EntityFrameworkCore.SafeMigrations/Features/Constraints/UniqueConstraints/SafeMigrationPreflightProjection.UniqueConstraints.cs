namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureUniqueConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (!TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            return InvalidateDataDependentMissing(
                intent.Definition.Table,
                intent.Definition.Schema,
                liveAnalysis);
        }

        var analysis = AnalyzeDefinition(
            table.UniqueConstraints,
            intent.Definition.Name,
            intent.Definition,
            SafeMigrationDefinitionEquivalence.UniqueConstraint);

        return InvalidateDataDependentMissing(table.Table, table.Schema, analysis);
    }

    private SafeMigrationProviderAnalysis Project(
        DropUniqueConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table)
        ? Analysis(
            table.UniqueConstraints.ContainsKey(intent.Name)
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Missing)
        : liveAnalysis;

    private void Observe(
        EnsureUniqueConstraintIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.UniqueConstraints[intent.Definition.Name] = intent.Definition;
        }
    }

    private void Observe(
        DropUniqueConstraintIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.UniqueConstraints.Remove(intent.Name);
        }
    }
}
