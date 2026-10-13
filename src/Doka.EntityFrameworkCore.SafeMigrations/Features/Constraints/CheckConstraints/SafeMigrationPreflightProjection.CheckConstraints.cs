namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureCheckConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (IsProjectedTableStructureUnknown(intent.Definition.Table, intent.Definition.Schema))
        {
            return StructureStateUnknown();
        }

        if (!TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            if (_prerequisites.TryGetValue(
                    new TableKey(intent.Definition.Table, intent.Definition.Schema),
                    out var prerequisites))
            {
                var accepted = AnalyzeAcceptedDefinition(
                    prerequisites.CheckConstraints,
                    intent.Definition.Name,
                    intent.Definition,
                    liveAnalysis);

                if (accepted is not null)
                {
                    if (accepted.ObservedState == SafeMigrationObservedState.Missing
                        && !TryGetConstraintPrerequisites(
                            intent.Definition.Table,
                            intent.Definition.Schema,
                            SafeMigrationPrerequisiteColumns.Local(intent),
                            out _)
                        && !CanReuseMatchingLiveColumnPrerequisites(
                            intent.Definition.Table,
                            intent.Definition.Schema,
                            SafeMigrationPrerequisiteColumns.Local(intent),
                            liveAnalysis))
                    {
                        return StructureStateUnknown();
                    }

                    return InvalidateDataDependentMissing(
                        intent.Definition.Table,
                        intent.Definition.Schema,
                        accepted);
                }
            }

            var projectedAnalysis = CanProjectMissingCheckConstraint(intent, liveAnalysis)
                ? Analysis(SafeMigrationObservedState.Missing)
                : liveAnalysis;

            return InvalidateDataDependentMissing(
                intent.Definition.Table,
                intent.Definition.Schema,
                projectedAnalysis);
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
    ) => IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
        ? StructureStateUnknown()
        : TryGet(intent.Table, intent.Schema, out var table)
            ? Analysis(
                table.CheckConstraints.ContainsKey(intent.Name)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Missing)
            : _prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites)
                && prerequisites.CheckConstraints.TryGetPhysicalState(intent.Name, out var present)
                    ? Analysis(present ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing)
                    : liveAnalysis;

    private void Observe(
        EnsureCheckConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp)
        {
            // WHY: A standalone accepted CHECK proves its owner exists even
            // when no earlier table or column operation created a sparse view.
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Definition.Table, intent.Definition.Schema);

            ObserveAcceptedDefinition(
                prerequisites.CheckConstraints,
                intent.Definition.Name,
                intent.Definition,
                liveAnalysis,
                decision);
        }

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
        if (decision.Action == SafeMigrationAction.Apply)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);

            prerequisites.CheckConstraints.MarkPhysicalMissing(intent.Name, CurrentOperationOrdinal);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.CheckConstraints.Remove(intent.Name);
        }
    }
}
