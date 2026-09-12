namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private SafeMigrationProviderAnalysis Project(
        EnsureIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        var indexKey = new IndexKey(
            intent.Definition.Table,
            intent.Definition.Schema,
            intent.Definition.Name);

        if (!HasProjectedIndexDefinition(intent.Definition)
            && IsProjectedIndexReplacement(indexKey, intent.Definition, liveAnalysis, out var earlyReplacement))
        {
            return ValidateProjectedIndex(intent, liveAnalysis, earlyReplacement);
        }

        if (IsProjectedTableStructureUnknown(intent.Definition.Table, intent.Definition.Schema))
        {
            // WHY: An earlier column or provider operation may have removed or
            // rewritten the accepted index. No historical semantic alias can
            // override that newer unknown-state boundary.
            return StructureStateUnknown();
        }

        if (TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            var analysis = AnalyzeDefinition(
                table.Indexes,
                intent.Definition.Name,
                intent.Definition,
                SafeMigrationDefinitionEquivalence.Index);

            if (analysis.ObservedState == SafeMigrationObservedState.Missing
                && table.Indexes.Values.Any(
                    index => SafeMigrationDefinitionEquivalence.IndexSemantics(index, intent.Definition)))
            {
                analysis = Analysis(SafeMigrationObservedState.Matching);
            }

            if (analysis.ObservedState == SafeMigrationObservedState.Missing
                && IsProjectedIndexReplacement(indexKey, intent.Definition, liveAnalysis, out var replacement))
            {
                return ValidateProjectedIndex(intent, liveAnalysis, replacement);
            }

            var tableAnalysis = intent.Definition.Unique
                ? InvalidateDataDependentMissing(table.Table, table.Schema, analysis)
                : analysis;

            var tableProjectedAnalysis = CanProjectMissingIndex(intent, tableAnalysis)
                ? Analysis(SafeMigrationObservedState.Missing)
                : tableAnalysis;

            return ValidateProjectedIndex(intent, liveAnalysis, tableProjectedAnalysis);
        }

        if (_prerequisites.TryGetValue(
                new TableKey(intent.Definition.Table, intent.Definition.Schema),
                out var prerequisites))
        {
            var accepted = AnalyzeAcceptedDefinition(
                prerequisites.Indexes,
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

                if (accepted.ObservedState == SafeMigrationObservedState.Missing
                    && IsProjectedIndexReplacement(indexKey, intent.Definition, liveAnalysis, out var replacement))
                {
                    accepted = replacement;
                }

                var projectedAccepted = intent.Definition.Unique
                    ? InvalidateDataDependentMissing(
                        intent.Definition.Table,
                        intent.Definition.Schema,
                        accepted)
                    : accepted;

                return ValidateProjectedIndex(intent, liveAnalysis, projectedAccepted);
            }
        }

        if (IsProjectedIndexReplacement(indexKey, intent.Definition, liveAnalysis, out var projectedReplacement))
        {
            return ValidateProjectedIndex(intent, liveAnalysis, projectedReplacement);
        }

        var projectedAnalysis = intent.Definition.Unique
            ? InvalidateDataDependentMissing(
                intent.Definition.Table,
                intent.Definition.Schema,
                liveAnalysis)
            : liveAnalysis;

        var result = CanProjectMissingIndex(intent, projectedAnalysis)
            ? Analysis(SafeMigrationObservedState.Missing)
            : projectedAnalysis;

        return ValidateProjectedIndex(intent, liveAnalysis, result);
    }

    private SafeMigrationProviderAnalysis Project(
        DropIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
        ? StructureStateUnknown()
        : TryGet(intent.Table, intent.Schema, out var table)
            ? Analysis(
                table.Indexes.ContainsKey(intent.Name)
                    ? SafeMigrationObservedState.Matching
                    : SafeMigrationObservedState.Missing)
            : liveAnalysis;

    private SafeMigrationProviderAnalysis Project(
        RenameIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => IsProjectedTableStructureUnknown(intent.Table, intent.Schema)
        ? StructureStateUnknown()
        : TryGet(intent.Table, intent.Schema, out var table)
            ? Analysis(
                !table.Indexes.ContainsKey(intent.Name)
                    ? SafeMigrationObservedState.Missing
                    : table.Indexes.ContainsKey(intent.NewName)
                        ? SafeMigrationObservedState.Different
                        : SafeMigrationObservedState.Matching)
            : liveAnalysis;

    private void Observe(
        EnsureIndexIntent intent,
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
                prerequisites.Indexes,
                intent.Definition.Name,
                intent.Definition,
                liveAnalysis,
                decision);
            ObserveSharedUniqueIndex(prerequisites, intent, liveAnalysis, decision);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.Indexes[intent.Definition.Name] = intent.Definition;
            ObserveSharedUniqueIndex(table, intent);
        }

        if (decision.Action == SafeMigrationAction.Apply)
        {
            _droppedPhysicalKeys.Remove(
                new IndexKey(intent.Definition.Table, intent.Definition.Schema, intent.Definition.Name));
        }
    }

    private void Observe(
        DropIndexIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action == SafeMigrationAction.Apply)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Table, intent.Schema);

            prerequisites.Indexes.MarkPhysicalMissing(intent.Name);
            if (SharesUniqueConstraintAndIndexIdentity)
            {
                _ = DropSharedUniqueIndex(prerequisites, intent.Name);
                _projectedCandidateKeyMutationTables.Add(new TableKey(intent.Table, intent.Schema));
            }
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Table, intent.Schema, out var table))
        {
            table.Indexes.Remove(intent.Name);
            if (SharesUniqueConstraintAndIndexIdentity)
            {
                _ = DropSharedUniqueIndex(table, intent.Name);
                _projectedCandidateKeyMutationTables.Add(new TableKey(intent.Table, intent.Schema));
            }
        }

        if (decision.Action == SafeMigrationAction.Apply)
        {
            _droppedPhysicalKeys.Add(new IndexKey(intent.Table, intent.Schema, intent.Name));
        }
    }

    private void Observe(
        RenameIndexIntent intent,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action != SafeMigrationAction.Apply)
        {
            return;
        }

        var definitionResolved = false;
        if (_prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites)
            && prerequisites.Indexes.RemovePhysical(intent.Name, out var index))
        {
            var renamed = CopyIndex(index, intent.NewName);

            prerequisites.Indexes.AcceptPhysical(intent.NewName, renamed);
            RenameSharedUniqueIndex(prerequisites, intent.Name, intent.NewName, renamed);
            definitionResolved = true;
        }

        if (TryGet(intent.Table, intent.Schema, out var table))
        {
            table.Indexes.TryGetValue(intent.Name, out var tableIndex);
            definitionResolved |= tableIndex is not null;
            table.RenameIndex(intent.Name, intent.NewName);

            if (tableIndex is not null)
            {
                RenameSharedUniqueIndex(
                    table,
                    intent.Name,
                    intent.NewName,
                    CopyIndex(tableIndex, intent.NewName));
            }
        }

        if (definitionResolved)
        {
            return;
        }

        // WHY: A successful rename proves that the source disappeared, but
        // not the target definition when no physical identity was captured.
        // Later safe operations must not reuse either stale side of the
        // immutable pre-batch catalog observation.
        GetOrCreateProviderPrerequisites(intent.Table, intent.Schema)
            .Indexes
            .MarkPhysicalMissing(intent.Name);
        SetProjectedTableStructureUnknown(intent.Table, intent.Schema);
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

    private bool CanProjectMissingIndex(
        EnsureIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (liveAnalysis.ObservedState != SafeMigrationObservedState.PrerequisiteMissing
            || !_prerequisites.TryGetValue(
                new TableKey(intent.Definition.Table, intent.Definition.Schema),
                out var prerequisites))
        {
            return false;
        }

        var requiredColumns = SafeMigrationPrerequisiteColumns.Local(intent);
        if (requiredColumns.Any(column => !prerequisites.Columns.ContainsKey(column)))
        {
            return false;
        }

        if (!intent.Definition.Unique)
        {
            return true;
        }

        if (prerequisites.DataMutationVersion < _providerDataMutationVersion)
        {
            return false;
        }

        if (prerequisites.NewlyCreated)
        {
            return !_projectedDataMutationTables.Contains(
                    new TableKey(intent.Definition.Table, intent.Definition.Schema))
                || HasProjectedModelManagedUniqueKey(intent.Definition);
        }

        if (intent.Definition.NullsDistinct == false)
        {
            return false;
        }

        // A unique index over pre-existing rows is safe only when an earlier
        // operation added a nullable, non-computed key without a non-null
        // default. Every old row then receives NULL, so existing rows cannot
        // collide while providers retain their ordinary NULL-distinct rules.
        return intent.Definition.Keys
            .Where(static key => key.Column is not null)
            .Select(key => prerequisites.Columns[key.Column!])
            .Any(static column => column is
            {
                AddedToExistingTable: true,
                IsNullable: true,
                PreservesNullForExistingRows: true,
                IsComputed: false,
            });
    }

    private bool IsProjectedIndexReplacement(
        IndexKey indexKey,
        ExpectedIndexDefinition definition,
        SafeMigrationProviderAnalysis liveAnalysis,
        [NotNullWhen(true)] out SafeMigrationProviderAnalysis? analysis
    )
    {
        analysis = null;

        if (!_droppedPhysicalKeys.Contains(indexKey)
            || !CanProjectProviderNeutralIndexReplacement(definition)
            || liveAnalysis.ObservedState is not (
                SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.Matching
                or SafeMigrationObservedState.Different))
        {
            return false;
        }

        analysis = StringComparer.Ordinal.Equals(
            liveAnalysis.Code,
            "index_replacement_data_blocked")
                ? new SafeMigrationProviderAnalysis(
                    SafeMigrationObservedState.DataBlocked,
                    SafeMigrationRepairCapability.None,
                    postconditionSatisfied: false,
                    liveAnalysis.Code)
                : Analysis(SafeMigrationObservedState.Missing);

        return true;
    }

    private SafeMigrationProviderAnalysis ValidateProjectedIndex(
        EnsureIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        if (_projectedKeyAnalyzer is null
            || (projectedAnalysis.ObservedState != SafeMigrationObservedState.Missing
                && !HasProjectedIndexColumnChange(intent.Definition)))
        {
            return projectedAnalysis;
        }

        return _projectedKeyAnalyzer.ValidateProjectedIndex(
            intent,
            this,
            liveAnalysis,
            projectedAnalysis);
    }

    private bool HasProjectedIndexColumnChange(
        ExpectedIndexDefinition definition
    )
    {
        var table = new TableKey(definition.Table, definition.Schema);
        if (_prerequisites.TryGetValue(table, out var prerequisites)
            && prerequisites.NewlyCreated)
        {
            return true;
        }

        if (!_projectedChangedColumns.TryGetValue(table, out var changedColumns))
        {
            return false;
        }

        return definition.Keys.Any(key => key.Column is not null && changedColumns.Contains(key.Column));
    }

    private bool HasProjectedIndexDefinition(
        ExpectedIndexDefinition definition
    )
    {
        var tableKey = new TableKey(definition.Table, definition.Schema);

        return _tables.TryGetValue(tableKey, out var table)
                && table.Indexes.Values.Any(
                    index => SafeMigrationDefinitionEquivalence.IndexSemantics(index, definition))
            || _prerequisites.TryGetValue(tableKey, out var prerequisites)
                && prerequisites.Indexes.ContainsSemantically(definition);
    }

    private static bool PreservesNullForExistingRows(
        SafeMigrationDefaultValue defaultValue
    ) => defaultValue.Kind == SafeMigrationDefaultValueKind.None
        || defaultValue.IsNullLiteral
        || defaultValue is
        {
            Kind: SafeMigrationDefaultValueKind.Sql,
            StructuredExpression: SafeMigrationSqlLiteralExpression { Value: null, },
        };

    private static bool CanProjectProviderNeutralIndexReplacement(
        ExpectedIndexDefinition definition
    ) => definition.Keys.All(static key => key.Column is not null)
        && (definition.Method is null
            || StringComparer.OrdinalIgnoreCase.Equals(definition.Method, "BTREE"));

    private static ExpectedIndexDefinition CopyIndex(
        ExpectedIndexDefinition definition,
        string name
    ) => new(
        name,
        definition.Table,
        definition.Keys,
        definition.Schema,
        definition.Unique,
        definition.Filter,
        definition.IncludedColumns,
        definition.Method,
        definition.NullsDistinct,
        definition.StructuredFilter);
}
