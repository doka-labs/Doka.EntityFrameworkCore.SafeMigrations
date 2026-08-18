namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationExpectedDefinitionFactory
{
    public static ExpectedForeignKeyDefinition From(
        AddForeignKeyOperation operation
    ) => new(
        operation.Name,
        operation.Table,
        operation.Columns,
        operation.PrincipalTable,
        operation.PrincipalColumns
        ?? throw new InvalidOperationException("Safe foreign keys require explicit principal columns."),
        operation.Schema,
        operation.PrincipalSchema,
        operation.OnUpdate,
        operation.OnDelete);
}
