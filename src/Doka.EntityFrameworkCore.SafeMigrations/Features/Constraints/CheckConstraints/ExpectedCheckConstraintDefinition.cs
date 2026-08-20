namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Describes a complete expected check-constraint definition.</summary>
public sealed class ExpectedCheckConstraintDefinition
{
    /// <summary>Initializes an expected check constraint.</summary>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="sql">The SQL expression.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    public ExpectedCheckConstraintDefinition(
        string name,
        string table,
        string sql,
        string? schema = null
    )
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        Sql = SafeMigrationDefinitionValidator.Required(sql, nameof(sql));
    }

    /// <summary>Gets the constraint name.</summary>
    public string Name { get; }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the expected SQL expression.</summary>
    public string Sql { get; }
}
