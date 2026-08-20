namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents ensuring a check constraint exists.</summary>
public sealed class EnsureCheckConstraintIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="definition">The complete expected database-object definition.</param>
    public EnsureCheckConstraintIntent(
        ExpectedCheckConstraintDefinition definition
    ) : base(SafeMigrationOperationKind.EnsureCheckConstraint)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
    }

    /// <summary>Gets the expected check-constraint definition.</summary>
    public ExpectedCheckConstraintDefinition Definition { get; }

    /// <inheritdoc />
    public override string ObjectName => Definition.Name;
}

/// <summary>Represents dropping a check constraint when it exists.</summary>
public sealed class DropCheckConstraintIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    public DropCheckConstraintIntent(
        string name,
        string table,
        string? schema = null
    ) : base(SafeMigrationOperationKind.DropCheckConstraint)
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
