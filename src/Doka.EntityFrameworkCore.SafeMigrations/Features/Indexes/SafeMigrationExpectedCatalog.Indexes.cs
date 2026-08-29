namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedCatalog
{
    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        EnsureIndexIntent intent
    )
    {
        var table = Find(tables, intent.Definition.Schema, intent.Definition.Table);
        if (table is null)
        {
            return;
        }

        table.Indexes.Add(intent.Definition.Name);
        if (intent.Definition.Unique)
        {
            table.UniqueIndexes.Add(intent.Definition.Name);
        }
        else
        {
            table.UniqueIndexes.Remove(intent.Definition.Name);
        }
    }

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        DropIndexIntent intent
    )
    {
        var table = Find(tables, intent.Schema, intent.Table);
        table?.Indexes.Remove(intent.Name);
        table?.UniqueIndexes.Remove(intent.Name);
    }

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        RenameIndexIntent intent
    )
    {
        var table = Find(tables, intent.Schema, intent.Table);
        Rename(table?.Indexes, intent.Name, intent.NewName);
        Rename(table?.UniqueIndexes, intent.Name, intent.NewName);
    }
}
