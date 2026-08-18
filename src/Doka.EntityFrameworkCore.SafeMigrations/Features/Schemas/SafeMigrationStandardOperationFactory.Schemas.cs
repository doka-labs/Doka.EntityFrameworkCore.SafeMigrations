namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    private static EnsureSchemaOperation CreateOperation(
        EnsureSchemaIntent intent
    ) => new() { Name = intent.Name };

    private static DropSchemaOperation CreateOperation(
        DropSchemaIntent intent
    ) => new() { Name = intent.Name };
}
