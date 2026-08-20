namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures a check constraint exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="definition">The complete expected database-object definition.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> EnsureCheckConstraint(
        this MigrationBuilder migrationBuilder,
        ExpectedCheckConstraintDefinition definition,
        SafeMigrationPolicy policy
    ) => Add(migrationBuilder, new EnsureCheckConstraintIntent(definition), policy);

    /// <summary>Ensures a check constraint exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="sql">The SQL expression.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
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
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
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
