namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures a primary key exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> EnsurePrimaryKey(
        this MigrationBuilder migrationBuilder,
        ExpectedPrimaryKeyDefinition definition,
        SafeMigrationPolicy policy
    ) => Add(migrationBuilder, new EnsurePrimaryKeyIntent(definition), policy);

    /// <summary>Ensures a primary key exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> AddPrimaryKeyIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        IEnumerable<string> columns,
        string? schema = null,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent
    ) => migrationBuilder.EnsurePrimaryKey(new ExpectedPrimaryKeyDefinition(name, table, columns, schema), policy);

    /// <summary>Drops a primary key when it exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> DropPrimaryKeyIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => Add(migrationBuilder, new DropPrimaryKeyIntent(name, table, schema), SafeMigrationPolicy.ThrowIfDifferent);
}
