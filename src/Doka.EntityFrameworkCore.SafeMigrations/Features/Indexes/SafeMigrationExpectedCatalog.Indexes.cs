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
        table.IndexDefinitions[intent.Definition.Name] = intent.Definition;
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
        table?.IndexDefinitions.Remove(intent.Name);
    }

    private static void Apply(
        Dictionary<TableKey, MutableTable> tables,
        RenameIndexIntent intent
    )
    {
        var table = Find(tables, intent.Schema, intent.Table);
        Rename(table?.Indexes, intent.Name, intent.NewName);
        Rename(table?.UniqueIndexes, intent.Name, intent.NewName);

        if (table?.IndexDefinitions.Remove(intent.Name, out var definition) == true)
        {
            table.IndexDefinitions[intent.NewName] = Copy(
                definition,
                name: intent.NewName);
        }
    }

    private static ExpectedIndexDefinition Copy(
        ExpectedIndexDefinition definition,
        string? name = null,
        string? table = null,
        string? schema = null,
        IReadOnlyList<ExpectedIndexKeyDefinition>? keys = null
    ) => new(
        name ?? definition.Name,
        table ?? definition.Table,
        keys ?? definition.Keys,
        schema ?? definition.Schema,
        definition.Unique,
        definition.Filter,
        definition.IncludedColumns,
        definition.Method,
        definition.NullsDistinct,
        definition.StructuredFilter);
}
