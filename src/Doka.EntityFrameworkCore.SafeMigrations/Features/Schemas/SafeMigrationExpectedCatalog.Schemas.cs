namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedCatalog
{
    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        DropSchemaIntent intent
    )
    {
        var keys = tables
            .Keys
            .Where(key => StringComparer.Ordinal.Equals(key.Schema, intent.Name))
            .ToArray();

        foreach (var key in keys)
        {
            tables.Remove(key);
        }
    }
}
