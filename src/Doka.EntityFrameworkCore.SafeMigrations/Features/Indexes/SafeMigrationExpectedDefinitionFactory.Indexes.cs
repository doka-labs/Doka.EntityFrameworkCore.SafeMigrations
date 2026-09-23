namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedIndexDefinition From(
        CreateIndexOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.IsDescending is not null
            && operation.IsDescending.Length != operation.Columns.Length)
        {
            throw new InvalidOperationException(
                $"Index '{operation.Name}' has a sort-order count that does not match its key count.");
        }

        var keys = operation.Columns.Select((column, ordinal) => new ExpectedIndexKeyDefinition(
            column,
            sortOrder: operation.IsDescending is null
                ? SafeMigrationIndexSortOrder.ProviderDefault
                : operation.IsDescending[ordinal]
                    ? SafeMigrationIndexSortOrder.Descending
                    : SafeMigrationIndexSortOrder.Ascending));

        return new ExpectedIndexDefinition(
            operation.Name,
            operation.Table,
            keys,
            operation.Schema,
            operation.IsUnique,
            operation.Filter);
    }
}
