namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedCheckConstraintDefinition From(
        AddCheckConstraintOperation operation
    ) => new(operation.Name, operation.Table, operation.Sql, operation.Schema);
}
