namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedPrimaryKeyDefinition From(
        AddPrimaryKeyOperation operation
    ) => new(operation.Name, operation.Table, operation.Columns, operation.Schema);
}
