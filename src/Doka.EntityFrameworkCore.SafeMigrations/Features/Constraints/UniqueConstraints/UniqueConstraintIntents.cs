namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents ensuring a unique constraint exists.</summary>
public sealed class EnsureUniqueConstraintIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="definition">The complete expected database-object definition.</param>
    public EnsureUniqueConstraintIntent(
        ExpectedUniqueConstraintDefinition definition
    ) : base(SafeMigrationOperationKind.EnsureUniqueConstraint)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
    }

    /// <summary>Gets the expected unique-constraint definition.</summary>
    public ExpectedUniqueConstraintDefinition Definition { get; }

    /// <inheritdoc />
    public override string ObjectName => Definition.Name;
}

/// <summary>Represents dropping a unique constraint when it exists.</summary>
public sealed class DropUniqueConstraintIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    public DropUniqueConstraintIntent(
        string name,
        string table,
        string? schema = null
    ) : base(SafeMigrationOperationKind.DropUniqueConstraint)
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
    }

    /// <summary>Gets the constraint name.</summary>
    public string Name { get; }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <inheritdoc />
    public override string ObjectName => Name;
}
