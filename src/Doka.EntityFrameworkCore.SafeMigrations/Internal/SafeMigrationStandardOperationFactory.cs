namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationStandardOperationFactory
{
    public static MigrationOperation Create(
        SafeMigrationIntent intent,
        Func<SafeMigrationSqlExpression, string>? renderExpression = null,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation = null
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        return intent switch
        {
            EnsureSchemaIntent value => CreateOperation(value),
            DropSchemaIntent value => CreateOperation(value),
            EnsureTableIntent value => CreateOperation(value, renderExpression, renderCollation),
            DropTableIntent value => CreateOperation(value),
            RenameTableIntent value => CreateOperation(value),
            EnsureColumnIntent value => CreateOperation(value, renderExpression, renderCollation),
            DropColumnIntent value => CreateOperation(value),
            RenameColumnIntent value => CreateOperation(value),
            AlterColumnIntent value => CreateOperation(value, renderExpression, renderCollation),
            EnsureIndexIntent value => CreateOperation(value, renderExpression),
            DropIndexIntent value => CreateOperation(value),
            RenameIndexIntent value => CreateOperation(value),
            EnsurePrimaryKeyIntent value => CreateOperation(value),
            DropPrimaryKeyIntent value => CreateOperation(value),
            EnsureUniqueConstraintIntent value => CreateOperation(value),
            DropUniqueConstraintIntent value => CreateOperation(value),
            EnsureCheckConstraintIntent value => CreateOperation(value, renderExpression),
            DropCheckConstraintIntent value => CreateOperation(value),
            EnsureForeignKeyIntent value => CreateOperation(value),
            DropForeignKeyIntent value => CreateOperation(value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(intent),
                intent.GetType()
                    .FullName,
                "Unknown SafeMigrations intent type."),
        };
    }

    private static string Render(
        SafeMigrationSqlExpression expression,
        Func<SafeMigrationSqlExpression, string>? renderExpression
    ) => renderExpression?.Invoke(expression)
        ?? throw new NotSupportedException("A structured SQL expression requires a provider-specific renderer.");

    private static string? Render(
        SafeMigrationCollationIdentifier? collation,
        Func<SafeMigrationCollationIdentifier, string?>? renderCollation
    )
    {
        if (collation is null)
        {
            return null;
        }

        if (renderCollation is null)
        {
            throw new NotSupportedException("A collation identity requires a provider-specific renderer.");
        }

        return renderCollation(collation);
    }
}
