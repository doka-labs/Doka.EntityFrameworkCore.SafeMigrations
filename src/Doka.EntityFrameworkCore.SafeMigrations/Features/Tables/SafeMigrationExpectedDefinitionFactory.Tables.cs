namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedTableDefinition From(
        CreateTableOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        return new ExpectedTableDefinition(
            operation.Name,
            operation.Columns.Select(From),
            operation.Schema,
            operation.Comment,
            operation.PrimaryKey is null ? null : From(operation.PrimaryKey),
            operation.UniqueConstraints.Select(From),
            operation.CheckConstraints.Select(From),
            operation.ForeignKeys.Select(From));
    }
}
