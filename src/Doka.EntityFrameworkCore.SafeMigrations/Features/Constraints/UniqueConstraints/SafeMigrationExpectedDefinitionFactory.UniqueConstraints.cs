namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedUniqueConstraintDefinition From(
        AddUniqueConstraintOperation operation
    ) => new(operation.Name, operation.Table, operation.Columns, operation.Schema);
}
