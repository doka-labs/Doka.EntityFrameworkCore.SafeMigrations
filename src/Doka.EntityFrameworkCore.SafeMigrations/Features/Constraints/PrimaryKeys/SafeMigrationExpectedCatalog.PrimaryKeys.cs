namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedCatalog
{
    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        EnsurePrimaryKeyIntent intent
    )
    {
        var table = Find(tables, intent.Definition.Schema, intent.Definition.Table);
        if (table is null)
        {
            return;
        }

        RemovePrimaryKey(table);
        table.Constraints[intent.Definition.Name] = SafeMigrationDatabaseObjectKind.PrimaryKey;
    }

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        DropPrimaryKeyIntent intent
    ) => RemovePrimaryKey(Find(tables, intent.Schema, intent.Table));

    private static void RemovePrimaryKey(
        MutableTable? table
    )
    {
        if (table is null)
        {
            return;
        }

        var names = table
            .Constraints
            .Where(static value => value.Value == SafeMigrationDatabaseObjectKind.PrimaryKey)
            .Select(static value => value.Key)

            // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        foreach (var name in names)
        {
            table.Constraints.Remove(name);
        }
    }
}
