namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures a foreign key exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="definition">The complete expected database-object definition.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> EnsureForeignKey(
        this MigrationBuilder migrationBuilder,
        ExpectedForeignKeyDefinition definition,
        SafeMigrationPolicy policy
    ) => Add(migrationBuilder, new EnsureForeignKeyIntent(definition), policy);

    /// <summary>Ensures a foreign key exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="columns">The ordered dependent column names.</param>
    /// <param name="principalTable">The referenced table name.</param>
    /// <param name="principalColumns">The ordered referenced columns.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="principalSchema">The referenced schema name, or null for the provider default.</param>
    /// <param name="onUpdate">The referential action applied on principal-key update.</param>
    /// <param name="onDelete">The referential action applied on principal-row deletion.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
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
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> DropForeignKeyIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => Add(migrationBuilder, new DropForeignKeyIntent(name, table, schema), SafeMigrationPolicy.ThrowIfDifferent);
}
