namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures a column using a complete expected definition.</summary>
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
        string? collation = null,
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
    public static OperationBuilder<SafeMigrationOperation> DropColumnIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => Add(migrationBuilder, new DropColumnIntent(name, table, schema), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Renames a column when the source exists and the target is free.</summary>
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
    public static OperationBuilder<SafeMigrationOperation> AlterColumnIfDifferent(
        this MigrationBuilder migrationBuilder,
        string table,
        ExpectedColumnDefinition definition,
        ExpectedColumnDefinition? oldDefinition,
        SafeMigrationPolicy policy,
        string? schema = null
    ) => Add(migrationBuilder, new AlterColumnIntent(table, definition, oldDefinition, schema), policy);
}
