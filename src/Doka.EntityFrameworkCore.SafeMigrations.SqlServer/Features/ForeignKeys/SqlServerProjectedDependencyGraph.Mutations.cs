namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerProjectedDependencyGraph
{
    /// <inheritdoc />
    public void ObserveAcceptedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        if (decision.Action is not (SafeMigrationAction.Apply or SafeMigrationAction.Repair))
        {
            return;
        }

        switch (operation.Intent)
        {
            case EnsureSchemaIntent schema:
                _missingSchemas.Remove(GetSchema(schema.Name));
                break;
            case DropSchemaIntent schema:
                _missingSchemas.Add(GetSchema(schema.Name));
                break;
            case EnsureTableIntent table:
                var tableId = GetTable(table.Definition.Table, table.Definition.Schema);

                _missingTables.Remove(tableId);
                _tableSchemas[tableId] = GetSchema(table.Definition.Schema ?? "dbo");
                _createdTables[tableId] = table.Definition;
                RegisterSchemaObject(
                    table.Definition.Schema,
                    table.Definition.Table,
                    new ConstraintOccupancy(tableId, 0, "U"));

                if (table.Definition.PrimaryKey is { } primaryKey)
                {
                    AddCandidateKey(tableId, primaryKey.Name, primaryKey.Columns);
                    RegisterConstraint(tableId, table.Definition.Schema, primaryKey.Name, "PK");
                }

                foreach (var unique in table.Definition.UniqueConstraints)
                {
                    AddCandidateKey(tableId, unique.Name, unique.Columns);
                    RegisterConstraint(tableId, table.Definition.Schema, unique.Name, "UQ");
                }

                foreach (var check in table.Definition.CheckConstraints)
                {
                    RegisterConstraint(tableId, table.Definition.Schema, check.Name, "C");
                }

                foreach (var foreignKey in table.Definition.ForeignKeys)
                {
                    Add(foreignKey);
                }

                break;
            case EnsureForeignKeyIntent foreignKey:
                Add(foreignKey.Definition);
                break;
            case DropForeignKeyIntent foreignKey:
                RemoveForeignKey(foreignKey.Table, foreignKey.Schema, foreignKey.Name);
                break;
            case DropTableIntent table:
                RemoveTable(table.Table, table.Schema);
                break;
            case RenameTableIntent table:
                _renameSafeTables.Add(GetTable(table.Name, table.Schema));
                RenameTable(table.Name, table.Schema, table.NewName ?? table.Name, table.NewSchema ?? table.Schema);
                break;
            case RenameColumnIntent column:
                RenameColumn(column.Table, column.Schema, column.Name, column.NewName);
                break;
            case DropColumnIntent column:
                RemoveColumn(column.Table, column.Schema, column.Name);
                break;
            case EnsureColumnIntent column:
                InvalidateColumnStorage(column.Table, column.Schema, column.Definition.Name);
                break;
            case AlterColumnIntent column:
                InvalidateColumnStorage(column.Table, column.Schema, column.Definition.Name);
                break;
            case EnsurePrimaryKeyIntent key:
                AddCandidateKey(
                    GetTable(key.Definition.Table, key.Definition.Schema),
                    key.Definition.Name,
                    key.Definition.Columns);
                RegisterConstraint(
                    GetTable(key.Definition.Table, key.Definition.Schema),
                    key.Definition.Schema,
                    key.Definition.Name,
                    "PK");
                break;
            case EnsureUniqueConstraintIntent key:
                AddCandidateKey(
                    GetTable(key.Definition.Table, key.Definition.Schema),
                    key.Definition.Name,
                    key.Definition.Columns);
                RegisterConstraint(
                    GetTable(key.Definition.Table, key.Definition.Schema),
                    key.Definition.Schema,
                    key.Definition.Name,
                    "UQ");
                break;
            case DropPrimaryKeyIntent key:
                _candidateKeys.Remove(GetTable(key.Table, key.Schema), key.Name);
                RemoveConstraint(GetTable(key.Table, key.Schema), key.Schema, key.Name, "PK");
                break;
            case DropUniqueConstraintIntent key:
                _candidateKeys.Remove(GetTable(key.Table, key.Schema), key.Name);
                RemoveConstraint(GetTable(key.Table, key.Schema), key.Schema, key.Name, "UQ");
                break;
            case EnsureCheckConstraintIntent check:
                RegisterConstraint(
                    GetTable(check.Definition.Table, check.Definition.Schema),
                    check.Definition.Schema,
                    check.Definition.Name,
                    "C");
                break;
            case DropCheckConstraintIntent check:
                RemoveConstraint(GetTable(check.Table, check.Schema), check.Schema, check.Name, "C");
                break;
            case EnsureIndexIntent index:
                AddCandidateIndex(index.Definition);
                break;
            case DropIndexIntent index:
                _candidateKeys.Remove(GetTable(index.Table, index.Schema), index.Name);
                break;
            case RenameIndexIntent index:
                _candidateKeys.Rename(GetTable(index.Table, index.Schema), index.Name, index.NewName);
                break;
        }
    }

    /// <inheritdoc />
    public void ObserveProviderOperation(
        MigrationOperation operation
    )
    {
        switch (operation)
        {
            case DropForeignKeyOperation foreignKey:
                RemoveForeignKey(foreignKey.Table, foreignKey.Schema, foreignKey.Name);
                break;
            case DropTableOperation table:
                RemoveTable(table.Name, table.Schema);
                break;
            case RenameTableOperation table:
                RenameTable(table.Name, table.Schema, table.NewName ?? table.Name, table.NewSchema ?? table.Schema);
                break;
            case EnsureSchemaOperation schema:
                _missingSchemas.Remove(GetSchema(schema.Name));
                break;
            case DropSchemaOperation schema:
                _missingSchemas.Add(GetSchema(schema.Name));
                break;
            case AddPrimaryKeyOperation key:
                AddCandidateKey(GetTable(key.Table, key.Schema), key.Name, key.Columns);
                RegisterConstraint(GetTable(key.Table, key.Schema), key.Schema, key.Name, "PK");
                break;
            case AddUniqueConstraintOperation key:
                AddCandidateKey(GetTable(key.Table, key.Schema), key.Name, key.Columns);
                RegisterConstraint(GetTable(key.Table, key.Schema), key.Schema, key.Name, "UQ");
                break;
            case DropPrimaryKeyOperation key:
                _candidateKeys.Remove(GetTable(key.Table, key.Schema), key.Name);
                RemoveConstraint(GetTable(key.Table, key.Schema), key.Schema, key.Name, "PK");
                break;
            case DropUniqueConstraintOperation key:
                _candidateKeys.Remove(GetTable(key.Table, key.Schema), key.Name);
                RemoveConstraint(GetTable(key.Table, key.Schema), key.Schema, key.Name, "UQ");
                break;
            case CreateIndexOperation { IsUnique: true, Filter: null } index:
                AddCandidateKey(GetTable(index.Table, index.Schema), index.Name, index.Columns);
                break;
            case DropIndexOperation { Table: not null } index:
                _candidateKeys.Remove(GetTable(index.Table, index.Schema), index.Name);
                break;
            case RenameIndexOperation { Table: not null } index:
                _candidateKeys.Rename(GetTable(index.Table, index.Schema), index.Name, index.NewName);
                break;
            case RenameColumnOperation column:
                RenameColumn(column.Table, column.Schema, column.Name, column.NewName);
                break;
            case DropColumnOperation column:
                RemoveColumn(column.Table, column.Schema, column.Name);
                break;
            case AddColumnOperation column:
                InvalidateColumnStorage(column.Table, column.Schema, column.Name);
                break;
            case AlterColumnOperation column:
                InvalidateColumnStorage(column.Table, column.Schema, column.Name);
                break;
            case AddCheckConstraintOperation check:
                RegisterConstraint(GetTable(check.Table, check.Schema), check.Schema, check.Name, "C");
                break;
            case DropCheckConstraintOperation check:
                RemoveConstraint(GetTable(check.Table, check.Schema), check.Schema, check.Name, "C");
                break;
            case CreateIndexOperation or InsertDataOperation or UpdateDataOperation or DeleteDataOperation:
                break;
            default:
                // WHY: Ordinary SQL or an unproven provider effect can change
                // FKs and triggers. Retaining the original graph would authorize
                // a later DROP or cascade based on evidence that no longer holds.
                _unknown = true;
                break;
        }
    }

    private void Add(
        ExpectedForeignKeyDefinition definition
    )
    {
        var dependent = GetTable(definition.Table, definition.Schema);
        var key = new ForeignKeyKey(dependent, definition.Name);
        if (!_foreignKeyNames.TryGetValue(key, out var id))
        {
            id = _nextForeignKeyId--;
            _foreignKeyNames[key] = id;
        }

        _foreignKeys[id] = new ForeignKeyEdge(
            dependent,
            GetTable(definition.PrincipalTable, definition.PrincipalSchema),
            definition.OnDelete,
            definition.OnUpdate);

        RegisterConstraint(dependent, definition.Schema, definition.Name, "F");
    }

    private void RemoveForeignKey(
        string table,
        string? schema,
        string name
    )
    {
        RemoveConstraint(GetTable(table, schema), schema, name, "F");
        if (_foreignKeyNames.TryGetValue(new ForeignKeyKey(GetTable(table, schema), name), out var id))
        {
            _foreignKeys.Remove(id);
        }
    }

    /// <summary>Tracks only authored names needed by later standalone FK collision checks.</summary>
    private void RegisterConstraint(
        int owner,
        string? schema,
        string name,
        string kind
    ) => RegisterSchemaObject(schema, name, new ConstraintOccupancy(0, owner, kind));

    private void RegisterSchemaObject(
        string? schema,
        string name,
        ConstraintOccupancy occupancy
    )
    {
        var key = new SchemaObjectKey(schema ?? "dbo", name);
        if (_standaloneObjectNames.Contains(name))
        {
            // WHY: An authored alias cannot replace a captured physical owner.
            // Only that owner's accepted DROP may clear an occupied name;
            // retaining it is conservative when a prior repair reused aliases.
            _constraintNames.TryAdd(key, occupancy);
        }
    }

    /// <summary>Discharges namespace occupancy only for the accepted physical owner and constraint kind.</summary>
    private void RemoveConstraint(
        int owner,
        string? schema,
        string name,
        string kind
    )
    {
        var key = new SchemaObjectKey(schema ?? "dbo", name);
        if (_constraintNames.TryGetValue(key, out var occupancy)
            && occupancy.Owner == owner
            && occupancy.Kind == kind)
        {
            _constraintNames.Remove(key);
        }
    }

    private void RemoveTable(
        string table,
        string? schema
    )
    {
        var id = GetTable(table, schema);

        foreach (var name in _constraintNames
                     .Where(pair => pair.Value.Owner == id || pair.Value.Object == id)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _constraintNames.Remove(name);
        }

        _missingTables.Add(id);
        _createdTables.Remove(id);
        _renameSafeTables.Remove(id);
        _triggers.Remove(id);
        _candidateKeys.RemoveTable(id);
        foreach (var key in _columns
                     .Keys
                     .Where(key => key.Table == id)
                     .ToArray())
        {
            _columns.Remove(key);
            _columnIds.Remove(key);
        }

        foreach (var pair in _foreignKeys.Where(pair => pair.Value.Dependent == id).ToArray())
        {
            _foreignKeys.Remove(pair.Key);
        }
    }

    private void AddCandidateIndex(
        ExpectedIndexDefinition index
    )
    {
        if (index is { Unique: true, Filter: null, StructuredFilter: null }
            && index.Keys.All(static key => key.Column is not null))
        {
            AddCandidateKey(
                GetTable(index.Table, index.Schema),
                index.Name,
                index
                    .Keys
                    .Select(static key => key.Column!)
                    .ToArray());
        }
    }

    private void AddCandidateKey(
        int table,
        string name,
        IReadOnlyList<string> columns
    )
    {
        Span<int> columnIds = stackalloc int[32];

        if (columns.Count > columnIds.Length)
        {
            return;
        }

        ResolveColumnIds(table, columns, columnIds);
        _candidateKeys.Add(table, name, columns, columnIds[..columns.Count]);
    }

    private void ResolveColumnIds(
        int table,
        IReadOnlyList<string> columns,
        Span<int> columnIds
    )
    {
        // WHY: Write each resolved physical column identity to its original key position.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            columnIds[ordinal] = _columnIds.GetValueOrDefault(new ColumnKey(table, columns[ordinal]));
        }
    }

    private void RenameColumn(
        string table,
        string? schema,
        string name,
        string newName
    )
    {
        var id = GetTable(table, schema);
        var source = new ColumnKey(id, name);
        var target = new ColumnKey(id, newName);

        if (_columns.Remove(source, out var safety))
        {
            _columns[target] = safety;
        }

        var columnId = _columnIds.GetValueOrDefault(source);

        if (_columnIds.Remove(source))
        {
            _columnIds[target] = columnId;
        }

        _candidateKeys.RenameColumn(id, name, newName, columnId);
    }

    private void RemoveColumn(
        string table,
        string? schema,
        string name
    )
    {
        var id = GetTable(table, schema);
        var key = new ColumnKey(id, name);
        var columnId = _columnIds.GetValueOrDefault(key);

        _columns.Remove(key);
        _columnIds.Remove(key);
        _candidateKeys.RemoveColumn(id, name, columnId);
    }

    private void InvalidateColumnStorage(
        string table,
        string? schema,
        string name
    )
    {
        var id = GetTable(table, schema);

        if (_columnIds.TryGetValue(new ColumnKey(id, name), out var columnId))
        {
            _invalidatedStorage.Add(new ColumnIdentity(id, columnId));
        }
    }

    private void RenameTable(
        string table,
        string? schema,
        string newTable,
        string? newSchema
    )
    {
        var id = GetTable(table, schema);
        if (table == newTable
            && (schema ?? "dbo") == (newSchema ?? "dbo"))
        {
            return;
        }

        var oldName = new SchemaObjectKey(schema ?? "dbo", table);
        if (_constraintNames.TryGetValue(oldName, out var occupancy)
            && occupancy.Object == id
            && occupancy.Kind == "U")
        {
            _constraintNames.Remove(oldName);
        }

        RegisterSchemaObject(newSchema, newTable, new ConstraintOccupancy(id, 0, "U"));
        if ((schema ?? "dbo") != (newSchema ?? "dbo"))
        {
            // WHY: ALTER SCHEMA transfers table-owned constraints as well as
            // the table. Move accepted ownership rather than resurrecting
            // names from the original authored definition after a prior DROP.
            foreach (var pair in _constraintNames
                         .Where(pair => pair.Value.Owner == id)
                         .ToArray())
            {
                _constraintNames.Remove(pair.Key);
                RegisterSchemaObject(newSchema, pair.Key.Name, pair.Value);
            }
        }

        _tables[new TableKey(newSchema ?? "dbo", newTable)] = id;
        _tableSchemas[id] = GetSchema(newSchema ?? "dbo");
        var missingSourceId = _nextTableId--;

        _tables[new TableKey(schema ?? "dbo", table)] = missingSourceId;
        _missingTables.Add(missingSourceId);
    }
}
