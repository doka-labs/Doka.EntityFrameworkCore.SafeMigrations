namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures a check constraint exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> EnsureCheckConstraint(
        this MigrationBuilder migrationBuilder,
        ExpectedCheckConstraintDefinition definition,
        SafeMigrationPolicy policy
    ) => Add(migrationBuilder, new EnsureCheckConstraintIntent(definition), policy);

    /// <summary>Ensures a check constraint exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> AddCheckConstraintIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string sql,
        string? schema = null,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent
    ) => migrationBuilder.EnsureCheckConstraint(
        new ExpectedCheckConstraintDefinition(name, table, sql, schema),
        policy);

    /// <summary>Drops a check constraint when it exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> DropCheckConstraintIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => Add(
        migrationBuilder,
        new DropCheckConstraintIntent(name, table, schema),
        SafeMigrationPolicy.ThrowIfDifferent);
}
