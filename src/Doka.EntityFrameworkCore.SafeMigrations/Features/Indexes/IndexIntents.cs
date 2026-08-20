namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents ensuring an index exists.</summary>
public sealed class EnsureIndexIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="definition">The complete expected database-object definition.</param>
    public EnsureIndexIntent(
        ExpectedIndexDefinition definition
    ) : base(SafeMigrationOperationKind.EnsureIndex)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
    }

    /// <summary>Gets the expected index definition.</summary>
    public ExpectedIndexDefinition Definition { get; }

    /// <inheritdoc />
    public override string ObjectName => Definition.Name;
}

/// <summary>Represents dropping an index when it exists.</summary>
public sealed class DropIndexIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    public DropIndexIntent(
        string name,
        string table,
        string? schema = null
    ) : base(SafeMigrationOperationKind.DropIndex)
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
    }

    /// <summary>Gets the index name.</summary>
    public string Name { get; }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <inheritdoc />
    public override string ObjectName => Name;
}

/// <summary>Represents renaming an index when the source exists.</summary>
public sealed class RenameIndexIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="newName">The target database object name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    public RenameIndexIntent(
        string name,
        string table,
        string newName,
        string? schema = null
    ) : base(SafeMigrationOperationKind.RenameIndex)
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        NewName = SafeMigrationDefinitionValidator.Required(newName, nameof(newName));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
    }

    /// <summary>Gets the current index name.</summary>
    public string Name { get; }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the new index name.</summary>
    public string NewName { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <inheritdoc />
    public override string ObjectName => Name;
}
