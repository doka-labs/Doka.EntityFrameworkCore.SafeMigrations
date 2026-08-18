namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static AddForeignKeyOperation CreateOperation(
        EnsureForeignKeyIntent intent
    ) => CreateForeignKey(intent.Definition);

    private static DropForeignKeyOperation CreateOperation(
        DropForeignKeyIntent intent
    ) => new()
    {
        Name = intent.Name,
        Table = intent.Table,
        Schema = intent.Schema,
    };

    private static AddForeignKeyOperation CreateForeignKey(
        ExpectedForeignKeyDefinition definition
    ) => new()
    {
        Name = definition.Name,
        Table = definition.Table,
        Schema = definition.Schema,
        Columns = definition.Columns.ToArray(),
        PrincipalTable = definition.PrincipalTable,
        PrincipalSchema = definition.PrincipalSchema,
        PrincipalColumns = definition.PrincipalColumns.ToArray(),
        OnUpdate = definition.OnUpdate,
        OnDelete = definition.OnDelete,
    };
}
