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
    }
}
