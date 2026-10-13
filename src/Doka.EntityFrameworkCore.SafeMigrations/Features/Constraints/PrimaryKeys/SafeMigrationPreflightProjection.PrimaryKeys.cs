namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private bool PrimaryKeyUsesNameIndependentIdentity =>
        _projectedKeyAnalyzer?.PrimaryKeyUsesNameIndependentIdentity == true;

    bool? ISafeMigrationProjectedColumnSource.IsProjectedPrimaryKeyColumn(
        string table,
        string? schema,
        string column,
        IEqualityComparer<string> columnComparer
    )
    {
        var key = new TableKey(table, schema);
        if (_projectedMissingTables.Contains(key))
        {
            return false;
        }

        if (_prerequisites.TryGetValue(key, out var prerequisites))
        {
            if (prerequisites.PrimaryKey is not null)
            {
                return PrimaryKeyHasColumn(prerequisites.PrimaryKey, column, columnComparer);
            }

            if (prerequisites.PrimaryKeyWasDropped)
            {
                return false;
            }
        }

        if (_tables.TryGetValue(key, out var projected))
        {
            return PrimaryKeyHasColumn(projected.PrimaryKey, column, columnComparer);
        }

        var canonical = ResolveAlterTable(key);
        foreach (var pair in _tables)
        {
            if (_tables.Comparer.Equals(ResolveAlterTable(pair.Key), canonical))
            {
                return PrimaryKeyHasColumn(pair.Value.PrimaryKey, column, columnComparer);
            }
        }

        foreach (var pair in _prerequisites)
        {
            if (_prerequisites.Comparer.Equals(ResolveAlterTable(pair.Key), canonical))
            {
                if (pair.Value.PrimaryKey is not null)
                {
                    return PrimaryKeyHasColumn(pair.Value.PrimaryKey, column, columnComparer);
                }

                if (pair.Value.PrimaryKeyWasDropped)
                {
                    return false;
                }
            }
        }

        return null;
    }

    /// <summary>Reads accepted PK membership without allocating a column enumeration.</summary>
    /// <param name="definition">The current accepted primary key, when present.</param>
    /// <param name="column">The requested column member.</param>
    /// <param name="comparer">The provider's column-only comparison rule.</param>
    private static bool PrimaryKeyHasColumn(
        ExpectedPrimaryKeyDefinition? definition,
        string column,
        IEqualityComparer<string> comparer
    )
    {
        if (definition is null)
        {
            return false;
        }

        for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
        {
            if (comparer.Equals(definition.Columns[ordinal], column))
            {
                return true;
            }
        }

        return false;
    }

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
                if (liveAnalysis.ObservedState is SafeMigrationObservedState.Unsupported
                    or SafeMigrationObservedState.DataBlocked)
                {
                    return liveAnalysis;
                }

                var removedDefinitionMatches = prerequisites.RemovedPrimaryKey is not null
                    && SafeMigrationDefinitionEquivalence.PrimaryKeySemantics(
                        prerequisites.RemovedPrimaryKey,
                        intent.Definition);

                var droppedAnalysis = removedDefinitionMatches
                    || liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                    || HasCurrentEmptyPrimaryKeyPrerequisites(intent)
                        ? Analysis(SafeMigrationObservedState.Missing)
                        : _projectedKeyAnalyzer is null
                            ? StructureStateUnknown()
                            : new SafeMigrationProviderAnalysis(
                                SafeMigrationObservedState.PrerequisiteMissing,
                                SafeMigrationRepairCapability.None,
                                postconditionSatisfied: false,
                                "projected_primary_key_replacement_unproven");

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

                // WHY: A provider may hold an exact live row/column proof for
                // a different replacement key even when Core never accepted
                // the removed definition. Unknown structure is not weakened.
                return ValidateProjectedPrimaryKey(intent, liveAnalysis, droppedResult, validateReplacement: true);
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

    /// <summary>Checks independent current empty-table proof and accepted nonnullable replacement columns.</summary>
    /// <param name="intent">The requested replacement primary key.</param>
    private bool HasCurrentEmptyPrimaryKeyPrerequisites(
        EnsurePrimaryKeyIntent intent
    ) => TryGetConstraintPrerequisites(
            intent.Definition.Table,
            intent.Definition.Schema,
            intent.Definition.Columns,
            out var prerequisites)
        && intent.Definition.Columns.All(column => TryGetProjectedColumnDefinition(
                intent.Definition.Table,
                intent.Definition.Schema,
                column,
                out var definition)
            && !definition.IsNullable)
        && ProvesTableEmpty(intent.Definition.Table, intent.Definition.Schema, prerequisites);

    private SafeMigrationProviderAnalysis Project(
        DropPrimaryKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (IsProjectedTableStructureUnknown(intent.Table, intent.Schema))
        {
            return StructureStateUnknown();
        }

        if (_prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites))
        {
            if (prerequisites.PrimaryKey is not null)
            {
                return AnalyzeProjectedPrimaryKeyDrop(intent, prerequisites.PrimaryKeyPhysicalName, liveAnalysis);
            }

            if (prerequisites.PrimaryKeyWasDropped)
            {
                return PreserveNamedPrimaryKeyDropConflict(liveAnalysis);
            }
        }

        if (TryGet(intent.Table, intent.Schema, out var table))
        {
            return table.PrimaryKey is null
                ? PreserveNamedPrimaryKeyDropConflict(liveAnalysis)
                : AnalyzeProjectedPrimaryKeyDrop(intent, table.PrimaryKey.Name, liveAnalysis);
        }

        return liveAnalysis;
    }

    /// <summary>Requires the physical name unless the provider explicitly certifies singleton PK identity.</summary>
    /// <param name="intent">The requested physical removal.</param>
    /// <param name="physicalName">Accepted physical-name evidence, not a semantic alias.</param>
    /// <param name="liveAnalysis">The immutable provider's exact-name or wrong-kind result.</param>
    private SafeMigrationProviderAnalysis AnalyzeProjectedPrimaryKeyDrop(
        DropPrimaryKeyIntent intent,
        string? physicalName,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported)
        {
            return liveAnalysis;
        }

        if (PrimaryKeyUsesNameIndependentIdentity)
        {
            return Analysis(SafeMigrationObservedState.Matching);
        }

        if (physicalName is null)
        {
            // WHY: A semantic Match can describe an anonymous or differently
            // named PK. Its authored logical name proves no physical drop target.

            return liveAnalysis;
        }

        return IdentifierEquals(_objectIdentityNormalizer, physicalName, intent.Name)
            ? Analysis(SafeMigrationObservedState.Matching)
            : PreserveNamedPrimaryKeyDropConflict(liveAnalysis);
    }

    /// <summary>Retains native wrong-name or wrong-kind rejection without fabricating another physical PK.</summary>
    /// <param name="liveAnalysis">The immutable named-drop result.</param>
    private SafeMigrationProviderAnalysis PreserveNamedPrimaryKeyDropConflict(
        SafeMigrationProviderAnalysis liveAnalysis
    ) => liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported
        || (!PrimaryKeyUsesNameIndependentIdentity
            && liveAnalysis.ObservedState == SafeMigrationObservedState.Different)
        ? liveAnalysis
        : Analysis(SafeMigrationObservedState.Missing);

    /// <summary>Captures physical PK identity only from actual creation or retained exact accepted evidence.</summary>
    /// <param name="definition">The accepted table-contained primary-key shape.</param>
    /// <param name="newlyCreated">Whether this operation creates the physical table and constraint.</param>
    private string? GetCapturedPrimaryKeyPhysicalName(ExpectedPrimaryKeyDefinition definition, bool newlyCreated)
    {
        if (PrimaryKeyUsesNameIndependentIdentity)
        {
            return "PRIMARY";
        }

        if (newlyCreated)
        {
            return definition.Name;
        }

        if (_prerequisites.TryGetValue(new TableKey(definition.Table, definition.Schema), out var previous)
            && previous.PrimaryKey is not null
            && SafeMigrationDefinitionEquivalence.PrimaryKeySemantics(previous.PrimaryKey, definition))
        {
            return previous.PrimaryKeyPhysicalName;
        }

        return TryGet(definition.Table, definition.Schema, out var table)
            && table.PrimaryKey is not null
            && SafeMigrationDefinitionEquivalence.PrimaryKeySemantics(table.PrimaryKey, definition)
                ? table.PrimaryKey.Name
                : null;
    }

    private void Observe(
        EnsurePrimaryKeyIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Definition.Table, intent.Definition.Schema);

            var physicalName = PrimaryKeyUsesNameIndependentIdentity
                ? "PRIMARY"
                : decision.Action == SafeMigrationAction.Apply
                    ? intent.Definition.Name
                    : prerequisites.PrimaryKeyPhysicalName
                        ?? (analysis.ObservedState == SafeMigrationObservedState.Matching
                            ? analysis.MatchedObjectName : null)
                        ?? (liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                            ? liveAnalysis.MatchedObjectName : null);

            prerequisites.AcceptPrimaryKey(intent.Definition, physicalName);
        }

        if (decision.Action == SafeMigrationAction.Apply
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            table.PrimaryKey = intent.Definition;
        }

        if (decision.Action == SafeMigrationAction.Apply)
        {
            _droppedPhysicalKeys.Remove(
                new IndexKey(intent.Definition.Table, intent.Definition.Schema,
                    PrimaryKeyUsesNameIndependentIdentity ? "PRIMARY" : intent.Definition.Name));
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
            var physicalName = PrimaryKeyUsesNameIndependentIdentity ? "PRIMARY" : intent.Name;

            prerequisites.DropPrimaryKey();
            _droppedPhysicalKeys.Add(new IndexKey(intent.Table, intent.Schema, physicalName));
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
        SafeMigrationProviderAnalysis projectedAnalysis,
        bool validateReplacement = false
    )
    {
        if (_projectedKeyAnalyzer is null
            || (!validateReplacement
                && projectedAnalysis.ObservedState != SafeMigrationObservedState.Missing
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
            SelectProjectedKeyEnvironment(intent.Definition.Table, intent.Definition.Schema, liveAnalysis),
            projectedAnalysis);
    }
}
