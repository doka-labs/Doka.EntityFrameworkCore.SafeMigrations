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
            if (_prerequisites.TryGetValue(
                    new TableKey(intent.Definition.Table, intent.Definition.Schema),
                    out var prerequisites)
                && prerequisites.PrimaryKey is not null)
            {
                var accepted = Analysis(
                    SafeMigrationDefinitionEquivalence.PrimaryKeySemantics(
                        prerequisites.PrimaryKey,
                        intent.Definition)
                        ? SafeMigrationObservedState.Matching
                        : SafeMigrationObservedState.Different);

                return ValidateProjectedPrimaryKey(intent, liveAnalysis, accepted);
            }

            if (prerequisites is { PrimaryKeyWasDropped: true, })
            {
                var removedDefinitionMatches = prerequisites.RemovedPrimaryKey is not null
                    && SafeMigrationDefinitionEquivalence.PrimaryKeySemantics(
                        prerequisites.RemovedPrimaryKey,
                        intent.Definition);

                var droppedAnalysis = removedDefinitionMatches
                    || liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                        ? Analysis(SafeMigrationObservedState.Missing)
                        : StructureStateUnknown();

                if (droppedAnalysis.ObservedState == SafeMigrationObservedState.Missing
                    && !TryGetConstraintPrerequisites(
                        intent.Definition.Table,
                        intent.Definition.Schema,
                        intent.Definition.Columns,
                        out _)
                    && !CanReuseMatchingLiveColumnPrerequisites(
                        intent.Definition.Table,
                        intent.Definition.Schema,
                        intent.Definition.Columns,
                        liveAnalysis))
                {
                    return StructureStateUnknown();
                }

                var droppedResult = InvalidateDataDependentMissing(
                    intent.Definition.Table,
                    intent.Definition.Schema,
                    droppedAnalysis);

                return ValidateProjectedPrimaryKey(intent, liveAnalysis, droppedResult);
            }

            var projectedAnalysis = CanProjectMissingPrimaryKey(intent, liveAnalysis)
                ? Analysis(SafeMigrationObservedState.Missing)
                : liveAnalysis;

            var result = InvalidateDataDependentMissing(
                intent.Definition.Table,
                intent.Definition.Schema,
                projectedAnalysis);

            return ValidateProjectedPrimaryKey(intent, liveAnalysis, result);
        }

        var analysis = AnalyzeOptional(
            table.PrimaryKey,
            intent.Definition,
            SafeMigrationDefinitionEquivalence.PrimaryKey);

        var tableAnalysis = InvalidateDataDependentMissing(table.Table, table.Schema, analysis);

        return ValidateProjectedPrimaryKey(intent, liveAnalysis, tableAnalysis);
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
        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp
            && _prerequisites.TryGetValue(
                new TableKey(intent.Definition.Table, intent.Definition.Schema),
                out var prerequisites))
        {
            prerequisites.AcceptPrimaryKey(intent.Definition);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.PrimaryKey = intent.Definition;
        }

        if (decision.Action == SafeMigrationAction.Apply)
        {
            _droppedPhysicalKeys.Remove(
                new IndexKey(intent.Definition.Table, intent.Definition.Schema, "PRIMARY"));
            _projectedCandidateKeyMutationTables.Add(
                new TableKey(intent.Definition.Table, intent.Definition.Schema));
        }
    }

    private void Observe(
        DropPrimaryKeyIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);

            prerequisites.DropPrimaryKey();
            _droppedPhysicalKeys.Add(new IndexKey(intent.Table, intent.Schema, "PRIMARY"));
            _projectedCandidateKeyMutationTables.Add(new TableKey(intent.Table, intent.Schema));
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.PrimaryKey = null;
        }
    }

    private SafeMigrationProviderAnalysis ValidateProjectedPrimaryKey(
        EnsurePrimaryKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        if (_projectedKeyAnalyzer is null
            || (projectedAnalysis.ObservedState != SafeMigrationObservedState.Missing
                && !HasProjectedKeyColumnChange(
                    intent.Definition.Table,
                    intent.Definition.Schema,
                    intent.Definition.Columns)))
        {
            return projectedAnalysis;
        }

        return _projectedKeyAnalyzer.ValidateProjectedPrimaryKey(
            intent,
            this,
            liveAnalysis,
            projectedAnalysis);
    }
}
