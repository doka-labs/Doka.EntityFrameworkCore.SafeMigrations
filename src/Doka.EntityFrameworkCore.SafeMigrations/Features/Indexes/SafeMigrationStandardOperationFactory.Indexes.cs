namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static CreateIndexOperation CreateOperation(
        EnsureIndexIntent intent
    )
    {
        var definition = intent.Definition;
        if (definition.Keys.Any(static key => key.Expression is not null))
        {
            throw new NotSupportedException(
                "Functional index keys require a provider-specific baseline operation contract.");
        }

        return new CreateIndexOperation
        {
            Name = definition.Name,
            Table = definition.Table,
            Schema = definition.Schema,
            Columns = definition
                .Keys.Select(static key => key.Column!)
                .ToArray(),
            IsUnique = definition.Unique,
            Filter = definition.Filter,
            IsDescending = definition
                .Keys.Select(static key => key.Descending)
                .ToArray(),
        };
    }

    private static DropIndexOperation CreateOperation(
        DropIndexIntent intent
    ) => new()
    {
        Name = intent.Name,
        Table = intent.Table,
        Schema = intent.Schema,
    };

    private static RenameIndexOperation CreateOperation(
        RenameIndexIntent intent
    ) => new()
    {
        Name = intent.Name,
        Table = intent.Table,
        Schema = intent.Schema,
        NewName = intent.NewName,
    };
}
