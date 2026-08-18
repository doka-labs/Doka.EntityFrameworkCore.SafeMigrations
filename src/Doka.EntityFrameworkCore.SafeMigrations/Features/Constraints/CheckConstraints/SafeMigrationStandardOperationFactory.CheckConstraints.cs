namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static AddCheckConstraintOperation CreateOperation(
        EnsureCheckConstraintIntent intent
    ) => CreateCheckConstraint(intent.Definition);

    private static DropCheckConstraintOperation CreateOperation(
        DropCheckConstraintIntent intent
    ) => new()
    {
        Name = intent.Name,
        Table = intent.Table,
        Schema = intent.Schema,
    };

    private static AddCheckConstraintOperation CreateCheckConstraint(
        ExpectedCheckConstraintDefinition definition
    ) => new()
    {
        Name = definition.Name,
        Table = definition.Table,
        Schema = definition.Schema,
        Sql = definition.Sql,
    };
}
