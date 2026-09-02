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
    ) => Find(tables, intent.Schema, intent.Table)
        ?.Columns
        .Remove(intent.Name);

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

            table.IndexDefinitions[pair.Key] = Copy(pair.Value, keys: keys);
        }
    }
}
