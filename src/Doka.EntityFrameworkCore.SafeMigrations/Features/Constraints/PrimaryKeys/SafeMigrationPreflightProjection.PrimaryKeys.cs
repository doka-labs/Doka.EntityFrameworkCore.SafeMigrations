namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsurePrimaryKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (IsProjectedTableStructureUnknown(intent.Definition.Table, intent.Definition.Schema))
        {
            return StructureStateUnknown();
        }

        if (!TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            return InvalidateDataDependentMissing(
                intent.Definition.Table,
                intent.Definition.Schema,
                liveAnalysis);
        }

        var analysis = AnalyzeOptional(
            table.PrimaryKey,
            intent.Definition,
            SafeMigrationDefinitionEquivalence.PrimaryKey);

        return InvalidateDataDependentMissing(table.Table, table.Schema, analysis);
    }

    private SafeMigrationProviderAnalysis Project(
        DropPrimaryKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
        ? StructureStateUnknown()
        : TryGet(intent.Table, intent.Schema, out var table)
            ? Analysis(
                table.PrimaryKey is null
                    ? SafeMigrationObservedState.Missing
                    : SafeMigrationObservedState.Matching)
            : liveAnalysis;

    private void Observe(
        EnsurePrimaryKeyIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.PrimaryKey = intent.Definition;
        }
    }

    private void Observe(
        DropPrimaryKeyIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.PrimaryKey = null;
        }
    }
}
