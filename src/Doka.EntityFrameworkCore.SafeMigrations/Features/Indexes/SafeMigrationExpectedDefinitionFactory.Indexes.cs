namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedIndexDefinition From(
        CreateIndexOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        var descending = operation.IsDescending;
        var keys = operation.Columns.Select((
            column,
            index
        ) => new ExpectedIndexKeyDefinition(
            column,
            descending: descending is not null && descending.Length > index && descending[index]));

        return new ExpectedIndexDefinition(
            operation.Name,
            operation.Table,
            keys,
            operation.Schema,
            operation.IsUnique,
            operation.Filter);
    }
}
