namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedCatalog
{
    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        EnsureForeignKeyIntent intent
    ) => SetConstraint(
        Find(tables, intent.Definition.Schema, intent.Definition.Table),
        intent.Definition.Name,
        SafeMigrationDatabaseObjectKind.ForeignKey);

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        DropForeignKeyIntent intent
    ) => Find(tables, intent.Schema, intent.Table)
        ?.Constraints.Remove(intent.Name);
}
