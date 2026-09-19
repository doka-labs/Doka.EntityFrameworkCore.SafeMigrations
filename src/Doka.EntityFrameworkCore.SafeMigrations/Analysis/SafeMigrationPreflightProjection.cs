namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection : ISafeMigrationProjectedColumnSource
{
    private readonly ISafeMigrationProviderOperationProjection? _providerOperationProjection;
    private readonly ISafeMigrationProjectedKeyAnalyzer? _projectedKeyAnalyzer;
    private readonly ISafeMigrationProviderObjectIdentityNormalizer? _objectIdentityNormalizer;
    private readonly Dictionary<TableKey, ProjectedTable> _tables;

    // WHY: Strict table projections retain complete definitions. This second view
    // records only prerequisites proven by earlier convergence operations, so
    // a later operation cannot infer safety from an object that was rejected.
    private readonly Dictionary<TableKey, ProjectedPrerequisites> _prerequisites;
    private readonly HashSet<IndexKey> _droppedPhysicalKeys;
    private readonly HashSet<TableKey> _projectedDataMutationTables;
    private readonly Dictionary<TableKey, HashSet<string>> _projectedModelManagedUniqueKeys;
    private readonly Dictionary<ColumnKey, ExpectedColumnDefinition> _projectedColumnDefinitions;
    private readonly HashSet<TableKey> _projectedCandidateKeyMutationTables;
    private readonly HashSet<ColumnKey> _projectedMissingColumns;
    private readonly HashSet<ColumnKey> _projectedUnknownColumns;
    private readonly HashSet<TableKey> _projectedMissingTables;
    private readonly HashSet<TableKey> _projectedUnknownTableStructures;
    private readonly HashSet<TableKey> _projectedStructurallyModifiedTables;
    private readonly Dictionary<TableKey, HashSet<string>> _projectedChangedColumns;
    private bool _hasOpaqueProviderPostcondition;
    private long _providerDataMutationVersion;

    public SafeMigrationPreflightProjection(
        ISafeMigrationProviderOperationProjection? providerOperationProjection = null,
        ISafeMigrationProjectedKeyAnalyzer? projectedKeyAnalyzer = null,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer = null
    )
    {
        _providerOperationProjection = providerOperationProjection;
        _projectedKeyAnalyzer = projectedKeyAnalyzer;
        _objectIdentityNormalizer = objectIdentityNormalizer;

        var tableComparer = new TableKeyComparer(objectIdentityNormalizer);
        var indexComparer = new IndexKeyComparer(objectIdentityNormalizer);
        var columnComparer = new ColumnKeyComparer(objectIdentityNormalizer);

        _tables = new Dictionary<TableKey, ProjectedTable>(tableComparer);
        _prerequisites = new Dictionary<TableKey, ProjectedPrerequisites>(tableComparer);
        _droppedPhysicalKeys = new HashSet<IndexKey>(indexComparer);
        _projectedDataMutationTables = new HashSet<TableKey>(tableComparer);
        _projectedModelManagedUniqueKeys = new Dictionary<TableKey, HashSet<string>>(tableComparer);
        _projectedColumnDefinitions = new Dictionary<ColumnKey, ExpectedColumnDefinition>(columnComparer);
        _projectedCandidateKeyMutationTables = new HashSet<TableKey>(tableComparer);
        _projectedMissingColumns = new HashSet<ColumnKey>(columnComparer);
        _projectedUnknownColumns = new HashSet<ColumnKey>(columnComparer);
        _projectedMissingTables = new HashSet<TableKey>(tableComparer);
        _projectedUnknownTableStructures = new HashSet<TableKey>(tableComparer);
        _projectedStructurallyModifiedTables = new HashSet<TableKey>(tableComparer);
        _projectedChangedColumns = new Dictionary<TableKey, HashSet<string>>(tableComparer);
        _modelManagedRows = new Dictionary<ModelManagedRowKey, ProjectedModelManagedRow>(
            new ModelManagedRowKeyComparer(objectIdentityNormalizer));
    }

    public SafeMigrationProviderAnalysis Project(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(liveAnalysis);

        if (_objectIdentityNormalizer?.IsObjectIdentityMismatch(liveAnalysis) == true)
        {
            // WHY: Earlier operations may repair an unsupported physical shape,
            // but they can never change which database object this operation names.

            return liveAnalysis;
        }

        if (_providerOperationProjection?.IsSequenceAwareAnalysis(operation, liveAnalysis) == true)
        {
            // WHY: Some providers can derive a blocking dependency from the
            // complete ordered operation stream. Replacing that result with a
            // generic structural fallback would discard stronger evidence.

            return liveAnalysis;
        }

        if (_hasOpaqueProviderPostcondition)
        {
            // WHY: Provider analysis is captured before ordered operations run.
            // Arbitrary provider SQL can invalidate every historical catalog
            // observation, so no later safe operation may reuse that evidence.

            return StructureStateUnknown();
        }

        return operation.Intent switch
        {
            EnsureSchemaIntent value => Project(value, liveAnalysis),
            DropSchemaIntent value => Project(value, liveAnalysis),
            EnsureTableIntent value => Project(value, liveAnalysis),
            DropTableIntent value => Project(value, liveAnalysis),
            RenameTableIntent value => Project(value, liveAnalysis),
            EnsureColumnIntent value => Project(value, liveAnalysis),
            AlterColumnIntent value => Project(value, liveAnalysis),
            DropColumnIntent value => Project(value, liveAnalysis),
            RenameColumnIntent value => Project(value, liveAnalysis),
            EnsureIndexIntent value => Project(value, liveAnalysis),
            DropIndexIntent value => Project(value, liveAnalysis),
            RenameIndexIntent value => Project(value, liveAnalysis),
            EnsurePrimaryKeyIntent value => Project(value, liveAnalysis),
            DropPrimaryKeyIntent value => Project(value, liveAnalysis),
            EnsureUniqueConstraintIntent value => Project(value, liveAnalysis),
            DropUniqueConstraintIntent value => Project(value, liveAnalysis),
            EnsureCheckConstraintIntent value => Project(value, liveAnalysis),
            DropCheckConstraintIntent value => Project(value, liveAnalysis),
            EnsureForeignKeyIntent value => Project(value, liveAnalysis),
            DropForeignKeyIntent value => Project(value, liveAnalysis),
            ModelManagedDataIntent value => Project(value, liveAnalysis),
            _ => liveAnalysis,
        };
    }

    public void Observe(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(liveAnalysis);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Action.RejectsExecution())
        {
            return;
        }

        // WHY: Preflight never mutates the database. Accepted operations instead
        // update this in-memory catalog so later operations observe prior ones.
        switch (operation.Intent)
        {
            case EnsureSchemaIntent value:
                Observe(value, decision);
                break;
            case DropSchemaIntent value:
                Observe(value, decision);
                break;
            case EnsureTableIntent value:
                Observe(value, analysis, decision);
                break;
            case DropTableIntent value:
                Observe(value, decision);
                break;
            case RenameTableIntent value:
                Observe(value, decision);
                break;
            case EnsureColumnIntent value:
                Observe(value, liveAnalysis, decision);
                break;
            case AlterColumnIntent value:
                Observe(value, decision);
                break;
            case DropColumnIntent value:
                Observe(value, decision);
                break;
            case RenameColumnIntent value:
                Observe(value, decision);
                break;
            case EnsureIndexIntent value:
                Observe(value, liveAnalysis, decision);
                break;
            case DropIndexIntent value:
                Observe(value, decision);
                break;
            case RenameIndexIntent value:
                Observe(value, decision);
                break;
            case EnsurePrimaryKeyIntent value:
                Observe(value, decision);
                break;
            case DropPrimaryKeyIntent value:
                Observe(value, decision);
                break;
            case EnsureUniqueConstraintIntent value:
                Observe(value, liveAnalysis, decision);
                break;
            case DropUniqueConstraintIntent value:
                Observe(value, decision);
                break;
            case EnsureCheckConstraintIntent value:
                Observe(value, liveAnalysis, decision);
                break;
            case DropCheckConstraintIntent value:
                Observe(value, decision);
                break;
            case EnsureForeignKeyIntent value:
                Observe(value, liveAnalysis, decision);
                break;
            case DropForeignKeyIntent value:
                Observe(value, decision);
                break;
            case ModelManagedDataIntent value:
                Observe(value, analysis, decision);
                break;
        }

        if ((decision.Action is SafeMigrationAction.Apply or SafeMigrationAction.Repair)
            && operation.Intent is not EnsureTableIntent
            && operation.Intent is not DropTableIntent)
        {
            MarkProjectedTableStructureChanged(operation.Intent);
        }
    }

    /// <summary>
    /// Projects deterministic structural postconditions of an ordered ordinary
    /// EF operation without claiming that SafeMigrations analyzed the operation.
    /// </summary>
    /// <param name="operation">The provider-owned operation that precedes later safe operations.</param>
    public void ObserveProviderPostcondition(
        MigrationOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (_providerOperationProjection?.PreservesExistingTableState(operation) == true)
        {
            // WHY: Only the active provider can prove that one of its ordinary
            // operations leaves every existing table-scoped fact unchanged.
            // Unknown providers and operations continue through the fail-closed
            // structural invalidation below.

            return;
        }

        switch (operation)
        {
            case CreateTableOperation value:
                ObserveProviderPostcondition(value);
                break;
            case AddColumnOperation value:
                ObserveProviderPostcondition(value);
                break;
            case AlterColumnOperation value:
                ObserveProviderPostcondition(value);
                break;
            case AlterTableOperation value:
                ObserveProviderPostcondition(value);
                break;
            case DropColumnOperation value:
                ObserveProviderPostcondition(value);
                break;
            case RenameColumnOperation value:
                ObserveProviderPostcondition(value);
                break;
            case DropIndexOperation value:
                ObserveProviderPostcondition(value);
                break;
            case DropTableOperation value:
                ObserveProviderPostcondition(value);
                break;
            case RenameTableOperation value:
                ObserveProviderPostcondition(value);
                break;
            case InsertDataOperation:
            case UpdateDataOperation:
            case DeleteDataOperation:
                ObserveProviderDataMutation();
                InvalidateModelManagedDataProjection();
                break;
            default:
                // WHY: An unrecognized provider operation may contain arbitrary DDL
                // or data changes. Invalidate live row-level proofs as well as
                // inferred structure so an opaque SQL operation cannot make a
                // later additive constraint appear safe.
                SetOpaqueProviderPostcondition(mayMutateData: true);
                break;
        }

        MarkProjectedTableStructureChanged(operation);
    }

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
    ) => _projectedColumnDefinitions.TryGetValue(
        new ColumnKey(table, schema, column),
        out definition);

    bool ISafeMigrationProjectedColumnSource.TryGetProjectedColumn(
        string table,
        string? schema,
        string column,
        [NotNullWhen(true)] out ExpectedColumnDefinition? definition
    ) => TryGetProjectedColumnDefinition(table, schema, column, out definition);

    private void SetProjectedColumnDefinition(
        string table,
        string? schema,
        ExpectedColumnDefinition definition
    )
    {
        var key = new ColumnKey(table, schema, definition.Name);

        _projectedColumnDefinitions[key] = definition;
        _projectedMissingColumns.Remove(key);
        _projectedUnknownColumns.Remove(key);
    }

    private void SetProjectedColumnMissing(
        string table,
        string? schema,
        string column
    )
    {
        var key = new ColumnKey(table, schema, column);

        _projectedColumnDefinitions.Remove(key);
        _projectedUnknownColumns.Remove(key);
        _projectedMissingColumns.Add(key);
    }

    private void SetProjectedColumnUnknown(
        string table,
        string? schema,
        string column
    )
    {
        var key = new ColumnKey(table, schema, column);

        _projectedColumnDefinitions.Remove(key);
        _projectedMissingColumns.Remove(key);
        _projectedUnknownColumns.Add(key);
    }

    private bool IsProjectedColumnMissing(
        string table,
        string? schema,
        string column
    ) => _projectedMissingColumns.Contains(new ColumnKey(table, schema, column));

    private bool IsProjectedColumnUnknown(
        string table,
        string? schema,
        string column
    ) => _projectedUnknownColumns.Contains(new ColumnKey(table, schema, column));

    private bool IsProjectedTableStructureUnknown(
        string table,
        string? schema
    ) => _projectedUnknownTableStructures.Contains(new TableKey(table, schema));

    private void SetProjectedTableStructureUnknown(
        string table,
        string? schema
    )
    {
        var key = new TableKey(table, schema);

        _tables.Remove(key);
        _projectedMissingTables.Remove(key);
        _projectedUnknownTableStructures.Add(key);
    }

    private void MarkProjectedTableStructureChanged(
        string table,
        string? schema
    ) => _projectedStructurallyModifiedTables.Add(new TableKey(table, schema));

    private void MarkProjectedColumnChanged(
        string table,
        string? schema,
        string column
    )
    {
        var key = new TableKey(table, schema);
        if (!_projectedChangedColumns.TryGetValue(key, out var columns))
        {
            columns = new HashSet<string>(new IdentifierComparer(_objectIdentityNormalizer));
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
        SafeMigrationIntent intent
    )
    {
        switch (intent)
        {
            case RenameTableIntent value:
                MarkProjectedTableStructureChanged(value.Name, value.Schema);
                MarkProjectedTableStructureChanged(value.NewName ?? value.Name, value.NewSchema ?? value.Schema);
                break;
            case EnsureColumnIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case AlterColumnIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case DropColumnIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case RenameColumnIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case EnsureIndexIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropIndexIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case RenameIndexIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case EnsurePrimaryKeyIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropPrimaryKeyIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case EnsureUniqueConstraintIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropUniqueConstraintIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case EnsureCheckConstraintIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropCheckConstraintIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
                break;
            case EnsureForeignKeyIntent value:
                MarkProjectedTableStructureChanged(value.Definition.Table, value.Definition.Schema);
                break;
            case DropForeignKeyIntent value:
                MarkProjectedTableStructureChanged(value.Table, value.Schema);
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
        var key = new ColumnKey(table, schema, column);

        _projectedColumnDefinitions.Remove(key);
        _projectedMissingColumns.Remove(key);
        _projectedUnknownColumns.Remove(key);
    }

    private void RemoveProjectedColumnDefinitions(
        string table,
        string? schema
    )
    {
        var keys = _projectedColumnDefinitions.Keys
            .Where(key => SameTable(key, table, schema))
            .ToArray();

        foreach (var key in keys)
        {
            _projectedColumnDefinitions.Remove(key);
        }

        _projectedMissingColumns.RemoveWhere(key => SameTable(key, table, schema));
        _projectedUnknownColumns.RemoveWhere(key => SameTable(key, table, schema));
    }

    private void RenameProjectedColumnDefinition(
        string table,
        string? schema,
        string source,
        string target
    )
    {
        var sourceKey = new ColumnKey(table, schema, source);
        _projectedColumnDefinitions.Remove(sourceKey, out var definition);

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
        var states = _projectedColumnDefinitions
            .Where(pair => SameTable(pair.Key, table, schema))
            .Select(static pair => new ProjectedColumnState(
                pair.Key.Name,
                pair.Value,
                IsMissing: false,
                IsUnknown: false))
            .Concat(
                _projectedMissingColumns
                    .Where(key => SameTable(key, table, schema))
                    .Select(static key => new ProjectedColumnState(key.Name, null, IsMissing: true, IsUnknown: false)))
            .Concat(
                _projectedUnknownColumns
                    .Where(key => SameTable(key, table, schema))
                    .Select(static key => new ProjectedColumnState(key.Name, null, IsMissing: false, IsUnknown: true)))
            .ToArray();

        RemoveProjectedColumnDefinitions(table, schema);
        RemoveProjectedColumnDefinitions(targetTable, targetSchema);

        foreach (var state in states)
        {
            if (state.Definition is not null)
            {
                SetProjectedColumnDefinition(targetTable, targetSchema, state.Definition);
            }
            else if (state.IsMissing)
            {
                SetProjectedColumnMissing(targetTable, targetSchema, state.Name);
            }
            else if (state.IsUnknown)
            {
                SetProjectedColumnUnknown(targetTable, targetSchema, state.Name);
            }
            else
            {
                throw new InvalidOperationException("A projected column state has no representation.");
            }
        }
    }

    private bool SameTable(
        ColumnKey key,
        string table,
        string? schema
    ) => IdentifierEquals(_objectIdentityNormalizer, key.Table, table) && SameSchema(key.Schema, schema);

    private bool SameSchema(
        string? left,
        string? right
    ) => StringComparer.Ordinal.Equals(
        NormalizeSchema(_objectIdentityNormalizer, left),
        NormalizeSchema(_objectIdentityNormalizer, right));

    private static string? NormalizeSchema(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        string? schema
    ) => normalizer is null ? schema : normalizer.NormalizeSchema(schema);

    private static string NormalizeIdentifier(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        string identifier
    ) => normalizer is null ? identifier : normalizer.NormalizeIdentifier(identifier);

    private static bool IdentifierEquals(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        string left,
        string right
    ) => normalizer is null
        ? StringComparer.Ordinal.Equals(left, right)
        : normalizer.IdentifierComparer.Equals(left, right);

    private static int IdentifierHashCode(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer,
        string identifier
    ) => normalizer is null
        ? StringComparer.Ordinal.GetHashCode(identifier)
        : normalizer.IdentifierComparer.GetHashCode(identifier);

    private static ExpectedColumnDefinition CopyProjectedColumn(
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
            || (!_tables.ContainsKey(key)
                && !_prerequisites.ContainsKey(key)
                && _providerDataMutationVersion > 0);
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

        var columns = definition.Keys
            .Select(static key => key.Column!)
            .ToArray();

        return uniqueKeys.Contains(ModelManagedUniqueKeyFingerprint(columns));
    }

    private string ModelManagedUniqueKeyFingerprint(
        IReadOnlyList<string> columns
    )
    {
        using var writer = new CanonicalHashWriter();

        writer.Add(columns.Count);
        foreach (var column in columns)
        {
            writer.Add(NormalizeIdentifier(_objectIdentityNormalizer, column));
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
    ) => analysis.RequiresLiveDataProof && HasUnanalyzedDataChanges(table, schema)
        ? DataStateUnknown()
        : analysis;

    private static SafeMigrationProviderAnalysis DataStateUnknown() => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "projected_data_state_unknown");

    private static SafeMigrationProviderAnalysis StructureStateUnknown() => new(
        SafeMigrationObservedState.PrerequisiteMissing,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "projected_structure_state_unknown");

    private static SafeMigrationProviderAnalysis AnalyzeDefinition<T>(
        IReadOnlyDictionary<string, T> definitions,
        string name,
        T expected,
        Func<T, T, bool> equals,
        SafeMigrationRepairCapability repairCapability = SafeMigrationRepairCapability.None
    )
        where T : class
    {
        if (!definitions.TryGetValue(name, out var actual))
        {
            return Analysis(SafeMigrationObservedState.Missing, repairCapability);
        }

        return Analysis(
            equals(actual, expected) ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            repairCapability);
    }

    private static SafeMigrationProviderAnalysis AnalyzeOptional<T>(
        T? actual,
        T expected,
        Func<T, T, bool> equals
    )
        where T : class
    {
        if (actual is null)
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        return Analysis(
            equals(actual, expected) ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different);
    }

    private static SafeMigrationProviderAnalysis Analysis(
        SafeMigrationObservedState state,
        SafeMigrationRepairCapability repairCapability = SafeMigrationRepairCapability.None
    ) => new(state, repairCapability, state == SafeMigrationObservedState.Matching, $"projected_{StateCode(state)}");

    private static string StateCode(
        SafeMigrationObservedState state
    ) => state switch
    {
        SafeMigrationObservedState.Missing => "missing",
        SafeMigrationObservedState.Matching => "matching",
        SafeMigrationObservedState.Different => "different",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private readonly record struct TableKey(
        string Table,
        string? Schema
    );

    private readonly record struct IndexKey(
        string Table,
        string? Schema,
        string Name
    );

    private readonly record struct ColumnKey(
        string Table,
        string? Schema,
        string Name
    );

    private sealed class TableKeyComparer(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer
    ) : IEqualityComparer<TableKey>
    {
        public bool Equals(
            TableKey left,
            TableKey right
        ) => IdentifierEquals(normalizer, left.Table, right.Table)
            && StringComparer.Ordinal.Equals(Normalize(left.Schema), Normalize(right.Schema));

        public int GetHashCode(
            TableKey value
        ) => HashCode.Combine(
            IdentifierHashCode(normalizer, value.Table),
            Normalize(value.Schema) is { } schema ? StringComparer.Ordinal.GetHashCode(schema) : 0);

        private string? Normalize(
            string? schema
        ) => NormalizeSchema(normalizer, schema);
    }

    private sealed class IndexKeyComparer(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer
    ) : IEqualityComparer<IndexKey>
    {
        private readonly TableKeyComparer _tableComparer = new(normalizer);

        public bool Equals(
            IndexKey left,
            IndexKey right
        ) => _tableComparer.Equals(
                new TableKey(left.Table, left.Schema),
                new TableKey(right.Table, right.Schema))
            && IdentifierEquals(normalizer, left.Name, right.Name);

        public int GetHashCode(
            IndexKey value
        ) => HashCode.Combine(
            _tableComparer.GetHashCode(new TableKey(value.Table, value.Schema)),
            IdentifierHashCode(normalizer, value.Name));
    }

    private sealed class ColumnKeyComparer(
        ISafeMigrationProviderObjectIdentityNormalizer? normalizer
    ) : IEqualityComparer<ColumnKey>
    {
        private readonly TableKeyComparer _tableComparer = new(normalizer);

        public bool Equals(
            ColumnKey left,
            ColumnKey right
        ) => _tableComparer.Equals(
                new TableKey(left.Table, left.Schema),
                new TableKey(right.Table, right.Schema))
            && IdentifierEquals(normalizer, left.Name, right.Name);

        public int GetHashCode(
            ColumnKey value
        ) => HashCode.Combine(
            _tableComparer.GetHashCode(new TableKey(value.Table, value.Schema)),
            IdentifierHashCode(normalizer, value.Name));
    }

    private sealed class IdentifierComparer(ISafeMigrationProviderObjectIdentityNormalizer? normalizer)
        : IEqualityComparer<string>
    {
        public bool Equals(
            string? left,
            string? right
        ) => ReferenceEquals(left, right)
            || left is not null && right is not null && IdentifierEquals(normalizer, left, right);

        public int GetHashCode(
            string value
        ) => IdentifierHashCode(normalizer, value);
    }

    private sealed record ProjectedColumnState(
        string Name,
        ExpectedColumnDefinition? Definition,
        bool IsMissing,
        bool IsUnknown
    );

    private sealed class ProjectedPrerequisites(
        bool newlyCreated,
        long dataMutationVersion,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer
    )
    {
        private readonly ISafeMigrationProviderObjectIdentityNormalizer? _objectIdentityNormalizer
            = objectIdentityNormalizer;

        public ProjectedDefinitionSet<ExpectedCheckConstraintDefinition> CheckConstraints { get; } = new(
            SafeMigrationSemanticDefinitionComparers.CheckConstraint,
            objectIdentityNormalizer);

        public Dictionary<string, ProjectedColumn> Columns { get; } =
            new(new IdentifierComparer(objectIdentityNormalizer));

        public long DataMutationVersion { get; } = dataMutationVersion;

        public long? EmptyTableProofVersion { get; set; }

        public ProjectedDefinitionSet<ExpectedForeignKeyDefinition> ForeignKeys { get; } = new(
            SafeMigrationSemanticDefinitionComparers.ForeignKey,
            objectIdentityNormalizer);

        public ProjectedDefinitionSet<ExpectedIndexDefinition> Indexes { get; } = new(
            SafeMigrationSemanticDefinitionComparers.Index,
            objectIdentityNormalizer);

        public bool NewlyCreated { get; } = newlyCreated;

        public ExpectedPrimaryKeyDefinition? PrimaryKey { get; private set; }

        public bool PrimaryKeyWasDropped { get; private set; }

        public ExpectedPrimaryKeyDefinition? RemovedPrimaryKey { get; private set; }

        public ProjectedDefinitionSet<ExpectedUniqueConstraintDefinition> UniqueConstraints { get; } =
            new(SafeMigrationSemanticDefinitionComparers.UniqueConstraint, objectIdentityNormalizer);

        public void AcceptPrimaryKey(
            ExpectedPrimaryKeyDefinition definition
        )
        {
            PrimaryKey = definition;
            PrimaryKeyWasDropped = false;
            RemovedPrimaryKey = null;
        }

        public void AcceptUniqueConstraint(
            ExpectedUniqueConstraintDefinition definition,
            string physicalName
        )
        {
            UniqueConstraints.AcceptPhysical(physicalName, definition);
        }

        public void DropPrimaryKey()
        {
            RemovedPrimaryKey = PrimaryKey;
            PrimaryKey = null;
            PrimaryKeyWasDropped = true;
        }

        public bool HasCandidateKey(
            string table,
            string? schema,
            IReadOnlyList<string> columns
        )
        {
            if (PrimaryKey is not null
                && IdentifierEquals(_objectIdentityNormalizer, PrimaryKey.Table, table)
                && StringComparer.Ordinal.Equals(
                    NormalizeSchema(_objectIdentityNormalizer, PrimaryKey.Schema),
                    NormalizeSchema(_objectIdentityNormalizer, schema))
                && SameColumns(PrimaryKey.Columns, columns, _objectIdentityNormalizer))
            {
                return true;
            }

            var candidate = new ExpectedUniqueConstraintDefinition(
                "doka_projected_candidate_key",
                table,
                columns,
                schema);

            return UniqueConstraints.ContainsSemantically(candidate);
        }
    }

    private sealed class ProjectedDefinitionSet<T>(
        IEqualityComparer<T> semanticComparer,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer
    )
        where T : class
    {
        private readonly IdentifierComparer _identifierComparer = new(objectIdentityNormalizer);

        private readonly Dictionary<string, PhysicalDefinition> _aliases = new(
            new IdentifierComparer(objectIdentityNormalizer));

        private readonly HashSet<string> _missingNames = new(new IdentifierComparer(objectIdentityNormalizer));

        private readonly Dictionary<string, PhysicalDefinition> _physicalDefinitions =
            new(new IdentifierComparer(objectIdentityNormalizer));

        private readonly Dictionary<string, T> _removedDefinitions =
            new(new IdentifierComparer(objectIdentityNormalizer));

        private readonly HashSet<string> _unresolvedMissingNames =
            new(new IdentifierComparer(objectIdentityNormalizer));

        // WHY: A semantic NoOp is an alias for an existing physical object, not
        // another object. Keeping that binding lets one drop invalidate every
        // alias without hiding a distinct physical object with the same shape.
        private readonly Dictionary<T, HashSet<PhysicalDefinition>> _semanticDefinitions = new(semanticComparer);
        private readonly HashSet<T> _mutatedSemanticDefinitions = new(semanticComparer);

        public void AcceptPhysical(
            string physicalName,
            T definition
        )
        {
            if (_physicalDefinitions.Remove(physicalName, out var previous))
            {
                RemovePhysical(previous);
            }

            RemoveAlias(physicalName);
            RemoveMissingName(physicalName);

            var physical = new PhysicalDefinition(physicalName, definition, _identifierComparer);
            physical.Aliases.Add(physicalName);
            _physicalDefinitions.Add(physicalName, physical);
            _aliases[physicalName] = physical;

            if (!_semanticDefinitions.TryGetValue(definition, out var definitions))
            {
                definitions = [];
                _semanticDefinitions.Add(definition, definitions);
            }

            definitions.Add(physical);
        }

        public bool BindAlias(
            string alias,
            string physicalName,
            T definition
        )
        {
            if (!_physicalDefinitions.TryGetValue(physicalName, out var physical))
            {
                AcceptPhysical(physicalName, definition);
                physical = _physicalDefinitions[physicalName];
            }
            else if (!semanticComparer.Equals(physical.Definition, definition))
            {
                return false;
            }

            if (_physicalDefinitions.ContainsKey(alias)
                && !_identifierComparer.Equals(alias, physicalName))
            {
                return false;
            }

            RemoveAlias(alias);
            RemoveMissingName(alias);
            _aliases[alias] = physical;
            physical.Aliases.Add(alias);

            return true;
        }

        public bool BindUniqueSemanticAlias(
            string alias,
            T definition
        )
        {
            if (!_semanticDefinitions.TryGetValue(definition, out var definitions)
                || definitions.Count != 1)
            {
                return false;
            }

            var physical = definitions.Single();

            return BindAlias(alias, physical.Name, definition);
        }

        public bool ContainsSemantically(
            T definition
        ) => _semanticDefinitions.ContainsKey(definition);

        public void MarkPhysicalMissing(
            string physicalName
        )
        {
            if (RemovePhysical(physicalName, out _))
            {
                return;
            }

            RemoveAlias(physicalName);
            _missingNames.Add(physicalName);
            _removedDefinitions.Remove(physicalName);
            _unresolvedMissingNames.Add(physicalName);
        }

        public bool SemanticallyEquals(
            T left,
            T right
        ) => semanticComparer.Equals(left, right);

        public bool RemovePhysical(
            string physicalName,
            [NotNullWhen(true)] out T? definition
        )
        {
            if (!_physicalDefinitions.Remove(physicalName, out var physical))
            {
                definition = null;
                return false;
            }

            definition = physical.Definition;
            RemovePhysical(physical);

            return true;
        }

        public bool RemoveWhere(
            Func<T, bool> predicate
        )
        {
            ArgumentNullException.ThrowIfNull(predicate);

            var names = _physicalDefinitions
                .Where(pair => predicate(pair.Value.Definition))
                .Select(static pair => pair.Key)
                .ToArray();

            foreach (var name in names)
            {
                _ = RemovePhysical(name, out _);
            }

            return names.Length > 0;
        }

        public ProjectedDefinitionMutation ResolveMutation(
            string name,
            T expected,
            SafeMigrationProviderAnalysis liveAnalysis
        )
        {
            if (liveAnalysis.ObservedState is not (
                    SafeMigrationObservedState.Missing
                    or SafeMigrationObservedState.Matching
                    or SafeMigrationObservedState.Different))
            {
                return ProjectedDefinitionMutation.None;
            }

            if (_missingNames.Contains(name))
            {
                if (_removedDefinitions.TryGetValue(name, out var removed)
                    && semanticComparer.Equals(removed, expected))
                {
                    return ProjectedDefinitionMutation.Missing;
                }

                return liveAnalysis is { ObservedState: SafeMigrationObservedState.Matching, MatchedObjectName: not null }
                    && _identifierComparer.Equals(liveAnalysis.MatchedObjectName, name)
                        ? ProjectedDefinitionMutation.Missing
                        : ProjectedDefinitionMutation.Unknown;
            }

            if (liveAnalysis.MatchedObjectName is { } physicalName
                && _missingNames.Contains(physicalName))
            {
                return liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                    ? ProjectedDefinitionMutation.Missing
                    : ProjectedDefinitionMutation.Unknown;
            }

            // WHY: A missing provider identity means the immutable live match
            // may have represented more than one semantic candidate. Once one
            // such candidate was mutated, the remaining live state is unknown.

            return liveAnalysis is { ObservedState: SafeMigrationObservedState.Matching, MatchedObjectName: null }
                && (_unresolvedMissingNames.Count > 0
                    || _mutatedSemanticDefinitions.Contains(expected))
                    ? ProjectedDefinitionMutation.Unknown
                    : ProjectedDefinitionMutation.None;
        }

        public bool TryGetValue(
            string name,
            [NotNullWhen(true)] out T? definition
        )
        {
            if (_aliases.TryGetValue(name, out var physical))
            {
                definition = physical.Definition;
                return true;
            }

            definition = null;
            return false;
        }

        private void RemoveAlias(
            string alias
        )
        {
            if (!_aliases.Remove(alias, out var physical))
            {
                return;
            }

            physical.Aliases.Remove(alias);
        }

        private void RemoveMissingName(
            string name
        )
        {
            _missingNames.Remove(name);
            _removedDefinitions.Remove(name);
            _unresolvedMissingNames.Remove(name);
        }

        private void RemovePhysical(
            PhysicalDefinition physical
        )
        {
            foreach (var alias in physical.Aliases)
            {
                _aliases.Remove(alias);
                _missingNames.Add(alias);
                _removedDefinitions[alias] = physical.Definition;
                _unresolvedMissingNames.Remove(alias);
            }

            _mutatedSemanticDefinitions.Add(physical.Definition);

            if (_semanticDefinitions.TryGetValue(physical.Definition, out var definitions))
            {
                definitions.Remove(physical);
                if (definitions.Count == 0)
                {
                    _semanticDefinitions.Remove(physical.Definition);
                }
            }
        }

        private sealed class PhysicalDefinition(
            string name,
            T definition,
            IEqualityComparer<string> identifierComparer
        )
        {
            public HashSet<string> Aliases { get; } = new(identifierComparer);

            public T Definition { get; } = definition;

            public string Name { get; } = name;
        }
    }

    private enum ProjectedDefinitionMutation : byte
    {
        None = 0,
        Missing = 1,
        Unknown = 2,
    }

    private sealed record ProjectedColumn(
        bool IsNullable,
        bool PreservesNullForExistingRows,
        bool IsComputed,
        bool AddedToExistingTable
    )
    {
        public static ProjectedColumn From(
            ExpectedColumnDefinition definition,
            bool addedToExistingTable
        ) => new(
            definition.IsNullable,
            SafeMigrationPreflightProjection.PreservesNullForExistingRows(definition.DefaultValue),
            definition.ComputedColumnSql is not null || definition.ComputedExpression is not null,
            addedToExistingTable);

        public static ProjectedColumn From(
            ColumnOperation operation,
            bool addedToExistingTable
        ) => new(
            operation.IsNullable,
            operation.DefaultValue is null && operation.DefaultValueSql is null,
            operation.ComputedColumnSql is not null,
            addedToExistingTable);

        public static ProjectedColumn Unknown { get; } = new(
            IsNullable: false,
            PreservesNullForExistingRows: false,
            IsComputed: true,
            AddedToExistingTable: false);
    }

    private sealed partial class ProjectedTable
    {
        private readonly List<string> _columnOrder;
        private readonly ISafeMigrationProviderObjectIdentityNormalizer? _objectIdentityNormalizer;
        private string _table;
        private string? _schema;
        private readonly string? _comment;

        public ProjectedTable(
            ExpectedTableDefinition definition,
            long dataMutationVersion,
            ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer
        )
        {
            _objectIdentityNormalizer = objectIdentityNormalizer;
            _table = definition.Table;
            _schema = definition.Schema;
            _comment = definition.Comment;
            _columnOrder = definition
                .Columns
                .Select(static value => value.Name)
                .ToList();

            var identifierComparer = new IdentifierComparer(objectIdentityNormalizer);

            Columns = definition.Columns.ToDictionary(static value => value.Name, identifierComparer);
            PrimaryKey = definition.PrimaryKey;

            UniqueConstraints = definition.UniqueConstraints.ToDictionary(
                static value => value.Name,
                identifierComparer);

            CheckConstraints = definition.CheckConstraints.ToDictionary(
                static value => value.Name,
                identifierComparer);

            ForeignKeys = definition.ForeignKeys.ToDictionary(static value => value.Name, identifierComparer);
            Indexes = new Dictionary<string, ExpectedIndexDefinition>(identifierComparer);
            DataMutationVersion = dataMutationVersion;
        }

        public ExpectedTableDefinition Definition =>
            new(
                _table,
                _columnOrder.Select(name => Columns[name]),
                _schema,
                _comment,
                PrimaryKey,
                UniqueConstraints.Values,
                CheckConstraints.Values,
                ForeignKeys.Values);

        public Dictionary<string, ExpectedColumnDefinition> Columns { get; }

        public long DataMutationVersion { get; }

        public string Table => _table;

        public string? Schema => _schema;

        public ExpectedPrimaryKeyDefinition? PrimaryKey { get; set; }

        public Dictionary<string, ExpectedUniqueConstraintDefinition> UniqueConstraints { get; }

        public Dictionary<string, ExpectedCheckConstraintDefinition> CheckConstraints { get; }

        public Dictionary<string, ExpectedForeignKeyDefinition> ForeignKeys { get; }

        public Dictionary<string, ExpectedIndexDefinition> Indexes { get; }

        private static void ReplaceValues<T>(
            Dictionary<string, T> dictionary,
            Func<T, T> transform
        )
        {
            foreach (var key in dictionary.Keys.ToArray())
            {
                dictionary[key] = transform(dictionary[key]);
            }
        }

        private string[] Rename(
            IReadOnlyList<string> values,
            string source,
            string target
        ) => values
            .Select(value => IdentifierEquals(_objectIdentityNormalizer, value, source) ? target : value)
            .ToArray();

        private bool SameIdentity(
            string leftTable,
            string? leftSchema,
            string rightTable,
            string? rightSchema
        ) => IdentifierEquals(_objectIdentityNormalizer, leftTable, rightTable)
            && StringComparer.Ordinal.Equals(
                NormalizeSchema(_objectIdentityNormalizer, leftSchema),
                NormalizeSchema(_objectIdentityNormalizer, rightSchema));

        private static ExpectedColumnDefinition Copy(
            ExpectedColumnDefinition value,
            string? name = null,
            string? computedColumnSql = null,
            SafeMigrationSqlExpression? computedExpression = null,
            bool replaceComputed = false
        ) => new(
            name ?? value.Name,
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
            replaceComputed ? computedColumnSql : computedColumnSql ?? value.ComputedColumnSql,
            value.IsStored,
            replaceComputed ? computedExpression : computedExpression ?? value.ComputedExpression)
        {
            ProviderAnnotations = value.ProviderAnnotations,
        };

        private static ExpectedPrimaryKeyDefinition Copy(
            ExpectedPrimaryKeyDefinition value,
            string? table = null,
            string? schema = null,
            IReadOnlyList<string>? columns = null
        ) => new(value.Name, table ?? value.Table, columns ?? value.Columns, schema ?? value.Schema);

        private static ExpectedUniqueConstraintDefinition Copy(
            ExpectedUniqueConstraintDefinition value,
            string? table = null,
            string? schema = null,
            IReadOnlyList<string>? columns = null
        ) => new(value.Name, table ?? value.Table, columns ?? value.Columns, schema ?? value.Schema);

        private static ExpectedCheckConstraintDefinition Copy(
            ExpectedCheckConstraintDefinition value,
            string? table = null,
            string? schema = null,
            string? sql = null,
            SafeMigrationSqlExpression? expression = null,
            bool replaceExpression = false
        )
        {
            var selectedSql = replaceExpression ? sql : sql ?? value.Sql;
            var selectedExpression = replaceExpression ? expression : expression ?? value.Expression;

            return selectedSql is not null
                ? new ExpectedCheckConstraintDefinition(
                    value.Name,
                    table ?? value.Table,
                    selectedSql,
                    schema ?? value.Schema)
                : ExpectedCheckConstraintDefinition.FromExpression(
                    value.Name,
                    table ?? value.Table,
                    selectedExpression ?? throw new InvalidOperationException("A check constraint has no expression."),
                    schema ?? value.Schema);
        }

        private static ExpectedForeignKeyDefinition Copy(
            ExpectedForeignKeyDefinition value,
            string? table = null,
            string? schema = null,
            IReadOnlyList<string>? columns = null,
            string? principalTable = null,
            string? principalSchema = null,
            IReadOnlyList<string>? principalColumns = null
        ) => new(
            value.Name,
            table ?? value.Table,
            columns ?? value.Columns,
            principalTable ?? value.PrincipalTable,
            principalColumns ?? value.PrincipalColumns,
            schema ?? value.Schema,
            principalSchema ?? value.PrincipalSchema,
            value.OnUpdate,
            value.OnDelete);

        private static ExpectedIndexDefinition Copy(
            ExpectedIndexDefinition value,
            string? name = null,
            string? table = null,
            string? schema = null,
            IEnumerable<ExpectedIndexKeyDefinition>? keys = null,
            string? filter = null,
            SafeMigrationSqlExpression? structuredFilter = null,
            bool replaceFilter = false
        ) => new(
            name ?? value.Name,
            table ?? value.Table,
            keys ?? value.Keys,
            schema ?? value.Schema,
            value.Unique,
            replaceFilter ? filter : filter ?? value.Filter,
            value.IncludedColumns,
            value.Method,
            value.NullsDistinct,
            replaceFilter ? structuredFilter : structuredFilter ?? value.StructuredFilter);

        private static ExpectedIndexKeyDefinition Copy(
            ExpectedIndexKeyDefinition value,
            string source,
            string target
        ) => new(
            value.Column is not null && StringComparer.Ordinal.Equals(value.Column, source) ? target : value.Column,
            expression: null,
            value.SortOrder,
            value.NullOrder,
            value.PrefixLength,
            value.Collation,
            value.OperatorClass,
            value.Expression is not null
                ? SafeMigrationSql.OpaqueAfterRename(value.Expression)
                : value.StructuredExpression is null
                    ? null
                    : SafeMigrationSqlExpressionInspector.RenameIdentifier(value.StructuredExpression, source, target));
    }
}
