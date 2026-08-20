namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures a primary key exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="definition">The complete expected database-object definition.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> EnsurePrimaryKey(
        this MigrationBuilder migrationBuilder,
        ExpectedPrimaryKeyDefinition definition,
        SafeMigrationPolicy policy
    ) => Add(migrationBuilder, new EnsurePrimaryKeyIntent(definition), policy);

    /// <summary>Ensures a primary key exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="columns">The ordered primary-key column names.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> AddPrimaryKeyIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        IEnumerable<string> columns,
        string? schema = null,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent
    ) => migrationBuilder.EnsurePrimaryKey(new ExpectedPrimaryKeyDefinition(name, table, columns, schema), policy);

    /// <summary>Drops a primary key when it exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> DropPrimaryKeyIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => Add(migrationBuilder, new DropPrimaryKeyIntent(name, table, schema), SafeMigrationPolicy.ThrowIfDifferent);
}
