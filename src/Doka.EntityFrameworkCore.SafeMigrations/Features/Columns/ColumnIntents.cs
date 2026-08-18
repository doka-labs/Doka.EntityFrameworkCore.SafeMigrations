namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents ensuring a column exists.</summary>
public sealed class EnsureColumnIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    public EnsureColumnIntent(
        string table,
        ExpectedColumnDefinition definition,
        string? schema = null
    ) : base(SafeMigrationOperationKind.EnsureColumn)
    {
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
    }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the expected column definition.</summary>
    public ExpectedColumnDefinition Definition { get; }

    /// <inheritdoc />
    public override string ObjectName => Definition.Name;
}

/// <summary>Represents dropping a column when it exists.</summary>
public sealed class DropColumnIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    public DropColumnIntent(
        string name,
        string table,
        string? schema = null
    ) : base(SafeMigrationOperationKind.DropColumn)
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
    }

    /// <summary>Gets the column name.</summary>
    public string Name { get; }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <inheritdoc />
    public override string ObjectName => Name;
}

/// <summary>Represents renaming a column when the source exists.</summary>
public sealed class RenameColumnIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    public RenameColumnIntent(
        string name,
        string table,
        string newName,
        string? schema = null
    ) : base(SafeMigrationOperationKind.RenameColumn)
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        NewName = SafeMigrationDefinitionValidator.Required(newName, nameof(newName));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
    }

    /// <summary>Gets the current column name.</summary>
    public string Name { get; }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the new column name.</summary>
    public string NewName { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <inheritdoc />
    public override string ObjectName => Name;
}

/// <summary>Represents altering a column when its definition differs.</summary>
public sealed class AlterColumnIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    public AlterColumnIntent(
        string table,
        ExpectedColumnDefinition definition,
        ExpectedColumnDefinition? oldDefinition = null,
        string? schema = null
    ) : base(SafeMigrationOperationKind.AlterColumn)
    {
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));

        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
        OldDefinition = oldDefinition;
        if (OldDefinition is not null
            && !StringComparer.Ordinal.Equals(Definition.Name, OldDefinition.Name))
        {
            throw new ArgumentException(
                "The old and target column definitions must identify the same column.",
                nameof(oldDefinition));
        }
    }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the target column definition.</summary>
    public ExpectedColumnDefinition Definition { get; }

    /// <summary>Gets the prior model definition when available.</summary>
    public ExpectedColumnDefinition? OldDefinition { get; }

    /// <inheritdoc />
    public override string ObjectName => Definition.Name;
}
