namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedCatalog
{
    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        EnsureColumnIntent intent
    ) => Find(tables, intent.Schema, intent.Table)
        ?.Columns
        .Add(intent.Definition.Name);

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        DropColumnIntent intent
    ) => Find(tables, intent.Schema, intent.Table)
        ?.Columns
        .Remove(intent.Name);

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        RenameColumnIntent intent
    ) => Rename(
        Find(tables, intent.Schema, intent.Table)?.Columns,
        intent.Name,
        intent.NewName);
}
