namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedCatalog
{
    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        EnsureColumnIntent intent
    )
    {
        var table = Find(tables, intent.Schema, intent.Table);
        table?.Columns[intent.Definition.Name] = intent.Definition.StoreType;
    }

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        DropColumnIntent intent
    )
    {
        var table = Find(tables, intent.Schema, intent.Table);
        if (table is null)
        {
            return;
        }

        var dependentIndex = table.IndexDefinitions.Values.FirstOrDefault(
            value => SafeMigrationExpectedIndexTransitions.MayDependOnColumn(value, intent.Name));
        if (dependentIndex is not null)
        {
            throw new InvalidOperationException(
                $"Cannot project column drop '{intent.Name}' on table '{intent.Table}' while index "
                + $"'{dependentIndex.Name}' may depend on it. Drop the index explicitly before the column "
                + "drop and recreate it afterward when required.");
        }

        table.Columns.Remove(intent.Name);
    }

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        RenameColumnIntent intent
    )
    {
        var columns = Find(tables, intent.Schema, intent.Table)?.Columns;
        if (columns?.Remove(intent.Name, out var storeType) == true)
        {
            columns[intent.NewName] = storeType;
        }
        else
        {
            return;
        }

        var table = Find(tables, intent.Schema, intent.Table)!;
        foreach (var pair in table.IndexDefinitions.ToArray())
        {
            var keys = pair.Value.Keys
                .Select(key => StringComparer.Ordinal.Equals(key.Column, intent.Name)
                    ? new ExpectedIndexKeyDefinition(
                        column: intent.NewName,
                        sortOrder: key.SortOrder,
                        nullOrder: key.NullOrder,
                        prefixLength: key.PrefixLength,
                        collation: key.Collation,
                        operatorClass: key.OperatorClass)
                    : key)
                .ToArray();

            table.IndexDefinitions[pair.Key] = Copy(
                pair.Value,
                keys: keys,
                includedColumns: pair.Value.IncludedColumns
                    .Select(column => StringComparer.Ordinal.Equals(column, intent.Name)
                        ? intent.NewName
                        : column)
                    .ToArray());
        }
    }
}
