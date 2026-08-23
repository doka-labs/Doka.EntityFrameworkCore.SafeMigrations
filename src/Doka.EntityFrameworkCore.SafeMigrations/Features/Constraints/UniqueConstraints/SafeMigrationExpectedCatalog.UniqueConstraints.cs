namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedCatalog
{
    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        EnsureUniqueConstraintIntent intent
    ) => SetConstraint(
        Find(tables, intent.Definition.Schema, intent.Definition.Table),
        intent.Definition.Name,
        SafeMigrationDatabaseObjectKind.UniqueConstraint);

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        DropUniqueConstraintIntent intent
    ) => Find(tables, intent.Schema, intent.Table)
        ?.Constraints
        .Remove(intent.Name);
}
