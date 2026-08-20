namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents ensuring a schema exists.</summary>
public sealed class EnsureSchemaIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="name">The database object name.</param>
    public EnsureSchemaIntent(
        string name
    ) : base(SafeMigrationOperationKind.EnsureSchema)
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
    }

    /// <summary>Gets the schema name.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string ObjectName => Name;
}

/// <summary>Represents dropping a schema when it exists.</summary>
public sealed class DropSchemaIntent : SafeMigrationIntent
{
    /// <summary>Initializes the intent.</summary>
    /// <param name="name">The database object name.</param>
    public DropSchemaIntent(
        string name
    ) : base(SafeMigrationOperationKind.DropSchema)
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
    }

    /// <summary>Gets the schema name.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override string ObjectName => Name;
}
