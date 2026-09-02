namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedCatalog
{
    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        EnsureTableIntent intent
    ) => tables[new TableKey(intent.Definition.Schema, intent.Definition.Table)] = MutableTable.From(intent.Definition);

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        DropTableIntent intent
    ) => tables.Remove(new TableKey(intent.Schema, intent.Table));

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        RenameTableIntent intent
    )
    {
        var source = new TableKey(intent.Schema, intent.Name);
        if (!tables.Remove(source, out var table))
        {
            return;
        }

        table.Table = intent.NewName ?? intent.Name;
        table.Schema = intent.NewSchema ?? intent.Schema;

        foreach (var pair in table.IndexDefinitions.ToArray())
        {
            table.IndexDefinitions[pair.Key] = Copy(
                pair.Value,
                table: table.Table,
                schema: table.Schema);
        }

        tables[new TableKey(table.Schema, table.Table)] = table;
    }
}
