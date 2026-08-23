namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures a column using a complete expected definition.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="table">The table name.</param>
    /// <param name="definition">The complete expected database-object definition.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> EnsureColumn(
        this MigrationBuilder migrationBuilder,
        string table,
        ExpectedColumnDefinition definition,
        SafeMigrationPolicy policy,
        string? schema = null
    ) => Add(migrationBuilder, new EnsureColumnIntent(table, definition, schema), policy);

    /// <summary>
    /// Ensures a column using the familiar EF Core column facets.
    /// </summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="type">The explicit store type, or null for provider inference.</param>
    /// <param name="unicode">The Unicode facet, or null when unspecified.</param>
    /// <param name="maxLength">The maximum-length facet, or null when unspecified.</param>
    /// <param name="rowVersion">Whether the column is a row-version column.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="nullable">Whether the column accepts null values.</param>
    /// <param name="defaultValue">The literal default value, or null when absent.</param>
    /// <param name="defaultValueSql">The default SQL expression, or null when absent.</param>
    /// <param name="computedColumnSql">The computed-column SQL expression, or null when absent.</param>
    /// <param name="fixedLength">The fixed-length facet, or null when unspecified.</param>
    /// <param name="comment">The expected database comment, or null when unspecified.</param>
    /// <param name="collation">
    /// The expected database collation identity. Null requires the effective
    /// provider-inferred default and never disables comparison.
    /// </param>
    /// <param name="precision">The numeric precision, or null when unspecified.</param>
    /// <param name="scale">The numeric scale, or null when unspecified.</param>
    /// <param name="stored">Whether the computed column is stored, or null when unspecified.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <typeparam name="T">The column CLR type.</typeparam>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> AddColumnIfNotExists<T>(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? type = null,
        bool? unicode = null,
        int? maxLength = null,
        bool rowVersion = false,
        string? schema = null,
        bool nullable = false,
        object? defaultValue = null,
        string? defaultValueSql = null,
        string? computedColumnSql = null,
        bool? fixedLength = null,
        string? comment = null,
        SafeMigrationCollationIdentifier? collation = null,
        int? precision = null,
        int? scale = null,
        bool? stored = null,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent
    )
    {
        if (defaultValue is not null
            && defaultValueSql is not null)
        {
            throw new ArgumentException("A literal default and default SQL are mutually exclusive.");
        }

        var expectedDefault = defaultValueSql is not null
            ? SafeMigrationDefaultValue.Sql(defaultValueSql)
            : defaultValue is null
                ? SafeMigrationDefaultValue.None
                : SafeMigrationDefaultValue.Literal(defaultValue);

        var definition = new ExpectedColumnDefinition(
            name,
            typeof(T),
            nullable,
            type,
            unicode,
            maxLength,
            fixedLength,
            rowVersion,
            precision,
            scale,
            collation,
            comment,
            expectedDefault,
            computedColumnSql,
            stored);

        return migrationBuilder.EnsureColumn(table, definition, policy, schema);
    }

    /// <summary>Drops a column when it exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> DropColumnIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => Add(migrationBuilder, new DropColumnIntent(name, table, schema), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Renames a column when the source exists and the target is free.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="newName">The target database object name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> RenameColumnIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string newName,
        string? schema = null
    ) => Add(
        migrationBuilder,
        new RenameColumnIntent(name, table, newName, schema),
        SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>
    /// Alters a column only when the live definition differs and the selected
    /// policy permits the proven transition.
    /// </summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="table">The table name.</param>
    /// <param name="definition">The complete expected database-object definition.</param>
    /// <param name="oldDefinition">The prior model definition, or null when unavailable.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> AlterColumnIfDifferent(
        this MigrationBuilder migrationBuilder,
        string table,
        ExpectedColumnDefinition definition,
        ExpectedColumnDefinition? oldDefinition,
        SafeMigrationPolicy policy,
        string? schema = null
    ) => Add(migrationBuilder, new AlterColumnIntent(table, definition, oldDefinition, schema), policy);
}
