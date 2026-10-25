namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    SafeMigrationProviderAnalysis? ISafeMigrationProjectedColumnSource.GetProjectedIndexIdentityAnalysis(
        ExpectedIndexDefinition definition,
        SafeMigrationProviderAnalysis liveAnalysis,
        bool includeConstraintBackingIndexNames
    )
    {
        var tableKey = new TableKey(definition.Table, definition.Schema);
        if (includeConstraintBackingIndexNames && HasAcceptedConstraintBackingIndexName(tableKey, definition.Name))
        {
            // WHY: On qualified providers, PK/UNIQUE symbols occupy the native
            // index namespace without becoming logical standalone indexes.
            // Reuse their existing physical bindings, not an independent map.
            return Analysis(SafeMigrationObservedState.Different);
        }

        if (_tables.TryGetValue(tableKey, out var table))
        {
            if (table.Indexes.TryGetValue(definition.Name, out var exact))
            {
                return Analysis(
                    ProjectedIndexSemantics(exact, definition)
                        ? SafeMigrationObservedState.Matching
                        : SafeMigrationObservedState.Different);
            }

            foreach (var candidate in table.Indexes.Values)
            {
                if (ProjectedIndexSemantics(candidate, definition))
                {
                    return Analysis(SafeMigrationObservedState.Matching);
                }
            }
        }

        var accepted = _prerequisites.TryGetValue(tableKey, out var prerequisites)
            ? AnalyzeAcceptedDefinition(prerequisites.Indexes, definition.Name, definition, liveAnalysis)
            : null;

        return accepted ?? (table is not null ? Analysis(SafeMigrationObservedState.Missing) : null);
    }

    /// <summary>Checks only native-qualified current constraint names, never unbound semantic aliases.</summary>
    private bool HasAcceptedConstraintBackingIndexName(
        TableKey owner,
        string name
    )
    {
        var comparer = _objectIdentityNormalizer?.IndexIdentifierComparer ?? StringComparer.Ordinal;
        if (_tables.TryGetValue(owner, out var table))
        {
            if (table.PrimaryKey is { } primary
                && comparer.Equals(primary.Name, name))
            {
                return true;
            }

            foreach (var constraint in table.UniqueConstraints.Values)
            {
                if (comparer.Equals(constraint.Name, name))
                {
                    return true;
                }
            }
        }

        return _prerequisites.TryGetValue(owner, out var prerequisites)
            && ((prerequisites.PrimaryKeyPhysicalName is { } physicalName && comparer.Equals(physicalName, name))
                || prerequisites.UniqueConstraints.TryGetPhysicalDefinition(name, out _));
    }

    private SafeMigrationProviderAnalysis Project(
        EnsureIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        var indexKey = new IndexKey(intent.Definition.Table, intent.Definition.Schema, intent.Definition.Name);

        // WHY: Provider-specific recognition must supply the same dependencies
        // to ordered projection and runtime guards, without rewriting the raw
        // definition or teaching Core a provider's SQL identifier grammar.
        var requiredColumns = _indexPrerequisiteSource?.GetIndexPrerequisiteColumns(intent)
            ?? SafeMigrationPrerequisiteColumns.Local(intent);

        if (!HasProjectedIndexDefinition(intent.Definition)
            && IsProjectedIndexReplacement(indexKey, intent.Definition, liveAnalysis, out var earlyReplacement))
        {
            return ValidateProjectedIndex(intent, liveAnalysis, earlyReplacement, requiredColumns);
        }

        if (IsProjectedTableStructureUnknown(intent.Definition.Table, intent.Definition.Schema))
        {
            if (CanValidateKeyAfterColumnDrop(intent.Definition.Table, intent.Definition.Schema)
                && !HasUnavailableKeyMember(intent.Definition.Table, intent.Definition.Schema, requiredColumns)
                && !RequiresIndependentIndexDependencyProof(intent.Definition, requiredColumns, liveAnalysis))
            {
                return _projectedKeyAnalyzer!.ValidateProjectedIndex(
                    intent,
                    this,
                    SelectProjectedKeyEnvironment(intent.Definition.Table, intent.Definition.Schema, liveAnalysis),
                    KeyAfterColumnDropUnproven());
            }

            // WHY: An earlier column or provider operation may have removed or
            // rewritten the accepted index. No historical semantic alias can
            // override that newer unknown-state boundary.
            return StructureStateUnknown();
        }

        if (TryGet(intent.Definition.Table, intent.Definition.Schema, out var table))
        {
            var analysis = table.Indexes.TryGetValue(intent.Definition.Name, out var exact)
                ? Analysis(
                    ProjectedIndexSemantics(exact, intent.Definition)
                        ? SafeMigrationObservedState.Matching
                        : SafeMigrationObservedState.Different)
                : Analysis(SafeMigrationObservedState.Missing);

            if (analysis.ObservedState == SafeMigrationObservedState.Missing)
            {
                foreach (var index in table.Indexes.Values)
                {
                    if (ProjectedIndexSemantics(index, intent.Definition))
                    {
                        analysis = Analysis(SafeMigrationObservedState.Matching);

                        break;
                    }
                }
            }

            if (analysis.ObservedState == SafeMigrationObservedState.Missing
                && IsProjectedIndexReplacement(indexKey, intent.Definition, liveAnalysis, out var replacement))
            {
                return ValidateProjectedIndex(intent, liveAnalysis, replacement, requiredColumns);
            }

            var tableAnalysis = intent.Definition.Unique
                ? InvalidateDataDependentMissing(table.Table, table.Schema, analysis)
                : analysis;

            var tableProjectedAnalysis = CanProjectMissingIndex(intent, tableAnalysis, requiredColumns)
                ? Analysis(SafeMigrationObservedState.Missing)
                : tableAnalysis;

            return ValidateProjectedIndex(intent, liveAnalysis, tableProjectedAnalysis, requiredColumns);
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
                        requiredColumns,
                        out _)
                    && !CanReuseMatchingLiveColumnPrerequisites(
                        intent.Definition.Table,
                        intent.Definition.Schema,
                        requiredColumns,
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
                    ? InvalidateDataDependentMissing(intent.Definition.Table, intent.Definition.Schema, accepted)
                    : accepted;

                return ValidateProjectedIndex(intent, liveAnalysis, projectedAccepted, requiredColumns);
            }
        }

        if (IsProjectedIndexReplacement(indexKey, intent.Definition, liveAnalysis, out var projectedReplacement))
        {
            return ValidateProjectedIndex(intent, liveAnalysis, projectedReplacement, requiredColumns);
        }

        var projectedAnalysis = intent.Definition.Unique
            ? InvalidateDataDependentMissing(intent.Definition.Table, intent.Definition.Schema, liveAnalysis)
            : liveAnalysis;

        var result = CanProjectMissingIndex(intent, projectedAnalysis, requiredColumns)
            ? Analysis(SafeMigrationObservedState.Missing)
            : projectedAnalysis;

        return ValidateProjectedIndex(intent, liveAnalysis, result, requiredColumns);
    }

    /// <summary>Keeps unresolved or generated member proofs ahead of generic DROP revalidation.</summary>
    private bool RequiresIndependentIndexDependencyProof(
        ExpectedIndexDefinition definition,
        IReadOnlyList<string> requiredColumns,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        foreach (var name in requiredColumns)
        {
            var hasProjected = TryGetProjectedColumnDefinition(
                definition.Table,
                definition.Schema,
                name,
                out var column);

            if (column is { ComputedExpression: not null } or { ComputedColumnSql: not null })
            {
                // WHY: The ordinary surviving-value certificate knows neither
                // generated dependencies nor their physical index-alias lifetime.
                return true;
            }

            if (!hasProjected
                && liveAnalysis.ObservedState == SafeMigrationObservedState.PrerequisiteMissing
                && liveAnalysis.IndexPhysicalEnvironment?.Columns?.ContainsKey(name) != true)
            {
                // WHY: A rejected generated ADD supplies no accepted member.
                // The generic key callback cannot replace that dependency failure.
                return true;
            }
        }

        return false;
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
            : _prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites)
            && prerequisites.Indexes.TryGetPhysicalState(intent.Name, out var present)
                ? Analysis(present ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing)
                : liveAnalysis;

    private SafeMigrationProviderAnalysis Project(
        RenameIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        if (IsProjectedTableStructureUnknown(intent.Table, intent.Schema))
        {
            return StructureStateUnknown();
        }

        // WHY: Semantic aliases are not additional physical rename endpoints.
        // Sparse physical states and tombstones take precedence over the
        // immutable catalog, without inferring absence from an untracked name.
        bool? sourceExists = null;
        bool? targetExists = null;
        var sourceCreated = false;
        var schemaScoped = _objectIdentityNormalizer?.IndexUsesSchemaScopedIdentity == true;

        if (TryGet(intent.Table, intent.Schema, out var table))
        {
            sourceExists = table.Indexes.ContainsKey(intent.Name);
            targetExists = table.Indexes.ContainsKey(intent.NewName)
                ? true
                : schemaScoped
                    ? null
                    : false;

            sourceCreated = sourceExists == true;
        }
        else if (_prerequisites.TryGetValue(new TableKey(intent.Table, intent.Schema), out var prerequisites))
        {
            sourceExists = prerequisites.Indexes.TryGetPhysicalState(intent.Name, out var sourcePresent)
                ? sourcePresent
                : null;
            targetExists = prerequisites.Indexes.TryGetPhysicalState(intent.NewName, out var targetPresent)
                ? targetPresent
                : null;
            sourceCreated = prerequisites.Indexes.WasCreatedByProjection(intent.Name);
        }

        if (sourceExists == false)
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        if (sourceExists is null
            && liveAnalysis.ObservedState == SafeMigrationObservedState.Missing)
        {
            return liveAnalysis;
        }

        if (liveAnalysis.RenameTargetSupported == false)
        {
            // WHY: Creating a new valid source cannot make a reserved or
            // otherwise unsupported authored destination contract valid.

            return Analysis(SafeMigrationObservedState.Different);
        }

        if (liveAnalysis.ObservedState == SafeMigrationObservedState.Unsupported)
        {
            return liveAnalysis;
        }

        if (!sourceCreated
            && liveAnalysis.RenameSourceSupported == false)
        {
            return liveAnalysis.ObservedState == SafeMigrationObservedState.Different
                ? liveAnalysis
                : Analysis(SafeMigrationObservedState.Different);
        }

        if (!sourceCreated
            && liveAnalysis.ObservedState == SafeMigrationObservedState.Different
            && liveAnalysis.RenameSourceSupported != true)
        {
            // WHY: Different can mean wrong source owner/kind, not only an
            // occupied destination. Dropping a target proves no source eligibility.

            return liveAnalysis;
        }

        if (schemaScoped)
        {
            targetExists = ProjectSchemaScopedIndexDestination(intent);
        }

        if (sourceCreated
            && liveAnalysis is
            {
                ObservedState: SafeMigrationObservedState.Different,
                RenameSourceSupported: false,
                RenameTargetExists: false
            }
            && targetExists != true)
        {
            // WHY: An accepted ordinary creation replaces an old wrong-kind
            // source. Independent destination absence plus current namespace
            // checks prove this new lifetime is eligible, not the old object.

            return Analysis(SafeMigrationObservedState.Matching);
        }

        return AnalyzeSparseMemberRename(sourceExists, targetExists, liveAnalysis);
    }

    private void Observe(
        EnsureIndexIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.NoOp)
        {
            var prerequisites = GetOrCreateProviderPrerequisites(intent.Definition.Table, intent.Definition.Schema);

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

            prerequisites.Indexes.MarkPhysicalMissing(intent.Name, CurrentOperationOrdinal);
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

            prerequisites.Indexes.AcceptPhysical(intent.NewName, renamed, createdByProjection: true);
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
                RenameSharedUniqueIndex(table, intent.Name, intent.NewName, CopyIndex(tableIndex, intent.NewName));
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
            .MarkPhysicalMissing(intent.Name, CurrentOperationOrdinal);

        SetProjectedTableStructureUnknown(intent.Table, intent.Schema, preservesGeneratedColumns: true);
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
        SafeMigrationProviderAnalysis liveAnalysis,
        IReadOnlyList<string> requiredColumns
    )
    {
        if (liveAnalysis.ObservedState != SafeMigrationObservedState.PrerequisiteMissing
            || !TryGetConstraintPrerequisites(
                intent.Definition.Table,
                intent.Definition.Schema,
                requiredColumns,
                out var prerequisites))
        {
            return false;
        }

        if (!intent.Definition.Unique)
        {
            return true;
        }

        if (prerequisites.NewlyCreated)
        {
            return ProvesTableEmpty(intent.Definition.Table, intent.Definition.Schema, prerequisites)
                || (prerequisites.ModelManagedDataMutationVersion == _providerDataMutationVersion
                    && HasProjectedModelManagedUniqueKey(intent.Definition));
        }

        if (prerequisites.DataMutationVersion < _providerDataMutationVersion)
        {
            return false;
        }

        if (intent.Definition.NullsDistinct == false)
        {
            return false;
        }

        // A unique index over pre-existing rows is safe only when an earlier
        // operation added a nullable, non-computed key without a non-null
        // default. Every old row then receives NULL, so existing rows cannot
        // collide while providers retain their ordinary NULL-distinct rules.
        foreach (var key in intent.Definition.Keys)
        {
            if (key.Column is { } column
                && prerequisites.Columns.TryGetValue(column, out var projected)
                && projected is
                {
                    AddedToExistingTable: true,
                    IsNullable: true,
                    PreservesNullForExistingRows: true,
                    IsComputed: false,
                })
            {
                return true;
            }
        }

        return false;
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
            || liveAnalysis.ObservedState is not (SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.Matching
                or SafeMigrationObservedState.Different))
        {
            return false;
        }

        analysis = StringComparer.Ordinal.Equals(liveAnalysis.Code, "index_replacement_data_blocked")
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
        SafeMigrationProviderAnalysis projectedAnalysis,
        IReadOnlyList<string> requiredColumns
    )
    {
        if (projectedAnalysis.ObservedState == SafeMigrationObservedState.Missing
            && TryGet(intent.Definition.Table, intent.Definition.Schema, out _)
            && !TryGetConstraintPrerequisites(
                intent.Definition.Table,
                intent.Definition.Schema,
                requiredColumns,
                out _))
        {
            // WHY: An empty projected index catalog proves absence, not that
            // its keys, includes and predicate columns exist. A complete table
            // must prove every dependency before Missing can authorize DDL.
            return StructureStateUnknown();
        }

        if (_projectedKeyAnalyzer is null
            || (projectedAnalysis.ObservedState != SafeMigrationObservedState.Missing
                && !HasProjectedIndexColumnChange(intent.Definition)))
        {
            return projectedAnalysis;
        }

        return _projectedKeyAnalyzer.ValidateProjectedIndex(
            intent,
            this,
            SelectProjectedKeyEnvironment(intent.Definition.Table, intent.Definition.Schema, liveAnalysis),
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

        if (_tables.TryGetValue(tableKey, out var table))
        {
            foreach (var index in table.Indexes.Values)
            {
                if (ProjectedIndexSemantics(index, definition))
                {
                    return true;
                }
            }
        }

        return _prerequisites.TryGetValue(tableKey, out var prerequisites)
            && prerequisites.Indexes.ContainsSemantically(definition);
    }

    /// <summary>Uses typed column identity after the owner dictionary resolves physical index identity.</summary>
    private bool ProjectedIndexSemantics(
        ExpectedIndexDefinition left,
        ExpectedIndexDefinition right
    ) => SafeMigrationDefinitionEquivalence.IndexSemantics(
        left,
        right,
        _objectIdentityNormalizer?.ColumnIdentifierComparer ?? StringComparer.Ordinal);

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
        && (definition.Method is null || StringComparer.OrdinalIgnoreCase.Equals(definition.Method, "BTREE"));

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
