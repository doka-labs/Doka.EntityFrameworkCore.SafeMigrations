namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures an index using a complete expected definition.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="definition">The complete expected database-object definition.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> EnsureIndex(
        this MigrationBuilder migrationBuilder,
        ExpectedIndexDefinition definition,
        SafeMigrationPolicy policy
    ) => Add(migrationBuilder, new EnsureIndexIntent(definition), policy);

    /// <summary>Ensures a column-based index exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="columns">The ordered index key column names.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    /// <param name="filter">The index predicate, or null for an unfiltered index.</param>
    /// <param name="sortOrders">The ordered key directions, or null for provider defaults.</param>
    /// <param name="nullOrders">The ordered null placements, or null for provider defaults.</param>
    /// <param name="policy">The conflict policy for the operation.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> CreateIndexIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        IEnumerable<string> columns,
        string? schema = null,
        bool unique = false,
        string? filter = null,
        IEnumerable<SafeMigrationIndexSortOrder>? sortOrders = null,
        IEnumerable<SafeMigrationIndexNullOrder>? nullOrders = null,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent
    )
    {
        ArgumentNullException.ThrowIfNull(columns);

        var columnSnapshot = columns.ToArray();
        var sortOrderSnapshot = sortOrders?.ToArray();
        if (sortOrderSnapshot is not null
            && sortOrderSnapshot.Length != columnSnapshot.Length)
        {
            throw new ArgumentException("Sort orders must have the same length as columns.", nameof(sortOrders));
        }

        var nullOrderSnapshot = nullOrders?.ToArray();
        if (nullOrderSnapshot is not null
            && nullOrderSnapshot.Length != columnSnapshot.Length)
        {
            throw new ArgumentException("Null orders must have the same length as columns.", nameof(nullOrders));
        }

        var keys = columnSnapshot.Select((
            column,
            index
        ) => new ExpectedIndexKeyDefinition(
            column,
            sortOrder: sortOrderSnapshot?[index] ?? SafeMigrationIndexSortOrder.ProviderDefault,
            nullOrder: nullOrderSnapshot?[index] ?? SafeMigrationIndexNullOrder.ProviderDefault));

        var definition = new ExpectedIndexDefinition(name, table, keys, schema, unique, filter);

        return EnsureIndex(migrationBuilder, definition, policy);
    }

    /// <summary>
    /// Captures a single-column EF Core index definition emitted by the
    /// scaffolder and converts it to an immutable SafeMigrations operation.
    /// </summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="column">The index key column.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    /// <param name="descending">The key direction, or null for the provider default.</param>
    /// <param name="filter">The index predicate, or null for an unfiltered index.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="migrationBuilder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// EF Core does not append exactly one index operation for the supplied definition.
    /// </exception>
    public static OperationBuilder<SafeMigrationOperation> CreateIndexIfNotExistsFromModel(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string column,
        string? schema = null,
        bool unique = false,
        bool[]? descending = null,
        string? filter = null
    ) => CaptureIndex(
        migrationBuilder,
        name,
        table,
        [column],
        schema,
        unique,
        descending,
        filter);

    /// <summary>
    /// Captures a multi-column EF Core index definition emitted by the
    /// scaffolder and converts it to an immutable SafeMigrations operation.
    /// </summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="columns">The ordered index key columns.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    /// <param name="descending">The ordered key directions, or null for provider defaults.</param>
    /// <param name="filter">The index predicate, or null for an unfiltered index.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="migrationBuilder"/> or <paramref name="columns"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// EF Core does not append exactly one index operation for the supplied definition.
    /// </exception>
    public static OperationBuilder<SafeMigrationOperation> CreateCompositeIndexIfNotExistsFromModel(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string[] columns,
        string? schema = null,
        bool unique = false,
        bool[]? descending = null,
        string? filter = null
    ) => CaptureIndex(migrationBuilder, name, table, columns, schema, unique, descending, filter);

    private static OperationBuilder<SafeMigrationOperation> CaptureIndex(
        MigrationBuilder migrationBuilder,
        string name,
        string table,
        string[] columns,
        string? schema,
        bool unique,
        bool[]? descending,
        string? filter
    )
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentNullException.ThrowIfNull(columns);

        // EF Core remains the authority for provider annotations and argument
        // validation. SafeMigrations replaces only the resulting operation
        // after it has captured that complete provider-owned definition.
        var operationCount = migrationBuilder.Operations.Count;
        _ = migrationBuilder.CreateIndex(
            name: name,
            table: table,
            columns: columns,
            schema: schema,
            unique: unique,
            descending: descending,
            filter: filter);

        if (migrationBuilder.Operations.Count != operationCount + 1
            || migrationBuilder.Operations[^1] is not CreateIndexOperation operation)
        {
            throw new InvalidOperationException("EF Core did not append exactly one CreateIndexOperation.");
        }

        migrationBuilder.Operations.RemoveAt(operationCount);

        var keys = operation.Columns.Select((column, index) => new ExpectedIndexKeyDefinition(
            column,
            sortOrder: operation.IsDescending is null
                ? SafeMigrationIndexSortOrder.ProviderDefault
                : operation.IsDescending[index]
                    ? SafeMigrationIndexSortOrder.Descending
                    : SafeMigrationIndexSortOrder.Ascending));

        var definition = new ExpectedIndexDefinition(
            operation.Name,
            operation.Table,
            keys,
            operation.Schema,
            operation.IsUnique,
            operation.Filter);

        return migrationBuilder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);
    }

    /// <summary>Drops an index when it exists.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> DropIndexIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => Add(migrationBuilder, new DropIndexIntent(name, table, schema), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Renames an index when the source exists and the target is free.</summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="newName">The target database object name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> RenameIndexIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string newName,
        string? schema = null
    ) => Add(
        migrationBuilder,
        new RenameIndexIntent(name, table, newName, schema),
        SafeMigrationPolicy.ThrowIfDifferent);
}
