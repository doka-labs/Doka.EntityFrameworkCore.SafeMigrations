namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Describes the complete expected primary-key definition.</summary>
public sealed class ExpectedPrimaryKeyDefinition
{
    /// <summary>Initializes an expected primary key.</summary>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="columns">The ordered primary-key column names.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    public ExpectedPrimaryKeyDefinition(
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

    /// <summary>Gets the declared constraint name.</summary>
    public string Name { get; }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the ordered key columns.</summary>
    public IReadOnlyList<string> Columns { get; }
}
