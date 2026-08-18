namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures that a schema exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> EnsureSchemaExists(
        this MigrationBuilder migrationBuilder,
        string name
    ) => Add(migrationBuilder, new EnsureSchemaIntent(name), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Drops a schema when it exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> DropSchemaIfExists(
        this MigrationBuilder migrationBuilder,
        string name
    ) => Add(migrationBuilder, new DropSchemaIntent(name), SafeMigrationPolicy.ThrowIfDifferent);
}
