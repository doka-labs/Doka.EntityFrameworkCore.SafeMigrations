namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>
    /// Ensures a table using a complete expected definition and an explicit
    /// table comparison mode.
    /// </summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="definition">The complete expected database-object definition.</param>
    /// <param name="mode">The table-definition comparison mode.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> EnsureTable(
        this MigrationBuilder migrationBuilder,
        ExpectedTableDefinition definition,
        SafeMigrationTableMode mode,
        SafeMigrationPolicy policy
    ) => Add(migrationBuilder, new EnsureTableIntent(definition, mode), policy);

    /// <summary>
    /// Creates a typed EF table definition and wraps its immutable snapshot in
    /// a fail-closed SafeMigrations operation.
    /// </summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="table">The table name.</param>
    /// <param name="columns">The callback that defines the typed EF Core table columns.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="constraints">The callback that configures table constraints.</param>
    /// <param name="comment">The expected database comment, or null when unspecified.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <param name="mode">The table-definition comparison mode.</param>
    /// <typeparam name="TColumns">The anonymous type that exposes the configured table columns.</typeparam>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> CreateTableIfNotExists<TColumns>(
        this MigrationBuilder migrationBuilder,
        string table,
        Func<ColumnsBuilder, TColumns> columns,
        string? schema = null,
        Action<CreateTableBuilder<TColumns>>? constraints = null,
        string? comment = null,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent,
        SafeMigrationTableMode mode = SafeMigrationTableMode.StrictDefinition
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentNullException.ThrowIfNull(columns);

        var operationCount = migrationBuilder.Operations.Count;
        _ = migrationBuilder.CreateTable(table, columns, schema, constraints, comment);

        if (migrationBuilder.Operations.Count != operationCount + 1
            || migrationBuilder.Operations[^1] is not CreateTableOperation operation)
        {
            throw new InvalidOperationException("EF Core did not append exactly one CreateTableOperation.");
        }

        migrationBuilder.Operations.RemoveAt(operationCount);
        return Add(
            migrationBuilder,
            new EnsureTableIntent(SafeMigrationExpectedDefinitionFactory.From(operation), mode),
            policy);
    }

    /// <summary>
    /// Emits a complete object-granular convergence baseline. A missing table
    /// is created from the full definition; an existing partial table is kept
    /// as a container and completed by the following column, constraint and
    /// index operations.
    /// </summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="definition">The complete expected database-object definition.</param>
    /// <param name="indexes">The expected indexes.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <returns>The same migration builder after all convergence operations have been appended.</returns>
    public static MigrationBuilder ConvergeTable(
        this MigrationBuilder migrationBuilder,
        ExpectedTableDefinition definition,
        IEnumerable<ExpectedIndexDefinition>? indexes = null,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentNullException.ThrowIfNull(definition);

        var indexSnapshot = SafeMigrationDefinitionValidator.Definitions(indexes ?? [], nameof(indexes));
        if (indexSnapshot.Any(index => !StringComparer.Ordinal.Equals(index.Table, definition.Table)
                || !StringComparer.Ordinal.Equals(index.Schema, definition.Schema)))
        {
            throw new ArgumentException(
                "Every convergence index must target the supplied table and schema.",
                nameof(indexes));
        }

        _ = migrationBuilder.EnsureTable(
            definition,
            SafeMigrationTableMode.ConvergenceContainer,
            SafeMigrationPolicy.ExistenceOnly);

        foreach (var column in definition.Columns)
        {
            _ = migrationBuilder.EnsureColumn(definition.Table, column, policy, definition.Schema);
        }

        if (definition.PrimaryKey is not null)
        {
            _ = migrationBuilder.EnsurePrimaryKey(definition.PrimaryKey, policy);
        }

        foreach (var constraint in definition.UniqueConstraints)
        {
            _ = migrationBuilder.EnsureUniqueConstraint(constraint, policy);
        }

        foreach (var constraint in definition.CheckConstraints)
        {
            _ = migrationBuilder.EnsureCheckConstraint(constraint, policy);
        }

        foreach (var constraint in definition.ForeignKeys)
        {
            _ = migrationBuilder.EnsureForeignKey(constraint, policy);
        }

        foreach (var index in indexSnapshot)
        {
            _ = migrationBuilder.EnsureIndex(index, policy);
        }

        return migrationBuilder;
    }

    /// <summary>Drops a table when it exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> DropTableIfExists(
        this MigrationBuilder migrationBuilder,
        string table,
        string? schema = null
    ) => Add(migrationBuilder, new DropTableIntent(table, schema), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Renames a table when the source exists and the target is free.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="newName">The target database object name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="newSchema">The target schema name, or null when unchanged.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> RenameTableIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string? newName = null,
        string? schema = null,
        string? newSchema = null
    ) => Add(
        migrationBuilder,
        new RenameTableIntent(name, newName, schema, newSchema),
        SafeMigrationPolicy.ThrowIfDifferent);
}
