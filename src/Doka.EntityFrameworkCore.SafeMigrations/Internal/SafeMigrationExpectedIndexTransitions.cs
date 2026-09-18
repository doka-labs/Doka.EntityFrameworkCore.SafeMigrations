namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Validates dependencies introduced by index operations in one ordered migration stream.
/// </summary>
internal static class SafeMigrationExpectedIndexTransitions
{
    public static void Validate(
        IReadOnlyList<MigrationOperation> operations
    ) => Validate(operations, static schema => schema);

    public static void Validate(
        IReadOnlyList<MigrationOperation> operations,
        Func<string?, string?> normalizeSchema
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(normalizeSchema);

        var tables = new Dictionary<TableKey, Dictionary<string, ExpectedIndexDefinition>>(
            new TableKeyComparer(normalizeSchema));
        foreach (var operation in operations.OfType<SafeMigrationOperation>())
        {
            Apply(tables, operation.Intent, normalizeSchema);
        }
    }

    public static bool MayDependOnColumn(
        ExpectedIndexDefinition definition,
        string column
    )
    {
        if (definition.IncludedColumns.Contains(column, StringComparer.Ordinal))
        {
            return true;
        }

        foreach (var key in definition.Keys)
        {
            if (StringComparer.Ordinal.Equals(key.Column, column)
                || key.Expression is not null
                || IndexExpressionMayReferenceColumn(key.StructuredExpression, column))
            {
                return true;
            }
        }

        return definition.Filter is not null
            || IndexExpressionMayReferenceColumn(definition.StructuredFilter, column);
    }

    private static void Apply(
        Dictionary<TableKey, Dictionary<string, ExpectedIndexDefinition>> tables,
        SafeMigrationIntent intent,
        Func<string?, string?> normalizeSchema
    )
    {
        switch (intent)
        {
            case EnsureTableIntent value:
                _ = GetOrAdd(tables, value.Definition.Schema, value.Definition.Table);
                break;
            case DropTableIntent value:
                tables.Remove(new TableKey(value.Schema, value.Table));
                break;
            case RenameTableIntent value:
                RenameTable(tables, value);
                break;
            case DropSchemaIntent value:
                DropSchema(tables, value.Name, normalizeSchema);
                break;
            case EnsureIndexIntent value:
                GetOrAdd(tables, value.Definition.Schema, value.Definition.Table)[value.Definition.Name] =
                    value.Definition;
                break;
            case DropIndexIntent value:
                Find(tables, value.Schema, value.Table)?.Remove(value.Name);
                break;
            case RenameIndexIntent value:
                RenameIndex(tables, value);
                break;
            case RenameColumnIntent value:
                RenameColumn(tables, value);
                break;
            case DropColumnIntent value:
                ValidateColumnDrop(tables, value);
                break;
        }
    }

    private static void RenameTable(
        Dictionary<TableKey, Dictionary<string, ExpectedIndexDefinition>> tables,
        RenameTableIntent intent
    )
    {
        if (!tables.Remove(new TableKey(intent.Schema, intent.Name), out var indexes))
        {
            return;
        }

        var table = intent.NewName ?? intent.Name;
        var schema = intent.NewSchema ?? intent.Schema;
        var target = new TableKey(schema, table);
        if (tables.ContainsKey(target))
        {
            throw new InvalidOperationException(
                $"Cannot project table rename '{intent.Name}' to '{table}' because both table identities "
                + "contain index transition evidence.");
        }

        tables[target] = indexes.ToDictionary(
            static pair => pair.Key,
            pair => Copy(pair.Value, table: table, schema: schema),
            StringComparer.Ordinal);
    }

    private static void DropSchema(
        Dictionary<TableKey, Dictionary<string, ExpectedIndexDefinition>> tables,
        string schema,
        Func<string?, string?> normalizeSchema
    )
    {
        var normalizedSchema = normalizeSchema(schema);
        foreach (var key in tables.Keys
                     .Where(key => StringComparer.Ordinal.Equals(normalizeSchema(key.Schema), normalizedSchema))
                     .ToArray())
        {
            tables.Remove(key);
        }
    }

    private static void RenameIndex(
        Dictionary<TableKey, Dictionary<string, ExpectedIndexDefinition>> tables,
        RenameIndexIntent intent
    )
    {
        var indexes = Find(tables, intent.Schema, intent.Table);
        if (indexes?.Remove(intent.Name, out var definition) == true)
        {
            indexes[intent.NewName] = Copy(definition, name: intent.NewName);
        }
    }

    private static void RenameColumn(
        Dictionary<TableKey, Dictionary<string, ExpectedIndexDefinition>> tables,
        RenameColumnIntent intent
    )
    {
        var indexes = Find(tables, intent.Schema, intent.Table);
        if (indexes is null)
        {
            return;
        }

        foreach (var pair in indexes.ToArray())
        {
            if (HasExpressionDependency(pair.Value, intent.Name))
            {
                throw new InvalidOperationException(
                    $"Cannot project column rename '{intent.Name}' to '{intent.NewName}' on table "
                    + $"'{intent.Table}' while index '{pair.Value.Name}' may depend on it through an "
                    + "expression. Drop the index explicitly before the column rename and recreate it afterward.");
            }

            indexes[pair.Key] = Copy(
                pair.Value,
                keys: pair.Value.Keys.Select(key => RenameKey(key, intent.Name, intent.NewName)).ToArray(),
                includedColumns: pair.Value.IncludedColumns
                    .Select(column => StringComparer.Ordinal.Equals(column, intent.Name)
                        ? intent.NewName
                        : column)
                    .ToArray());
        }
    }

    private static void ValidateColumnDrop(
        Dictionary<TableKey, Dictionary<string, ExpectedIndexDefinition>> tables,
        DropColumnIntent intent
    )
    {
        var dependency = Find(tables, intent.Schema, intent.Table)?
            .Values
            .FirstOrDefault(index => MayDependOnColumn(index, intent.Name));
        if (dependency is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot project column drop '{intent.Name}' on table '{intent.Table}' while index "
            + $"'{dependency.Name}' may depend on it. Drop the index explicitly before the column "
            + "drop and recreate it afterward when required.");
    }

    private static bool HasExpressionDependency(
        ExpectedIndexDefinition definition,
        string column
    ) => definition.Keys.Any(key =>
            key.Expression is not null
            || IndexExpressionMayReferenceColumn(key.StructuredExpression, column))
        || definition.Filter is not null
        || IndexExpressionMayReferenceColumn(definition.StructuredFilter, column);

    private static bool IndexExpressionMayReferenceColumn(
        SafeMigrationSqlExpression? expression,
        string column
    ) => expression is not null
        && (!SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression)
            || SafeMigrationSqlExpressionInspector.ReferencesIdentifier(expression, column));

    private static ExpectedIndexKeyDefinition RenameKey(
        ExpectedIndexKeyDefinition key,
        string column,
        string newColumn
    ) => StringComparer.Ordinal.Equals(key.Column, column)
        ? new ExpectedIndexKeyDefinition(
            column: newColumn,
            sortOrder: key.SortOrder,
            nullOrder: key.NullOrder,
            prefixLength: key.PrefixLength,
            collation: key.Collation,
            operatorClass: key.OperatorClass)
        : key;

    private static ExpectedIndexDefinition Copy(
        ExpectedIndexDefinition definition,
        string? name = null,
        string? table = null,
        string? schema = null,
        IReadOnlyList<ExpectedIndexKeyDefinition>? keys = null,
        IReadOnlyList<string>? includedColumns = null
    ) => new(
        name ?? definition.Name,
        table ?? definition.Table,
        keys ?? definition.Keys,
        schema ?? definition.Schema,
        definition.Unique,
        definition.Filter,
        includedColumns ?? definition.IncludedColumns,
        definition.Method,
        definition.NullsDistinct,
        definition.StructuredFilter);

    private static Dictionary<string, ExpectedIndexDefinition> GetOrAdd(
        Dictionary<TableKey, Dictionary<string, ExpectedIndexDefinition>> tables,
        string? schema,
        string table
    )
    {
        var key = new TableKey(schema, table);
        if (!tables.TryGetValue(key, out var indexes))
        {
            indexes = new Dictionary<string, ExpectedIndexDefinition>(StringComparer.Ordinal);
            tables.Add(key, indexes);
        }

        return indexes;
    }

    private static Dictionary<string, ExpectedIndexDefinition>? Find(
        Dictionary<TableKey, Dictionary<string, ExpectedIndexDefinition>> tables,
        string? schema,
        string table
    ) => tables.GetValueOrDefault(new TableKey(schema, table));

    private readonly record struct TableKey(
        string? Schema,
        string Table
    );

    private sealed class TableKeyComparer(
        Func<string?, string?> normalizeSchema
    ) : IEqualityComparer<TableKey>
    {
        public bool Equals(
            TableKey left,
            TableKey right
        ) => StringComparer.Ordinal.Equals(left.Table, right.Table)
            && StringComparer.Ordinal.Equals(
                normalizeSchema(left.Schema),
                normalizeSchema(right.Schema));

        public int GetHashCode(
            TableKey value
        ) => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(value.Table),
            normalizeSchema(value.Schema) is { } schema
                ? StringComparer.Ordinal.GetHashCode(schema)
                : 0);
    }
}
