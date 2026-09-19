namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>
/// Captures model-owned and operation-projected artifacts required for safe
/// SQLite table rebuilds.
/// </summary>
internal sealed class SqliteRebuildArtifactContract
{
    private static readonly StringComparer s_identifierComparer = SqliteIdentifierComparer.Instance;

    private readonly IReadOnlyDictionary<string, AddedTableArtifacts> _addedArtifacts;
    private readonly IReadOnlyList<MigrationOperation> _operations;
    private readonly IReadOnlyDictionary<string, RemovedTableArtifacts> _removedArtifacts;
    private readonly IReadOnlySet<string> _removedTables;
    private readonly IReadOnlyDictionary<string, RebuildTableContract> _tables;

    private SqliteRebuildArtifactContract(
        IReadOnlyDictionary<string, RebuildTableContract> tables,
        IReadOnlyDictionary<string, AddedTableArtifacts> addedArtifacts,
        IReadOnlyDictionary<string, RemovedTableArtifacts> removedArtifacts,
        IReadOnlySet<string> removedTables,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        _tables = tables;
        _addedArtifacts = addedArtifacts;
        _operations = operations;
        _removedArtifacts = removedArtifacts;
        _removedTables = removedTables;
    }

    /// <summary>Creates a rebuild contract from the target model and ordered operations.</summary>
    public static SqliteRebuildArtifactContract FromModel(
        IModel? model,
        IReadOnlyList<MigrationOperation>? operations = null
    )
    {
        var tables = new Dictionary<string, RebuildTableContract>(s_identifierComparer);
        if (model is not null)
        {
            foreach (var table in model.GetRelationalModel().Tables)
            {
                if (table.Schema is not null
                    && !s_identifierComparer.Equals(table.Schema, "main"))
                {
                    continue;
                }

                tables[table.Name] = new RebuildTableContract(
                    table.Columns.ToDictionary(static column => column.Name, s_identifierComparer),
                    table.Indexes.ToDictionary(static index => index.Name, s_identifierComparer),
                    table.PrimaryKey?.Name,
                    table
                        .PrimaryKey
                        ?.Columns
                        .Select(static column => column.Name)
                        .ToArray()
                    ?? [],
                    table
                        .UniqueConstraints
                        .Where(static constraint => !constraint.GetIsPrimaryKey())
                        .Select(static constraint => new NamedColumns(
                            constraint.Name,
                            constraint
                                .Columns
                                .Select(static column => column.Name)
                                .ToArray()))
                        .ToArray(),
                    table
                        .ForeignKeyConstraints
                        .Select(static foreignKey => new ForeignKeyShape(
                            foreignKey.Name,
                            foreignKey.PrincipalTable.Name,
                            foreignKey
                                .Columns
                                .Select(static column => column.Name)
                                .ToArray(),
                            foreignKey
                                .PrincipalColumns
                                .Select(static column => column.Name)
                                .ToArray(),
                            ReferentialAction.NoAction,
                            foreignKey.OnDeleteAction))
                        .ToArray(),
                    table
                        .CheckConstraints
                        .Select(static check => new CheckShape(check.Name, check.Sql))
                        .ToArray());
            }
        }

        var operationSnapshot = operations?.ToArray() ?? [];

        return new SqliteRebuildArtifactContract(
            tables,
            ReadAddedArtifacts(operationSnapshot),
            ReadRemovedArtifacts(operationSnapshot),
            ReadRemovedTables(operationSnapshot),
            operationSnapshot);
    }

    /// <summary>Creates a bounded operation-only contract when no target model applies.</summary>
    public static SqliteRebuildArtifactContract FromOperations(
        IReadOnlyList<MigrationOperation>? operations
    )
    {
        var operationSnapshot = operations?.ToArray() ?? [];

        return new SqliteRebuildArtifactContract(
            new Dictionary<string, RebuildTableContract>(s_identifierComparer),
            new Dictionary<string, AddedTableArtifacts>(s_identifierComparer),
            new Dictionary<string, RemovedTableArtifacts>(s_identifierComparer),
            new HashSet<string>(s_identifierComparer),
            operationSnapshot);
    }

    /// <summary>Determines whether projected dependents still reference a principal table.</summary>
    public bool HasUnresolvedIncomingForeignKey(
        SqliteCatalogSnapshot snapshot,
        string principalTable,
        SafeMigrationOperation currentOperation
    )
    {
        var tables = snapshot.Tables.Keys.ToHashSet(s_identifierComparer);
        var foreignKeys = snapshot
            .Tables
            .Values
            .SelectMany(table => table.ForeignKeys.Select(foreignKey => new ProjectedForeignKey(
                table.Name,
                foreignKey.Name,
                foreignKey.PrincipalTable)))
            .ToList();

        foreach (var operation in _operations)
        {
            if (ReferenceEquals(operation, currentOperation))
            {
                break;
            }

            switch (operation)
            {
                case SafeMigrationOperation { Intent: EnsureTableIntent value }:
                    if (tables.Add(value.Definition.Table))
                    {
                        foreach (var foreignKey in value.Definition.ForeignKeys)
                        {
                            AddProjectedForeignKey(foreignKeys, foreignKey);
                        }
                    }

                    break;
                case CreateTableOperation value:
                    if (tables.Add(value.Name))
                    {
                        foreach (var foreignKey in value.ForeignKeys)
                        {
                            AddProjectedForeignKey(foreignKeys, foreignKey);
                        }
                    }

                    break;
                case SafeMigrationOperation { Intent: EnsureForeignKeyIntent value }:
                    AddProjectedForeignKey(foreignKeys, value.Definition);
                    break;
                case AddForeignKeyOperation value:
                    AddProjectedForeignKey(foreignKeys, value);
                    break;
                case SafeMigrationOperation { Intent: DropForeignKeyIntent value }:
                    RemoveProjectedForeignKey(foreignKeys, value.Table, value.Name);
                    break;
                case DropForeignKeyOperation value:
                    RemoveProjectedForeignKey(foreignKeys, value.Table, value.Name);
                    break;
                case SafeMigrationOperation { Intent: RenameTableIntent value }:
                    RenameProjectedTable(foreignKeys, tables, value.Name, value.NewName ?? value.Name);
                    break;
                case RenameTableOperation value:
                    RenameProjectedTable(foreignKeys, tables, value.Name, value.NewName ?? value.Name);
                    break;
                case SafeMigrationOperation { Intent: DropTableIntent value }:
                    RemoveProjectedTable(foreignKeys, tables, value.Table);
                    break;
                case DropTableOperation value:
                    RemoveProjectedTable(foreignKeys, tables, value.Name);
                    break;
            }
        }

        return tables.Contains(principalTable)
            && foreignKeys.Any(foreignKey => tables.Contains(foreignKey.Table)
                && !s_identifierComparer.Equals(foreignKey.Table, principalTable)
                && s_identifierComparer.Equals(foreignKey.PrincipalTable, principalTable));
    }

    private static void AddProjectedForeignKey(
        List<ProjectedForeignKey> foreignKeys,
        ExpectedForeignKeyDefinition definition
    )
    {
        RemoveProjectedForeignKey(foreignKeys, definition.Table, definition.Name);
        foreignKeys.Add(new ProjectedForeignKey(definition.Table, definition.Name, definition.PrincipalTable));
    }

    private static void AddProjectedForeignKey(
        List<ProjectedForeignKey> foreignKeys,
        AddForeignKeyOperation operation
    )
    {
        RemoveProjectedForeignKey(foreignKeys, operation.Table, operation.Name);
        foreignKeys.Add(new ProjectedForeignKey(operation.Table, operation.Name, operation.PrincipalTable));
    }

    private static void RemoveProjectedForeignKey(
        List<ProjectedForeignKey> foreignKeys,
        string table,
        string name
    ) => foreignKeys.RemoveAll(foreignKey => s_identifierComparer.Equals(foreignKey.Table, table)
        && foreignKey.Name is not null
        && s_identifierComparer.Equals(foreignKey.Name, name));

    private static void RenameProjectedTable(
        List<ProjectedForeignKey> foreignKeys,
        HashSet<string> tables,
        string source,
        string target
    )
    {
        if (tables.Remove(source))
        {
            _ = tables.Add(target);
        }

        for (var index = 0; index < foreignKeys.Count; index++)
        {
            var foreignKey = foreignKeys[index];
            foreignKeys[index] = foreignKey with
            {
                Table = s_identifierComparer.Equals(foreignKey.Table, source) ? target : foreignKey.Table,
                PrincipalTable = s_identifierComparer.Equals(foreignKey.PrincipalTable, source)
                    ? target
                    : foreignKey.PrincipalTable,
            };
        }
    }

    private static void RemoveProjectedTable(
        List<ProjectedForeignKey> foreignKeys,
        HashSet<string> tables,
        string table
    )
    {
        _ = tables.Remove(table);
        foreignKeys.RemoveAll(foreignKey => s_identifierComparer.Equals(foreignKey.Table, table));
    }

    /// <summary>Determines whether a live foreign key survives the projected operation stream.</summary>
    public bool RetainsForeignKey(
        string table,
        SqliteForeignKeySnapshot foreignKey
    ) => !_removedTables.Contains(table) && !IsRemovedForeignKey(table, foreignKey.Name);

    /// <summary>Resolves a projected table name back to its current live catalog name.</summary>
    public string ResolveLiveTableName(
        string projectedTable,
        SafeMigrationOperation currentOperation
    )
    {
        var currentIndex = -1;
        for (var index = 0; index < _operations.Count; index++)
        {
            if (ReferenceEquals(_operations[index], currentOperation))
            {
                currentIndex = index;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return projectedTable;
        }

        var result = projectedTable;
        for (var index = currentIndex - 1; index >= 0; index--)
        {
            switch (_operations[index])
            {
                case SafeMigrationOperation { Intent: RenameTableIntent value }
                    when s_identifierComparer.Equals(value.NewName ?? value.Name, result):
                    result = value.Name;
                    break;
                case RenameTableOperation value when s_identifierComparer.Equals(value.NewName ?? value.Name, result):
                    result = value.Name;
                    break;
            }
        }

        return result;
    }

    /// <summary>Gets the first live artifact that prevents a lossless table rebuild.</summary>
    public string? GetUnsupportedRebuildFeature(
        SqliteCatalogSnapshot snapshot,
        string table,
        Func<SqliteColumnSnapshot, IColumn, bool> columnMatches,
        Func<SqliteIndexSnapshot, ITableIndex, bool> indexMatches
    )
    {
        ArgumentNullException.ThrowIfNull(columnMatches);
        ArgumentNullException.ThrowIfNull(indexMatches);

        if (!snapshot.Tables.TryGetValue(table, out var tableSnapshot))
        {
            return null;
        }

        if (tableSnapshot.IsStrict)
        {
            return "strict_table_rebuild";
        }

        if (tableSnapshot.WithoutRowId)
        {
            return "without_rowid_table_rebuild";
        }

        if (snapshot.OtherObjects.Any(value => value.Type == "trigger"
                && (s_identifierComparer.Equals(value.Table, table)
                    || SqliteSqlIdentifierScanner.ReferencesIdentifier(value.Sql, table))))
        {
            return "table_rebuild_trigger";
        }

        if (snapshot.OtherObjects.Any(value => value.Type == "view"
                && SqliteSqlIdentifierScanner.ReferencesIdentifier(value.Sql, table)))
        {
            return "table_rebuild_view";
        }

        if (!_tables.TryGetValue(table, out var expected))
        {
            return "table_rebuild_model_missing";
        }

        _ = _addedArtifacts.TryGetValue(table, out var added);
        _ = _removedArtifacts.TryGetValue(table, out var removed);

        if (tableSnapshot.HasUnmodeledConstraintOptions)
        {
            return "table_rebuild_provider_constraint_option";
        }

        foreach (var actual in tableSnapshot.Columns.Values)
        {
            if (!expected.Columns.TryGetValue(actual.Name, out var expectedColumn))
            {
                if (removed?.Columns.Contains(actual.Name) != true)
                {
                    return "table_rebuild_unmanaged_column";
                }

                continue;
            }

            if (!columnMatches(actual, expectedColumn)
                && !IsReplacement(removed?.Columns, added?.Columns, actual.Name))
            {
                return "table_rebuild_column_drift";
            }
        }

        if (expected.Columns.Keys.Any(column => !tableSnapshot.Columns.ContainsKey(column)
                && added?.Columns.Contains(column) != true))
        {
            return "table_rebuild_unmodeled_target_column";
        }

        foreach (var actual in tableSnapshot.Indexes.Where(static index => index.Origin == "c"))
        {
            if (!expected.Indexes.TryGetValue(actual.Name, out var expectedIndex))
            {
                if (removed?.Indexes.Contains(actual.Name) != true)
                {
                    return "table_rebuild_unmanaged_index";
                }

                continue;
            }

            if (!indexMatches(actual, expectedIndex)
                && !IsReplacement(removed?.Indexes, added?.Indexes, actual.Name))
            {
                return "table_rebuild_index_drift";
            }
        }

        if (expected.Indexes.Keys.Any(index =>
                tableSnapshot.Indexes.All(actual => !s_identifierComparer.Equals(actual.Name, index))
                && added?.Indexes.Contains(index) != true))
        {
            return "table_rebuild_unmodeled_target_index";
        }

        var primaryKeySemanticsMatch = SqliteCatalogEquivalence.IdentifiersEqual(
            tableSnapshot.PrimaryKeyColumns,
            expected.PrimaryKeyColumns);

        if (tableSnapshot.PrimaryKeyColumns.Count > 0
            && !primaryKeySemanticsMatch
            && removed?.PrimaryKey != true)
        {
            return "table_rebuild_unmanaged_primary_key";
        }

        if (primaryKeySemanticsMatch
            && tableSnapshot.PrimaryKeyColumns.Count > 0
            && !PrimaryKeyMatches(tableSnapshot, expected)
            && !(removed?.PrimaryKey == true
                && expected.PrimaryKeyName is not null
                && added?.PrimaryKeys.Contains(expected.PrimaryKeyName) == true))
        {
            return "table_rebuild_primary_key_drift";
        }

        foreach (var unique in tableSnapshot.UniqueConstraints)
        {
            var expectedUnique = expected.UniqueConstraints.FirstOrDefault(shape =>
                SqliteCatalogEquivalence.IdentifiersEqual(unique.Columns, shape.Columns));

            if (expectedUnique is null)
            {
                if (unique.Name is null
                    || removed?.UniqueConstraints.Contains(unique.Name) != true)
                {
                    return "table_rebuild_unmanaged_unique_constraint";
                }

                continue;
            }

            if (!UniqueConstraintMatches(unique, expectedUnique, expected.Columns)
                && !IsConstraintReplacement(
                    removed?.UniqueConstraints,
                    added?.UniqueConstraints,
                    unique.Name,
                    expectedUnique.Name))
            {
                return "table_rebuild_unique_constraint_drift";
            }
        }

        foreach (var foreignKey in tableSnapshot.ForeignKeys)
        {
            var expectedForeignKey = expected.ForeignKeys.FirstOrDefault(shape =>
                ForeignKeySemanticsMatch(snapshot, foreignKey, shape));

            if (expectedForeignKey is null)
            {
                if (foreignKey.Name is null
                    || removed?.ForeignKeys.Contains(foreignKey.Name) != true)
                {
                    return "table_rebuild_unmanaged_foreign_key";
                }

                continue;
            }

            if (!ConstraintNamesEqual(foreignKey.Name, expectedForeignKey.Name)
                && !IsConstraintReplacement(
                    removed?.ForeignKeys,
                    added?.ForeignKeys,
                    foreignKey.Name,
                    expectedForeignKey.Name))
            {
                return "table_rebuild_foreign_key_drift";
            }
        }

        foreach (var check in tableSnapshot.Checks)
        {
            var expectedCheck = expected.Checks.FirstOrDefault(shape => SqlEquivalent(check.Expression, shape.Sql));
            if (expectedCheck is null)
            {
                if (check.Name is null
                    || removed?.Checks.Contains(check.Name) != true)
                {
                    return "table_rebuild_unmanaged_check_constraint";
                }

                continue;
            }

            if (!ConstraintNamesEqual(check.Name, expectedCheck.Name)
                && !IsConstraintReplacement(removed?.Checks, added?.Checks, check.Name, expectedCheck.Name))
            {
                return "table_rebuild_check_constraint_drift";
            }
        }

        if (expected.PrimaryKeyColumns.Count > 0
            && !primaryKeySemanticsMatch
            && (expected.PrimaryKeyName is null || added?.PrimaryKeys.Contains(expected.PrimaryKeyName) != true))
        {
            return "table_rebuild_unmodeled_target_primary_key";
        }

        if (expected.UniqueConstraints.Any(unique =>
                tableSnapshot.UniqueConstraints.All(actual =>
                    !SqliteCatalogEquivalence.IdentifiersEqual(actual.Columns, unique.Columns))
                && added?.UniqueConstraints.Contains(unique.Name) != true))
        {
            return "table_rebuild_unmodeled_target_unique_constraint";
        }

        if (expected.ForeignKeys.Any(foreignKey => tableSnapshot.ForeignKeys.All(actual =>
                    !SqliteCatalogEquivalence.ForeignKeyMatches(
                        snapshot,
                        actual,
                        foreignKey.PrincipalTable,
                        foreignKey.Columns,
                        foreignKey.PrincipalColumns,
                        foreignKey.OnUpdate,
                        foreignKey.OnDelete))
                && added?.ForeignKeys.Contains(foreignKey.Name) != true))
        {
            return "table_rebuild_unmodeled_target_foreign_key";
        }

        if (expected.Checks.Any(check =>
                tableSnapshot.Checks.All(actual => !SqlEquivalent(actual.Expression, check.Sql))
                && (check.Name is null || added?.Checks.Contains(check.Name) != true)))
        {
            return "table_rebuild_unmodeled_target_check_constraint";
        }

        return null;
    }

    private static bool PrimaryKeyMatches(
        SqliteTableSnapshot actual,
        RebuildTableContract expected
    ) => ConstraintNamesEqual(actual.PrimaryKeyName, expected.PrimaryKeyName)
        && KeyShapeMatches(actual.PrimaryKeyKeys, expected.PrimaryKeyColumns, expected.Columns);

    private static bool UniqueConstraintMatches(
        SqliteUniqueSnapshot actual,
        NamedColumns expected,
        IReadOnlyDictionary<string, IColumn> expectedColumns
    ) => ConstraintNamesEqual(actual.Name, expected.Name)
        && KeyShapeMatches(actual.Keys, expected.Columns, expectedColumns);

    private static bool KeyShapeMatches(
        IReadOnlyList<SqliteIndexKeySnapshot> actual,
        IReadOnlyList<string> expected,
        IReadOnlyDictionary<string, IColumn> expectedColumns
    )
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }

        for (var ordinal = 0; ordinal < actual.Count; ordinal++)
        {
            var actualKey = actual[ordinal];
            if (!expectedColumns.TryGetValue(expected[ordinal], out var expectedColumn)
                || actualKey.Expression is not null
                || actualKey.Descending
                || !s_identifierComparer.Equals(actualKey.Column, expected[ordinal])
                || !s_identifierComparer.Equals(actualKey.Collation, expectedColumn.Collation ?? "BINARY"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ForeignKeySemanticsMatch(
        SqliteCatalogSnapshot snapshot,
        SqliteForeignKeySnapshot actual,
        ForeignKeyShape expected
    ) => s_identifierComparer.Equals(actual.Match, "NONE")
        && SqliteCatalogEquivalence.ForeignKeyMatches(
            snapshot,
            actual,
            expected.PrincipalTable,
            expected.Columns,
            expected.PrincipalColumns,
            expected.OnUpdate,
            expected.OnDelete);

    private static bool ConstraintNamesEqual(
        string? left,
        string? right
    ) => left is null ? right is null : right is not null && s_identifierComparer.Equals(left, right);

    private static bool IsConstraintReplacement(
        HashSet<string>? removed,
        HashSet<string>? added,
        string? actualName,
        string? expectedName
    ) => actualName is not null
        && expectedName is not null
        && removed?.Contains(actualName) == true
        && added?.Contains(expectedName) == true;

    /// <summary>Determines whether an expression references columns not yet materialized.</summary>
    public bool HasUnmaterializedReferencedColumns(
        SqliteCatalogSnapshot snapshot,
        string table,
        string expression
    )
    {
        if (!_tables.TryGetValue(table, out var expected)
            || !snapshot.Tables.TryGetValue(table, out var actual))
        {
            return false;
        }

        return expected.Columns.Keys.Any(column => !actual.Columns.ContainsKey(column)
            && SqliteSqlIdentifierScanner.ReferencesIdentifier(expression, column));
    }

    /// <summary>Determines whether projected nullable columns make a future foreign key safely missing.</summary>
    public bool CanTreatProjectedForeignKeyAsMissing(
        SqliteCatalogSnapshot snapshot,
        ExpectedForeignKeyDefinition definition
    )
    {
        if (!snapshot.Tables.TryGetValue(definition.Table, out var dependent)
            || !snapshot.Tables.TryGetValue(definition.PrincipalTable, out var principal)
            || !_tables.TryGetValue(definition.Table, out var expectedDependent)
            || !_addedArtifacts.TryGetValue(definition.Table, out var added)
            || definition.PrincipalColumns.Any(column => !principal.Columns.ContainsKey(column))
            || !HasCandidateKey(principal, definition.PrincipalColumns))
        {
            return false;
        }

        var hasNullPreservingColumn = false;
        for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
        {
            var column = definition.Columns[ordinal];
            if (dependent.Columns.ContainsKey(column))
            {
                continue;
            }

            if (!added.Columns.Contains(column)
                || !expectedDependent.Columns.TryGetValue(column, out var expectedColumn))
            {
                return false;
            }

            hasNullPreservingColumn |= expectedColumn.IsNullable;
        }

        // WHY: Existing rows cannot violate a composite foreign key when at
        // least one newly materialized component is NULL. Other shapes require
        // a live post-column data probe and therefore remain fail-closed.

        return hasNullPreservingColumn;
    }

    private static bool HasCandidateKey(
        SqliteTableSnapshot table,
        IReadOnlyList<string> columns
    ) => SqliteCatalogEquivalence.IdentifiersEqual(table.PrimaryKeyColumns, columns)
        || table.UniqueConstraints.Any(unique => SqliteCatalogEquivalence.IdentifiersEqual(unique.Columns, columns))
        || table.Indexes.Any(index => index is { Unique: true, Partial: false }
            && index
                .Keys
                .Where(static key => key.IsKey)
                .All(static key => key.Column is not null)
            && SqliteCatalogEquivalence.IdentifiersEqual(
                index
                    .Keys
                    .Where(static key => key.IsKey)
                    .OrderBy(static key => key.Ordinal)
                    .Select(static key => key.Column!)
                    .ToArray(),
                columns));

    private static Dictionary<string, AddedTableArtifacts> ReadAddedArtifacts(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var result = new Dictionary<string, AddedTableArtifacts>(s_identifierComparer);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case SafeMigrationOperation { Intent: EnsureColumnIntent value }:
                    _ = Add(result, value.Table)
                        .Columns
                        .Add(value.Definition.Name);
                    break;
                case SafeMigrationOperation { Intent: RenameColumnIntent value }:
                    _ = Add(result, value.Table)
                        .Columns
                        .Add(value.NewName);
                    break;
                case SafeMigrationOperation { Intent: AlterColumnIntent value }:
                    _ = Add(result, value.Table)
                        .Columns
                        .Add(value.Definition.Name);
                    break;
                case SafeMigrationOperation { Intent: EnsureIndexIntent value }:
                    _ = Add(result, value.Definition.Table)
                        .Indexes
                        .Add(value.Definition.Name);
                    break;
                case SafeMigrationOperation { Intent: RenameIndexIntent value }:
                    _ = Add(result, value.Table)
                        .Indexes
                        .Add(value.NewName);
                    break;
                case SafeMigrationOperation { Intent: EnsurePrimaryKeyIntent value }:
                    _ = Add(result, value.Definition.Table)
                        .PrimaryKeys
                        .Add(value.Definition.Name);
                    break;
                case SafeMigrationOperation { Intent: EnsureUniqueConstraintIntent value }:
                    _ = Add(result, value.Definition.Table)
                        .UniqueConstraints
                        .Add(value.Definition.Name);
                    break;
                case SafeMigrationOperation { Intent: EnsureCheckConstraintIntent value }:
                    _ = Add(result, value.Definition.Table)
                        .Checks
                        .Add(value.Definition.Name);
                    break;
                case SafeMigrationOperation { Intent: EnsureForeignKeyIntent value }:
                    _ = Add(result, value.Definition.Table)
                        .ForeignKeys
                        .Add(value.Definition.Name);
                    break;
                case AddColumnOperation value:
                    _ = Add(result, value.Table)
                        .Columns
                        .Add(value.Name);
                    break;
                case AlterColumnOperation value:
                    _ = Add(result, value.Table)
                        .Columns
                        .Add(value.Name);
                    break;
                case RenameColumnOperation value:
                    _ = Add(result, value.Table)
                        .Columns
                        .Add(value.NewName);
                    break;
                case CreateIndexOperation value:
                    _ = Add(result, value.Table)
                        .Indexes
                        .Add(value.Name);
                    break;
                case RenameIndexOperation value when value.Table is { } table:
                    _ = Add(result, table)
                        .Indexes
                        .Add(value.NewName);
                    break;
                case AddPrimaryKeyOperation value:
                    _ = Add(result, value.Table)
                        .PrimaryKeys
                        .Add(value.Name);
                    break;
                case AddUniqueConstraintOperation value:
                    _ = Add(result, value.Table)
                        .UniqueConstraints
                        .Add(value.Name);
                    break;
                case AddCheckConstraintOperation value:
                    _ = Add(result, value.Table)
                        .Checks
                        .Add(value.Name);
                    break;
                case AddForeignKeyOperation value:
                    _ = Add(result, value.Table)
                        .ForeignKeys
                        .Add(value.Name);
                    break;
            }
        }

        return result;
    }

    private static Dictionary<string, RemovedTableArtifacts> ReadRemovedArtifacts(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var result = new Dictionary<string, RemovedTableArtifacts>(s_identifierComparer);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case SafeMigrationOperation { Intent: DropColumnIntent value }:
                    _ = Remove(result, value.Table)
                        .Columns
                        .Add(value.Name);
                    break;
                case SafeMigrationOperation { Intent: RenameColumnIntent value }:
                    _ = Remove(result, value.Table)
                        .Columns
                        .Add(value.Name);
                    break;
                case SafeMigrationOperation { Intent: AlterColumnIntent value }:
                    _ = Remove(result, value.Table)
                        .Columns
                        .Add(value.Definition.Name);
                    break;
                case SafeMigrationOperation { Intent: DropIndexIntent value }:
                    _ = Remove(result, value.Table)
                        .Indexes
                        .Add(value.Name);
                    break;
                case SafeMigrationOperation { Intent: RenameIndexIntent value }:
                    _ = Remove(result, value.Table)
                        .Indexes
                        .Add(value.Name);
                    break;
                case SafeMigrationOperation { Intent: DropPrimaryKeyIntent value }:
                    Remove(result, value.Table)
                        .PrimaryKey = true;
                    break;
                case SafeMigrationOperation { Intent: DropUniqueConstraintIntent value }:
                    _ = Remove(result, value.Table)
                        .UniqueConstraints
                        .Add(value.Name);
                    break;
                case SafeMigrationOperation { Intent: DropCheckConstraintIntent value }:
                    _ = Remove(result, value.Table)
                        .Checks
                        .Add(value.Name);
                    break;
                case SafeMigrationOperation { Intent: DropForeignKeyIntent value }:
                    _ = Remove(result, value.Table)
                        .ForeignKeys
                        .Add(value.Name);
                    break;
                case DropColumnOperation value:
                    _ = Remove(result, value.Table)
                        .Columns
                        .Add(value.Name);
                    break;
                case AlterColumnOperation value:
                    _ = Remove(result, value.Table)
                        .Columns
                        .Add(value.Name);
                    break;
                case RenameColumnOperation value:
                    _ = Remove(result, value.Table)
                        .Columns
                        .Add(value.Name);
                    break;
                case DropIndexOperation value when value.Table is { } table:
                    _ = Remove(result, table)
                        .Indexes
                        .Add(value.Name);
                    break;
                case RenameIndexOperation value when value.Table is { } table:
                    _ = Remove(result, table)
                        .Indexes
                        .Add(value.Name);
                    break;
                case DropPrimaryKeyOperation value:
                    Remove(result, value.Table)
                        .PrimaryKey = true;
                    break;
                case DropUniqueConstraintOperation value:
                    _ = Remove(result, value.Table)
                        .UniqueConstraints
                        .Add(value.Name);
                    break;
                case DropCheckConstraintOperation value:
                    _ = Remove(result, value.Table)
                        .Checks
                        .Add(value.Name);
                    break;
                case DropForeignKeyOperation value:
                    _ = Remove(result, value.Table)
                        .ForeignKeys
                        .Add(value.Name);
                    break;
            }
        }

        return result;
    }

    private static HashSet<string> ReadRemovedTables(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var result = new HashSet<string>(s_identifierComparer);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case SafeMigrationOperation { Intent: DropTableIntent value }:
                    _ = result.Add(value.Table);
                    break;
                case DropTableOperation value:
                    _ = result.Add(value.Name);
                    break;
            }
        }

        return result;
    }

    private static AddedTableArtifacts Add(
        Dictionary<string, AddedTableArtifacts> result,
        string table
    )
    {
        if (!result.TryGetValue(table, out var artifacts))
        {
            artifacts = new AddedTableArtifacts();
            result.Add(table, artifacts);
        }

        return artifacts;
    }

    private static RemovedTableArtifacts Remove(
        Dictionary<string, RemovedTableArtifacts> result,
        string table
    )
    {
        if (!result.TryGetValue(table, out var artifacts))
        {
            artifacts = new RemovedTableArtifacts();
            result.Add(table, artifacts);
        }

        return artifacts;
    }

    private static bool IsReplacement(
        HashSet<string>? removed,
        HashSet<string>? added,
        string name
    ) => removed?.Contains(name) == true && added?.Contains(name) == true;

    private bool IsRemovedForeignKey(
        string table,
        string? name
    ) => name is not null
        && _removedArtifacts.TryGetValue(table, out var removed)
        && removed.ForeignKeys.Contains(name)
        && (!_addedArtifacts.TryGetValue(table, out var added) || !added.ForeignKeys.Contains(name));

    private static bool SqlEquivalent(
        string left,
        string right
    ) => SqliteSqlNormalizer.Equivalent(left, right);

    private sealed record RebuildTableContract(
        IReadOnlyDictionary<string, IColumn> Columns,
        IReadOnlyDictionary<string, ITableIndex> Indexes,
        string? PrimaryKeyName,
        IReadOnlyList<string> PrimaryKeyColumns,
        IReadOnlyList<NamedColumns> UniqueConstraints,
        IReadOnlyList<ForeignKeyShape> ForeignKeys,
        IReadOnlyList<CheckShape> Checks
    );

    private sealed record NamedColumns(
        string Name,
        IReadOnlyList<string> Columns
    );

    private sealed record ForeignKeyShape(
        string Name,
        string PrincipalTable,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> PrincipalColumns,
        ReferentialAction OnUpdate,
        ReferentialAction OnDelete
    );

    private sealed record CheckShape(
        string? Name,
        string Sql
    );

    private sealed record ProjectedForeignKey(
        string Table,
        string? Name,
        string PrincipalTable
    );

    private sealed class AddedTableArtifacts
    {
        public HashSet<string> Checks { get; } = new(s_identifierComparer);

        public HashSet<string> Columns { get; } = new(s_identifierComparer);

        public HashSet<string> ForeignKeys { get; } = new(s_identifierComparer);

        public HashSet<string> Indexes { get; } = new(s_identifierComparer);

        public HashSet<string> PrimaryKeys { get; } = new(s_identifierComparer);

        public HashSet<string> UniqueConstraints { get; } = new(s_identifierComparer);
    }

    private sealed class RemovedTableArtifacts
    {
        public HashSet<string> Checks { get; } = new(s_identifierComparer);

        public HashSet<string> Columns { get; } = new(s_identifierComparer);

        public HashSet<string> ForeignKeys { get; } = new(s_identifierComparer);

        public HashSet<string> Indexes { get; } = new(s_identifierComparer);

        public bool PrimaryKey { get; set; }

        public HashSet<string> UniqueConstraints { get; } = new(s_identifierComparer);
    }
}
