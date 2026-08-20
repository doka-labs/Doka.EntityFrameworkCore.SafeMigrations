namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures that a schema exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> EnsureSchemaExists(
        this MigrationBuilder migrationBuilder,
        string name
    ) => Add(migrationBuilder, new EnsureSchemaIntent(name), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Drops a schema when it exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> DropSchemaIfExists(
        this MigrationBuilder migrationBuilder,
        string name
    ) => Add(migrationBuilder, new DropSchemaIntent(name), SafeMigrationPolicy.ThrowIfDifferent);
}
