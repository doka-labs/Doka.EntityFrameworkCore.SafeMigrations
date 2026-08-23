namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedCatalog
{
    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        EnsureIndexIntent intent
    ) => Find(tables, intent.Definition.Schema, intent.Definition.Table)
        ?.Indexes
        .Add(intent.Definition.Name);

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        DropIndexIntent intent
    ) => Find(tables, intent.Schema, intent.Table)
        ?.Indexes
        .Remove(intent.Name);

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        RenameIndexIntent intent
    ) => Rename(
        Find(tables, intent.Schema, intent.Table)?.Indexes,
        intent.Name,
        intent.NewName);
}
