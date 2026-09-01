namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureCheckConstraintIntent intent,
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
            table.CheckConstraints,
            intent.Definition.Name,
            intent.Definition,
            SafeMigrationDefinitionEquivalence.CheckConstraint);

        return InvalidateDataDependentMissing(table.Table, table.Schema, analysis);
    }

    private SafeMigrationProviderAnalysis Project(
        DropCheckConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => TryGet(intent.Table, intent.Schema, out var table)
        ? Analysis(
            table.CheckConstraints.ContainsKey(intent.Name)
                ? SafeMigrationObservedState.Matching
                : SafeMigrationObservedState.Missing)
        : liveAnalysis;

    private void Observe(
        EnsureCheckConstraintIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.CheckConstraints[intent.Definition.Name] = intent.Definition;
        }
    }

    private void Observe(
        DropCheckConstraintIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.CheckConstraints.Remove(intent.Name);
        }
    }
}
