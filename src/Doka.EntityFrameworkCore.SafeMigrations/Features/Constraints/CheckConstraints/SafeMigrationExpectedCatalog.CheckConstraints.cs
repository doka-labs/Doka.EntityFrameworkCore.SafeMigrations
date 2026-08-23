namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedCatalog
{
    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        EnsureCheckConstraintIntent intent
    ) => SetConstraint(
        Find(tables, intent.Definition.Schema, intent.Definition.Table),
        intent.Definition.Name,
        SafeMigrationDatabaseObjectKind.CheckConstraint);

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        DropCheckConstraintIntent intent
    ) => Find(tables, intent.Schema, intent.Table)
        ?.Constraints
        .Remove(intent.Name);
}
