namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private readonly ISafeMigrationProviderOperationProjection? _providerOperationProjection;
    private readonly Dictionary<TableKey, ProjectedTable> _tables = [];

    // WHY: Strict table projections retain complete definitions. This second view
    // records only prerequisites proven by earlier convergence operations, so
    // a later operation cannot infer safety from an object that was rejected.
    private readonly Dictionary<TableKey, ProjectedPrerequisites> _prerequisites = [];
    private readonly HashSet<IndexKey> _droppedIndexes = [];
    private readonly HashSet<TableKey> _projectedDataMutationTables = [];
    private readonly Dictionary<TableKey, HashSet<string>> _projectedModelManagedUniqueKeys = [];
    private readonly Dictionary<ColumnKey, ExpectedColumnDefinition> _projectedColumnDefinitions = [];
    private readonly HashSet<ColumnKey> _projectedMissingColumns = [];
    private readonly HashSet<ColumnKey> _projectedUnknownColumns = [];
    private readonly HashSet<TableKey> _projectedMissingTables = [];
    private readonly HashSet<TableKey> _projectedUnknownTableStructures = [];
    private readonly HashSet<TableKey> _projectedStructurallyModifiedTables = [];
    private readonly Dictionary<TableKey, HashSet<string>> _projectedChangedColumns = [];
    private bool _hasOpaqueProviderPostcondition;
    private long _providerDataMutationVersion;

    public SafeMigrationPreflightProjection(
        ISafeMigrationProviderOperationProjection? providerOperationProjection = null
    ) => _providerOperationProjection = providerOperationProjection;

    public SafeMigrationProviderAnalysis Project(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(liveAnalysis);

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
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Action is SafeMigrationAction.RejectDifferent
            or SafeMigrationAction.RejectUnsupported
            or SafeMigrationAction.RejectDataBlocked
            or SafeMigrationAction.RejectPrerequisiteMissing)
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
                Observe(value, decision);
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
                Observe(value, decision);
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
                Observe(value, decision);
                break;
            case DropUniqueConstraintIntent value:
                Observe(value, decision);
                break;
            case EnsureCheckConstraintIntent value:
                Observe(value, decision);
                break;
            case DropCheckConstraintIntent value:
                Observe(value, decision);
                break;
            case EnsureForeignKeyIntent value:
                Observe(value, decision);
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
            columns = new HashSet<string>(StringComparer.Ordinal);
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

    private static bool SameTable(
        ColumnKey key,
        string table,
        string? schema
    ) => StringComparer.Ordinal.Equals(key.Table, table)
        && StringComparer.Ordinal.Equals(key.Schema, schema);

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

    private static string ModelManagedUniqueKeyFingerprint(
        IReadOnlyList<string> columns
    )
    {
        using var writer = new CanonicalHashWriter();

        writer.Add(columns.Count);
        foreach (var column in columns)
        {
            writer.Add(column);
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

    private sealed record ProjectedColumnState(
        string Name,
        ExpectedColumnDefinition? Definition,
        bool IsMissing,
        bool IsUnknown
    );

    private sealed class ProjectedPrerequisites(
        bool newlyCreated,
        long dataMutationVersion
    )
    {
        public Dictionary<string, ProjectedColumn> Columns { get; } = new(StringComparer.Ordinal);

        public long DataMutationVersion { get; } = dataMutationVersion;

        public bool NewlyCreated { get; } = newlyCreated;
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
        private string _table;
        private string? _schema;
        private readonly string? _comment;

        public ProjectedTable(
            ExpectedTableDefinition definition,
            long dataMutationVersion
        )
        {
            _table = definition.Table;
            _schema = definition.Schema;
            _comment = definition.Comment;
            _columnOrder = definition
                .Columns
                .Select(static value => value.Name)
                .ToList();

            Columns = definition.Columns.ToDictionary(static value => value.Name, StringComparer.Ordinal);
            PrimaryKey = definition.PrimaryKey;

            UniqueConstraints = definition.UniqueConstraints.ToDictionary(
                static value => value.Name,
                StringComparer.Ordinal);

            CheckConstraints = definition.CheckConstraints.ToDictionary(
                static value => value.Name,
                StringComparer.Ordinal);

            ForeignKeys = definition.ForeignKeys.ToDictionary(static value => value.Name, StringComparer.Ordinal);
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

        public Dictionary<string, ExpectedIndexDefinition> Indexes { get; } = new(StringComparer.Ordinal);

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

        private static string[] Rename(
            IReadOnlyList<string> values,
            string source,
            string target
        ) => values
            .Select(value => StringComparer.Ordinal.Equals(value, source) ? target : value)
            .ToArray();

        private static bool SameIdentity(
            string leftTable,
            string? leftSchema,
            string rightTable,
            string? rightSchema
        ) => StringComparer.Ordinal.Equals(leftTable, rightTable)
            && StringComparer.Ordinal.Equals(leftSchema, rightSchema);

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
