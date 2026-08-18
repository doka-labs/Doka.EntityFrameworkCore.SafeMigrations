namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>Ensures an index using a complete expected definition.</summary>
    public static OperationBuilder<SafeMigrationOperation> EnsureIndex(
        this MigrationBuilder migrationBuilder,
        ExpectedIndexDefinition definition,
        SafeMigrationPolicy policy
    ) => Add(migrationBuilder, new EnsureIndexIntent(definition), policy);

    /// <summary>Ensures a column-based index exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> CreateIndexIfNotExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        IEnumerable<string> columns,
        string? schema = null,
        bool unique = false,
        string? filter = null,
        IEnumerable<bool>? descending = null,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent
    )
    {
        ArgumentNullException.ThrowIfNull(columns);

        var columnSnapshot = columns.ToArray();
        var descendingSnapshot = descending?.ToArray();
        if (descendingSnapshot is not null
            && descendingSnapshot.Length != columnSnapshot.Length)
        {
            throw new ArgumentException("Descending flags must have the same length as columns.", nameof(descending));
        }

        var keys = columnSnapshot.Select((
            column,
            index
        ) => new ExpectedIndexKeyDefinition(
            column,
            descending: descendingSnapshot is not null && descendingSnapshot[index]));

        var definition = new ExpectedIndexDefinition(name, table, keys, schema, unique, filter);

        return EnsureIndex(migrationBuilder, definition, policy);
    }

    /// <summary>Drops an index when it exists.</summary>
    public static OperationBuilder<SafeMigrationOperation> DropIndexIfExists(
        this MigrationBuilder migrationBuilder,
        string name,
        string table,
        string? schema = null
    ) => Add(migrationBuilder, new DropIndexIntent(name, table, schema), SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Renames an index when the source exists and the target is free.</summary>
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
