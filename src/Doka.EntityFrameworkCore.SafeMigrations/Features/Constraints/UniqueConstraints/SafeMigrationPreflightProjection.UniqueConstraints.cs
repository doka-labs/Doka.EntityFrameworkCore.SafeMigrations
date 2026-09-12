namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureUniqueConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        var key = new IndexKey(
            intent.Definition.Table,
            intent.Definition.Schema,
            intent.Definition.Name);

        if (IsProjectedUniqueConstraintReplacement(key, liveAnalysis, out var earlyReplacement))
        {
            var replacement = InvalidateDataDependentMissing(
                intent.Definition.Table,
                intent.Definition.Schema,
                earlyReplacement);

            return ValidateProjectedUniqueConstraint(intent, liveAnalysis, replacement);
        }

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
                    prerequisites.UniqueConstraints,
                    intent.Definition.Name,
                    intent.Definition,
                    liveAnalysis);

                if (accepted is not null)
                {
                    if (accepted.ObservedState == SafeMigrationObservedState.Missing
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

                    var projectedAccepted = InvalidateDataDependentMissing(
                        intent.Definition.Table,
                        intent.Definition.Schema,
                        accepted);

                    return ValidateProjectedUniqueConstraint(intent, liveAnalysis, projectedAccepted);
                }
            }

            var projectedAnalysis = CanProjectMissingUniqueConstraint(intent, liveAnalysis)
                ? Analysis(SafeMigrationObservedState.Missing)
                : liveAnalysis;

            var result = InvalidateDataDependentMissing(
                intent.Definition.Table,
                intent.Definition.Schema,
                projectedAnalysis);

            return ValidateProjectedUniqueConstraint(intent, liveAnalysis, result);
        }

        var analysis = AnalyzeDefinition(
            table.UniqueConstraints,
            intent.Definition.Name,
            intent.Definition,
            SafeMigrationDefinitionEquivalence.UniqueConstraint);

        var tableAnalysis = InvalidateDataDependentMissing(table.Table, table.Schema, analysis);

        return ValidateProjectedUniqueConstraint(intent, liveAnalysis, tableAnalysis);
    }

    private SafeMigrationProviderAnalysis Project(
        DropUniqueConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
        ? StructureStateUnknown()
        : TryGet(intent.Table, intent.Schema, out var table)
            ? Analysis(
                table.UniqueConstraints.ContainsKey(intent.Name)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Missing)
            : liveAnalysis;

    private void Observe(
        EnsureUniqueConstraintIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp
            && _prerequisites.TryGetValue(
                new TableKey(intent.Definition.Table, intent.Definition.Schema),
                out var prerequisites))
        {
            ObserveAcceptedDefinition(
                prerequisites.UniqueConstraints,
                intent.Definition.Name,
                intent.Definition,
                liveAnalysis,
                decision);
            ObserveSharedUniqueConstraint(prerequisites, intent, liveAnalysis, decision);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.UniqueConstraints[intent.Definition.Name] = intent.Definition;
            ObserveSharedUniqueConstraint(table, intent);
        }

        if (decision.Action == SafeMigrationAction.Apply)
        {
            _droppedPhysicalKeys.Remove(
                new IndexKey(intent.Definition.Table, intent.Definition.Schema, intent.Definition.Name));
            _projectedCandidateKeyMutationTables.Add(
                new TableKey(intent.Definition.Table, intent.Definition.Schema));
        }
    }

    private void Observe(
        DropUniqueConstraintIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);

            prerequisites.UniqueConstraints.MarkPhysicalMissing(intent.Name);
            DropSharedUniqueConstraint(prerequisites, intent.Name);
            _droppedPhysicalKeys.Add(new IndexKey(intent.Table, intent.Schema, intent.Name));
            _projectedCandidateKeyMutationTables.Add(new TableKey(intent.Table, intent.Schema));
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.UniqueConstraints.Remove(intent.Name);
            DropSharedUniqueConstraint(table, intent.Name);
        }
    }

    private bool IsProjectedUniqueConstraintReplacement(
        IndexKey key,
        SafeMigrationProviderAnalysis liveAnalysis,
        [NotNullWhen(true)] out SafeMigrationProviderAnalysis? analysis
    )
    {
        analysis = null;

        if (!_droppedPhysicalKeys.Contains(key)
            || liveAnalysis.ObservedState is not (
                SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.Matching
                or SafeMigrationObservedState.Different))
        {
            return false;
        }

        analysis = StringComparer.Ordinal.Equals(
            liveAnalysis.Code,
            "unique_constraint_replacement_data_blocked")
                ? new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.DataBlocked,
                    SafeMigrationRepairCapability.None,
                    postconditionSatisfied: false,
                    liveAnalysis.Code)
                : Analysis(SafeMigrationObservedState.Missing);

        return true;
    }

    private SafeMigrationProviderAnalysis ValidateProjectedUniqueConstraint(
        EnsureUniqueConstraintIntent intent,
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

        return _projectedKeyAnalyzer.ValidateProjectedUniqueConstraint(
            intent,
            this,
            liveAnalysis,
            projectedAnalysis);
    }
}
