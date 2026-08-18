namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures a unique constraint exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> EnsureUniqueConstraint(
        this MigrationBuilder migrationBuilder,
        ExpectedUniqueConstraintDefinition definition,
        SafeMigrationPolicy policy
    ) => Add(migrationBuilder, new EnsureUniqueConstraintIntent(definition), policy);

    /// <summary>Ensures a unique constraint exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> AddUniqueConstraintIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        IEnumerable<string> columns,
        string? schema = null,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent
    ) => migrationBuilder.EnsureUniqueConstraint(
        new ExpectedUniqueConstraintDefinition(name, table, columns, schema),
        policy);

    /// <summary>Drops a unique constraint when it exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> DropUniqueConstraintIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => Add(
        migrationBuilder,
        new DropUniqueConstraintIntent(name, table, schema),
        SafeMigrationPolicy.ThrowIfDifferent);
}
