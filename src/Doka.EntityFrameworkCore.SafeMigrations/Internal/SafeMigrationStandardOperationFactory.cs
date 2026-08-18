namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    public static MigrationOperation Create(
        SafeMigrationIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        return intent switch
        {
            EnsureSchemaIntent value => CreateOperation(value),
            DropSchemaIntent value => CreateOperation(value),
            EnsureTableIntent value => CreateOperation(value),
            DropTableIntent value => CreateOperation(value),
            RenameTableIntent value => CreateOperation(value),
            EnsureColumnIntent value => CreateOperation(value),
            DropColumnIntent value => CreateOperation(value),
            RenameColumnIntent value => CreateOperation(value),
            AlterColumnIntent value => CreateOperation(value),
            EnsureIndexIntent value => CreateOperation(value),
            DropIndexIntent value => CreateOperation(value),
            RenameIndexIntent value => CreateOperation(value),
            EnsurePrimaryKeyIntent value => CreateOperation(value),
            DropPrimaryKeyIntent value => CreateOperation(value),
            EnsureUniqueConstraintIntent value => CreateOperation(value),
            DropUniqueConstraintIntent value => CreateOperation(value),
            EnsureCheckConstraintIntent value => CreateOperation(value),
            DropCheckConstraintIntent value => CreateOperation(value),
            EnsureForeignKeyIntent value => CreateOperation(value),
            DropForeignKeyIntent value => CreateOperation(value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(intent),
                intent.GetType().FullName,
                "Unknown SafeMigrations intent type."),
        };
    }
}
