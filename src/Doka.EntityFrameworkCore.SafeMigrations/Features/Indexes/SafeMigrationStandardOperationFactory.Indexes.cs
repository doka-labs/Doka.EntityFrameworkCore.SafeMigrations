namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static CreateIndexOperation CreateOperation(
        EnsureIndexIntent intent,
        Func<SafeMigrationSqlExpression, string>? renderExpression
    )
    {
        var definition = intent.Definition;
        if (definition.Keys.Any(static key => key.Expression is not null || key.StructuredExpression is not null))
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
                .Keys
                .Select(static key => key.Column!)

                // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based
                // materialization.
                // ReSharper disable once UseCollectionExpression
                .ToArray(),
            IsUnique = definition.Unique,
            Filter = definition.Filter
                ?? (definition.StructuredFilter is null
                    ? null
                    : Render(definition.StructuredFilter, renderExpression)),
            IsDescending = definition
                .Keys
                .Select(static key => key.SortOrder == SafeMigrationIndexSortOrder.Descending)

                // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based
                // materialization.
                // ReSharper disable once UseCollectionExpression
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
