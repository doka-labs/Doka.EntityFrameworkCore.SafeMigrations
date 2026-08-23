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
