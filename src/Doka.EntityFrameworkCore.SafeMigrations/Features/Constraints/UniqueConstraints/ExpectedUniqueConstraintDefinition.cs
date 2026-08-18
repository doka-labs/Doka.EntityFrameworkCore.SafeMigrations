namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Describes a complete expected unique-constraint definition.</summary>
public sealed class ExpectedUniqueConstraintDefinition
{
    /// <summary>Initializes an expected unique constraint.</summary>
    public ExpectedUniqueConstraintDefinition(
        string name,
        string table,
        IEnumerable<string> columns,
        string? schema = null
    )
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        Columns = SafeMigrationDefinitionValidator.Identifiers(columns, nameof(columns));
    }

    /// <summary>Gets the constraint name.</summary>
    public string Name { get; }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the ordered constrained columns.</summary>
    public IReadOnlyList<string> Columns { get; }
}
