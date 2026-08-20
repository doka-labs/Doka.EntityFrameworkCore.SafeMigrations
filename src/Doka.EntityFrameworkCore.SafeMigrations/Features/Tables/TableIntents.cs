namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents ensuring a table exists.</summary>
public sealed class EnsureTableIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="definition">The complete expected database-object definition.</param>
    /// <param name="mode">The table-definition comparison mode.</param>
    public EnsureTableIntent(
        ExpectedTableDefinition definition,
        SafeMigrationTableMode mode
    ) : base(SafeMigrationOperationKind.EnsureTable)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Definition = definition;
        Mode = mode;
    }

    /// <summary>Gets the complete expected table definition.</summary>
    public ExpectedTableDefinition Definition { get; }

    /// <summary>Gets the table comparison mode.</summary>
    public SafeMigrationTableMode Mode { get; }

    /// <inheritdoc />
    public override string ObjectName => Definition.Table;
}

/// <summary>Represents dropping a table when it exists.</summary>
public sealed class DropTableIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    public DropTableIntent(
        string table,
        string? schema = null
    ) : base(SafeMigrationOperationKind.DropTable)
    {
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
    }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <inheritdoc />
    public override string ObjectName => Table;
}

/// <summary>Represents renaming a table when the source exists.</summary>
public sealed class RenameTableIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="name">The database object name.</param>
    /// <param name="newName">The target database object name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="newSchema">The target schema name, or null when unchanged.</param>
    public RenameTableIntent(
        string name,
        string? newName = null,
        string? schema = null,
        string? newSchema = null
    ) : base(SafeMigrationOperationKind.RenameTable)
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        NewName = SafeMigrationDefinitionValidator.Optional(newName, nameof(newName));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        NewSchema = SafeMigrationDefinitionValidator.Optional(newSchema, nameof(newSchema));

        if (NewName is null
            && NewSchema is null)
        {
            throw new ArgumentException("A new table name or schema is required.");
        }
    }

    /// <summary>Gets the current table name.</summary>
    public string Name { get; }

    /// <summary>Gets the new table name when specified.</summary>
    public string? NewName { get; }

    /// <summary>Gets the current schema when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the new schema when specified.</summary>
    public string? NewSchema { get; }

    /// <inheritdoc />
    public override string ObjectName => Name;
}
