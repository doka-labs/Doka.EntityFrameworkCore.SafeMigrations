namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents ensuring a foreign key exists.</summary>
public sealed class EnsureForeignKeyIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    public EnsureForeignKeyIntent(
        ExpectedForeignKeyDefinition definition
    ) : base(SafeMigrationOperationKind.EnsureForeignKey)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
    }

    /// <summary>Gets the expected foreign-key definition.</summary>
    public ExpectedForeignKeyDefinition Definition { get; }

    /// <inheritdoc />
    public override string ObjectName => Definition.Name;
}

/// <summary>Represents dropping a foreign key when it exists.</summary>
public sealed class DropForeignKeyIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    public DropForeignKeyIntent(
        string name,
        string table,
        string? schema = null
    ) : base(SafeMigrationOperationKind.DropForeignKey)
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
