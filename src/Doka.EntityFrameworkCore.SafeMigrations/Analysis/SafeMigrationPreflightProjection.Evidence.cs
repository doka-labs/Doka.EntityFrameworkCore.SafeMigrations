namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private bool Contains(
        string table,
        string? schema
    ) => _tables.ContainsKey(new TableKey(table, schema));

    private bool TryGet(
        string table,
        string? schema,
        [NotNullWhen(true)] out ProjectedTable? projection
    ) => _tables.TryGetValue(new TableKey(table, schema), out projection);

    private bool TryGetProjectedColumnDefinition(
        string table,
        string? schema,
        string column,
        [NotNullWhen(true)] out ExpectedColumnDefinition? definition
    )
    {
        definition = TryGetProjectedColumnState(table, schema, column, out var state) ? state.Definition : null;

        return definition is not null;
    }

    private bool TryGetProjectedColumnState(
        string table,
        string? schema,
        string column,
        out ProjectedColumnState state
    )
    {
        if (_projectedColumnStates.TryGetValue(new TableKey(table, schema), out var columns))
        {
            return columns.TryGetValue(column, out state);
        }

        state = default;

        return false;
    }

    private Dictionary<string, ProjectedColumnState> GetOrCreateProjectedColumnStates(
        string table,
        string? schema
    )
    {
        var key = new TableKey(table, schema);
        if (!_projectedColumnStates.TryGetValue(key, out var columns))
        {
            columns = new Dictionary<string, ProjectedColumnState>(
                new IdentifierComparer(_objectIdentityNormalizer, _objectIdentityNormalizer?.ColumnIdentifierComparer));

            _projectedColumnStates.Add(key, columns);
        }

        return columns;
    }

    bool ISafeMigrationProjectedColumnSource.TryGetProjectedColumn(
        string table,
        string? schema,
        string column,
        [NotNullWhen(true)] out ExpectedColumnDefinition? definition
    ) => TryGetProjectedColumnDefinition(table, schema, column, out definition);

    bool? ISafeMigrationProjectedTableSource.HasProjectedGeneratedColumnDependency(
        string table,
        string? schema,
        string column,
        IEqualityComparer<string> columnComparer
    )
    {
        if (_hasOpaqueSqlPostcondition || _hasOpaqueProviderPostcondition)
        {
            return null;
        }

        var key = new TableKey(table, schema);
        var comparable = true;
        if (_projectedColumnStates.TryGetValue(key, out var acceptedColumns))
        {
            foreach (var state in acceptedColumns.Values)
            {
                if (state.Definition is null)
                {
                    comparable &= state.IsMissing;

                    continue;
                }

                if (state.Definition.ComputedColumnSql is not null)
                {
                    comparable = false;
                }

                if (state.Definition.ComputedExpression is { } captured
                    && !SafeMigrationSqlExpressionInspector.IsStructurallyComparable(captured))
                {
                    comparable = false;
                }

                if (state.Definition?.ComputedExpression is { } expression
                    && SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression)
                    && SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
                        expression,
                        column,
                        columnComparer))
                {
                    // A current accepted generated member proves a positive
                    // dependency even when the rest of its owner is sparse.
                    return true;
                }
            }
        }

        if (_prerequisites.TryGetValue(key, out var prerequisites)
            && prerequisites.GeneratedColumnsComplete)
        {
            // WHY: Known member removals invalidate physical keys and layout,
            // but not this separately certified complete generated-member set.
            // No creation, emptiness or row proof follows from this bit.
            return comparable ? false : null;
        }

        if (IsProjectedTableStructureUnknown(table, schema)
            || !_tables.TryGetValue(key, out var owner))
        {
            return null;
        }

        foreach (var definition in owner.Columns.Values)
        {
            if (definition.ComputedColumnSql is not null)
            {
                comparable = false;
            }

            if (definition.ComputedExpression is not { } expression)
            {
                continue;
            }

            if (!SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression))
            {
                comparable = false;

                continue;
            }

            if (SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(expression, column, columnComparer))
            {
                return true;
            }
        }

        // WHY: Sparse accepted columns cannot prove that another generated
        // member is absent. Only this complete, current owner view establishes
        // nondependency; opaque nodes never become lexical absence evidence.
        return comparable ? false : null;
    }

    bool ISafeMigrationProjectedTableSource.TryGetProjectedTableState(
        string table,
        string? schema,
        out SafeMigrationProjectedTableState state
    )
    {
        var key = new TableKey(table, schema);
        var hasPrerequisites = _prerequisites.TryGetValue(key, out var prerequisites);

        if (!hasPrerequisites
            && !_tables.ContainsKey(key))
        {
            state = default;

            return false;
        }

        // WHY: A created table is empty only until an accepted data mutation.
        // Providers must consume both facts rather than infer row safety from
        // the creation marker alone.
        state = new SafeMigrationProjectedTableState(
            prerequisites?.NewlyCreated == true,
            HasUnanalyzedDataChanges(table, schema),
            prerequisites?.PrimaryKeyWasDropped == true,
            IsProjectedTableStructureUnknown(table, schema))
        {
            ColumnDropOriginOrdinal = _certifiedColumnDropOrigins?.TryGetValue(key, out var origin) == true
                ? origin
                : null,
        };

        return true;
    }

    private void SetProjectedColumnDefinition(
        string table,
        string? schema,
        ExpectedColumnDefinition definition,
        bool preservesLiveValueDomain = false,
        int valueMutationOriginOrdinal = -1
    )
    {
        definition = SafeMigrationCapturedExpressionNormalization.NormalizeColumn(
            definition,
            _objectIdentityNormalizer?.FoldUnquotedCapturedSqlIdentifiers == true);

        GetOrCreateProjectedColumnStates(table, schema)[definition.Name] = new ProjectedColumnState(
            definition,
            IsMissing: false,
            preservesLiveValueDomain,
            valueMutationOriginOrdinal);
    }

    private SafeMigrationProviderAnalysis SelectProjectedKeyEnvironment(
        string table,
        string? schema,
        SafeMigrationProviderAnalysis analysis
    ) => _prerequisites.TryGetValue(new TableKey(table, schema), out var prerequisites)
        && prerequisites.NewlyCreated
        && analysis.IndexPhysicalEnvironment?.NewTableEnvironment is { } creationEnvironment
            ? analysis.WithIndexPhysicalEnvironment(creationEnvironment)
            : analysis;

    private void SetProjectedColumnMissing(
        string table,
        string? schema,
        string column
    ) => GetOrCreateProjectedColumnStates(table, schema)[column] =
        new ProjectedColumnState(Definition: null, IsMissing: true);

    private void SetProjectedColumnUnknown(
        string table,
        string? schema,
        string column
    ) => GetOrCreateProjectedColumnStates(table, schema)[column] =
        new ProjectedColumnState(Definition: null, IsMissing: false);

    private bool IsProjectedColumnMissing(
        string table,
        string? schema,
        string column
    ) => TryGetProjectedColumnState(table, schema, column, out var state) && state.IsMissing;

    private bool IsProjectedColumnUnknown(
        string table,
        string? schema,
        string column
    ) => TryGetProjectedColumnState(table, schema, column, out var state)
        && state.Definition is null
        && !state.IsMissing;

    private bool IsProjectedTableStructureUnknown(
        string table,
        string? schema
    ) => _projectedUnknownTableStructures.Contains(new TableKey(table, schema));

    private void SetProjectedTableStructureUnknown(
        string table,
        string? schema,
        bool preservesGeneratedColumns = false
    )
    {
        var key = new TableKey(table, schema);

        _tables.Remove(key);
        _projectedMissingTables.Remove(key);
        _projectedUnknownTableStructures.Add(key);
        if (_prerequisites.TryGetValue(key, out var prerequisites))
        {
            prerequisites.CheckDefinitionsFollowKnownRename = false;
            if (!preservesGeneratedColumns)
            {
                prerequisites.GeneratedColumnsComplete = false;
            }
        }
    }

    /// <summary>Invalidates live structure evidence while retaining bounded removal-only histories.</summary>
    /// <param name="table">The physical table whose accepted structure changed.</param>
    /// <param name="schema">The normalized schema identity.</param>
    /// <param name="safeColumnDrop">Whether this accepted mutation is a safe column drop, not an opaque change.</param>
    /// <param name="safeForeignKeyDrop">Whether this accepted mutation removes only a safe foreign key.</param>
    /// <param name="preservesForeignKeyColumnProofs">Whether certified nullability retains sibling proofs.</param>
    /// <param name="preservesIntrinsicColumnSources">Whether native intrinsic sources remain unchanged.</param>
    /// <param name="preservesRenamedCheckDefinitions">Whether this rename just rebound accepted CHECK shapes.</param>
    /// <param name="preservesNativeGeneratedSnapshot">Whether only a guarded index or constraint was removed.</param>
    private void MarkProjectedTableStructureChanged(
        string table,
        string? schema,
        bool safeColumnDrop = false,
        bool safeForeignKeyDrop = false,
        bool preservesForeignKeyColumnProofs = false,
        bool preservesIntrinsicColumnSources = false,
        bool preservesRenamedCheckDefinitions = false,
        bool preservesNativeGeneratedSnapshot = false
    )
    {
        var key = new TableKey(table, schema);
        if (!preservesRenamedCheckDefinitions
            && _prerequisites.TryGetValue(key, out var prerequisites))
        {
            prerequisites.CheckDefinitionsFollowKnownRename = false;
        }

        var firstMutation = _projectedStructurallyModifiedTables.Add(key);
        if (_prerequisites.TryGetValue(key, out var generatedOwner))
        {
            // WHY: Guarded index and named-constraint removal cannot add or
            // rewrite generated members, but still invalidates independent
            // physical layout and key evidence. No later removal may revive
            // this narrow prefix after another structural or opaque effect.
            generatedOwner.NativeGeneratedSnapshotPreservedByIndexOrConstraintRemoval = preservesNativeGeneratedSnapshot
                && (firstMutation || generatedOwner.NativeGeneratedSnapshotPreservedByIndexOrConstraintRemoval);
        }

        if (safeColumnDrop && (firstMutation || _projectedColumnDropOnlyTables?.Contains(key) == true))
        {
            // WHY: SQLite can certify an unchanged column through a validated
            // rebuild after sibling drops, but no other mutation shares that proof.
            _projectedColumnDropOnlyTables ??= new HashSet<TableKey>(_projectedStructurallyModifiedTables.Comparer);
            _projectedColumnDropOnlyTables.Add(key);
        }
        else
        {
            // WHY: SQLite's drop-only rebuild certificate expires on any other
            // structural mutation, even a nullability-only repair certified by
            // the separate FK/intrinsic column-source contracts.
            _projectedColumnDropOnlyTables?.Remove(key);
        }

        if (safeForeignKeyDrop && (firstMutation || _projectedForeignKeyDropOnlyTables?.Contains(key) == true))
        {
            // WHY: FK removal changes table structure without changing an exact
            // column's storage or values. Only provider opt-in may reuse that proof.
            _projectedForeignKeyDropOnlyTables ??= new HashSet<TableKey>(_projectedStructurallyModifiedTables.Comparer);
            _projectedForeignKeyDropOnlyTables.Add(key);
        }
        else if (!preservesForeignKeyColumnProofs)
        {
            _projectedForeignKeyDropOnlyTables?.Remove(key);
        }

        if (preservesIntrinsicColumnSources
            && (firstMutation
                || _projectedIntrinsicColumnSourceTables?.Contains(key) == true
                || (safeColumnDrop && _certifiedColumnDropOrigins?.ContainsKey(key) == true)))
        {
            // WHY: Removing a PRIMARY or creating an ordinary index can preserve
            // column storage while invalidating candidate-key proofs. Keep those
            // lifetimes separate, and never revive a source after opaque mutation.
            _projectedIntrinsicColumnSourceTables ??= new HashSet<TableKey>(
                _projectedStructurallyModifiedTables.Comparer);

            _projectedIntrinsicColumnSourceTables.Add(key);
        }
        else if (!preservesForeignKeyColumnProofs)
        {
            _projectedIntrinsicColumnSourceTables?.Remove(key);
        }
    }

    private void MarkProjectedColumnChanged(
        string table,
        string? schema,
        string column
    )
    {
        var key = new TableKey(table, schema);
        if (!_projectedChangedColumns.TryGetValue(key, out var columns))
        {
            columns = new HashSet<string>(
                new IdentifierComparer(_objectIdentityNormalizer, _objectIdentityNormalizer?.ColumnIdentifierComparer));
            _projectedChangedColumns.Add(key, columns);
        }

        columns.Add(column);
    }

    private bool HasProjectedStrictTableDifference(
        ExpectedTableDefinition definition
    )
    {
        var key = new TableKey(definition.Table, definition.Schema);
        if (!_projectedChangedColumns.TryGetValue(key, out var changedColumns))
        {
            return false;
        }

        var expectedChangedColumnCount = 0;
        foreach (var expectedColumn in definition.Columns)
        {
            if (!changedColumns.Contains(expectedColumn.Name))
            {
                continue;
            }

            expectedChangedColumnCount++;
            if (!TryGetProjectedColumnDefinition(
                    definition.Table,
                    definition.Schema,
                    expectedColumn.Name,
                    out var projectedColumn)
                || !SafeMigrationDefinitionEquivalence.Column(projectedColumn, expectedColumn))
            {
                return true;
            }
        }

        // WHY: An added or repaired column outside the strict definition remains
        // an observable extra column. This proves drift without rescanning every
        // projected column as large migrations accumulate operations.

        return expectedChangedColumnCount != changedColumns.Count;
    }

    private void MarkProjectedTableStructureChanged(
        SafeMigrationIntent intent,
        bool preservesForeignKeyColumnProofs = false,
        bool preservesNativeGeneratedSnapshot = false
    )
    {
        var preservesIntrinsicColumnSources = _projectedColumnAnalyzer?.PreservesIntrinsicColumnSources(intent) == true;

        switch (intent)
        {
            case RenameTableIntent value:
                MarkProjectedTableStructureChanged(value.Name, value.Schema);
                MarkProjectedTableStructureChanged(value.NewName ?? value.Name, value.NewSchema ?? value.Schema);
                break;
            case EnsureColumnIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesForeignKeyColumnProofs: preservesForeignKeyColumnProofs,
                    preservesIntrinsicColumnSources: preservesForeignKeyColumnProofs);
                break;
            case AlterColumnIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesForeignKeyColumnProofs: preservesForeignKeyColumnProofs,
                    preservesIntrinsicColumnSources: preservesForeignKeyColumnProofs);
                break;
            case DropColumnIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    safeColumnDrop: true,
                    preservesIntrinsicColumnSources: preservesIntrinsicColumnSources);
                break;
            case RenameColumnIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema, preservesRenamedCheckDefinitions: true);
                break;
            case EnsureIndexIntent value:
                MarkProjectedTableStructureChanged(
                    value.Definition.Table,
                    value.Definition.Schema,
                    preservesIntrinsicColumnSources: preservesIntrinsicColumnSources);
                break;
            case DropIndexIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesNativeGeneratedSnapshot: preservesNativeGeneratedSnapshot);
                break;
            case RenameIndexIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case EnsurePrimaryKeyIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropPrimaryKeyIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesIntrinsicColumnSources: preservesIntrinsicColumnSources,
                    preservesNativeGeneratedSnapshot: preservesNativeGeneratedSnapshot);
                break;
            case EnsureUniqueConstraintIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropUniqueConstraintIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesNativeGeneratedSnapshot: preservesNativeGeneratedSnapshot);
                break;
            case EnsureCheckConstraintIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropCheckConstraintIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    preservesNativeGeneratedSnapshot: preservesNativeGeneratedSnapshot);
                break;
            case EnsureForeignKeyIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropForeignKeyIntent value:
                MarkProjectedTableStructureChanged(
                    value.Table,
                    value.Schema,
                    safeForeignKeyDrop: true,
                    preservesIntrinsicColumnSources: preservesIntrinsicColumnSources,
                    preservesNativeGeneratedSnapshot: preservesNativeGeneratedSnapshot);
                break;
        }
    }

    private void MarkProjectedTableStructureChanged(
        MigrationOperation operation
    )
    {
        switch (operation)
        {
            case AddColumnOperation value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case AlterColumnOperation value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case AlterTableOperation value:
                MarkProjectedTableStructureChanged(value.Name, value.Schema);
                break;
            case DropColumnOperation value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case RenameColumnOperation value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case DropIndexOperation { Table: not null } value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case DropCheckConstraintOperation { Table: not null } value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case RenameTableOperation value:
                MarkProjectedTableStructureChanged(value.Name, value.Schema);
                MarkProjectedTableStructureChanged(value.NewName ?? value.Name, value.NewSchema ?? value.Schema);
                break;
        }
    }

    private void RemoveProjectedColumnDefinition(
        string table,
        string? schema,
        string column
    )
    {
        var key = new TableKey(table, schema);
        if (_projectedColumnStates.TryGetValue(key, out var columns))
        {
            columns.Remove(column);
            if (columns.Count == 0)
            {
                _projectedColumnStates.Remove(key);
            }
        }
    }

    private void RemoveProjectedColumnDefinitions(
        string table,
        string? schema
    ) => _projectedColumnStates.Remove(new TableKey(table, schema));

    private void RenameProjectedColumnDefinition(
        string table,
        string? schema,
        string source,
        string target
    )
    {
        TryGetProjectedColumnDefinition(table, schema, source, out var definition);
        RemoveProjectedColumnDefinition(table, schema, source);
        RemoveProjectedColumnDefinition(table, schema, target);

        if (definition is not null)
        {
            SetProjectedColumnDefinition(table, schema, CopyProjectedColumn(definition, target));
        }
        else
        {
            SetProjectedColumnUnknown(table, schema, target);
        }

        SetProjectedColumnMissing(table, schema, source);
    }

    private void RenameProjectedTableColumnDefinitions(
        string table,
        string? schema,
        string targetTable,
        string? targetSchema
    )
    {
        _projectedColumnStates.Remove(new TableKey(table, schema), out var states);

        var target = new TableKey(targetTable, targetSchema);

        _projectedColumnStates.Remove(target);
        if (states is not null)
        {
            _projectedColumnStates[target] = states;
        }
    }

    /// <summary>Copies every immutable column facet while changing only its certified physical member name.</summary>
    internal static ExpectedColumnDefinition CopyProjectedColumn(
        ExpectedColumnDefinition value,
        string name
    ) => new(
        name,
        value.ClrType,
        value.IsNullable,
        value.StoreType,
        value.IsUnicode,
        value.MaxLength,
        value.IsFixedLength,
        value.IsRowVersion,
        value.Precision,
        value.Scale,
        value.Collation,
        value.Comment,
        value.DefaultValue,
        value.ComputedColumnSql,
        value.IsStored,
        value.ComputedExpression)
    {
        ProviderAnnotations = value.ProviderAnnotations,
        CapturedComputedSql = value.CapturedComputedSql,
    };

    private bool HasUnanalyzedDataChanges(
        string table,
        string? schema
    )
    {
        var key = new TableKey(table, schema);

        return _projectedDataMutationTables.Contains(key)
            || (_tables.TryGetValue(key, out var projectedTable)
                && projectedTable.DataMutationVersion < _providerDataMutationVersion)
            || (_prerequisites.TryGetValue(key, out var prerequisites)
                && prerequisites.DataMutationVersion < _providerDataMutationVersion)
            || (!_tables.ContainsKey(key) && !_prerequisites.ContainsKey(key) && _providerDataMutationVersion > 0);
    }

    private bool HasProjectedModelManagedUniqueKey(
        ExpectedIndexDefinition definition
    )
    {
        if (definition.NullsDistinct == false
            || definition.Keys.Any(static key => key.Column is null))
        {
            return false;
        }

        var table = new TableKey(definition.Table, definition.Schema);
        if (!_projectedModelManagedUniqueKeys.TryGetValue(table, out var uniqueKeys))
        {
            return false;
        }

        var columns = definition
            .Keys
            .Select(static key => key.Column!)
            .ToArray();

        return uniqueKeys.Contains(ModelManagedUniqueKeyFingerprint(columns));
    }

    private string ModelManagedUniqueKeyFingerprint(
        IReadOnlyList<string> columns
    )
    {
        using var writer = new CanonicalHashWriter();
        var canonicalColumns = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(
            columns,
            _objectIdentityNormalizer,
            out var ordinals);

        writer.Add(canonicalColumns.Count);
        // WHY: Read canonical labels through the same ordinal permutation used for row identity.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var column = 0; column < canonicalColumns.Count; column++)
        {
            writer.Add(canonicalColumns[ordinals?[column] ?? column]);
        }

        return writer.GetHash();
    }

    private SafeMigrationProviderAnalysis InvalidateDataDependentMissing(
        string table,
        string? schema,
        SafeMigrationProviderAnalysis analysis
    ) => HasUnanalyzedDataChanges(table, schema)
        && analysis.ObservedState is SafeMigrationObservedState.Missing
            or SafeMigrationObservedState.PrerequisiteMissing
            ? DataStateUnknown()
            : analysis;

    private SafeMigrationProviderAnalysis InvalidateStaleLiveDataProof(
        string table,
        string? schema,
        SafeMigrationProviderAnalysis analysis
    ) => analysis.RequiresLiveDataProof && HasUnanalyzedDataChanges(table, schema) ? DataStateUnknown() : analysis;
}
