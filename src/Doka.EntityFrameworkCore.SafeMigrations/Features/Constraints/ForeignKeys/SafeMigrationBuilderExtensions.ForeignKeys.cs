namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures a foreign key exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> EnsureForeignKey(
        this MigrationBuilder migrationBuilder,
        ExpectedForeignKeyDefinition definition,
        SafeMigrationPolicy policy
    ) => Add(migrationBuilder, new EnsureForeignKeyIntent(definition), policy);

    /// <summary>Ensures a foreign key exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> AddForeignKeyIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        IEnumerable<string> columns,
        string principalTable,
        IEnumerable<string> principalColumns,
        string? schema = null,
        string? principalSchema = null,
        ReferentialAction onUpdate = ReferentialAction.NoAction,
        ReferentialAction onDelete = ReferentialAction.NoAction,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent
    ) => migrationBuilder.EnsureForeignKey(
        new ExpectedForeignKeyDefinition(
            name,
            table,
            columns,
            principalTable,
            principalColumns,
            schema,
            principalSchema,
            onUpdate,
            onDelete),
        policy);

    /// <summary>Drops a foreign key when it exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> DropForeignKeyIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => Add(migrationBuilder, new DropForeignKeyIntent(name, table, schema), SafeMigrationPolicy.ThrowIfDifferent);
}
