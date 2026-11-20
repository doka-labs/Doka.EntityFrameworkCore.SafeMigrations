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
            .Where(key => tables.Comparer.Equals(key, key with { Schema = intent.Name }))

            // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        foreach (var key in keys)
        {
            tables.Remove(key);
        }
    }
}
