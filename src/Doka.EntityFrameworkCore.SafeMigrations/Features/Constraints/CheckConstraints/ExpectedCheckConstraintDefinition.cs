namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Describes a complete expected check-constraint definition.</summary>
public sealed class ExpectedCheckConstraintDefinition
{
    /// <summary>Gets the original provider SQL accompanying a successfully parsed EF check expression.</summary>
    /// <remarks>The source is not an opaque-SQL approval and never replaces the structured proof boundary.</remarks>
    internal string? CapturedSql { get; init; }

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

    private ExpectedCheckConstraintDefinition(
        string name,
        string table,
        SafeMigrationSqlExpression expression,
        string? schema
    )
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        ArgumentNullException.ThrowIfNull(expression);
        Expression = expression;
    }

    /// <summary>Creates an expected check constraint with a structured expression.</summary>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="expression">The structured SQL expression.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <returns>The complete expected check-constraint definition.</returns>
    public static ExpectedCheckConstraintDefinition FromExpression(
        string name,
        string table,
        SafeMigrationSqlExpression expression,
        string? schema = null
    ) => new(name, table, expression, schema);

    /// <summary>Retains native SQL spelling beside its separately parsed identifier and expression roles.</summary>
    internal static ExpectedCheckConstraintDefinition FromCapturedExpression(
        string name,
        string table,
        SafeMigrationSqlExpression expression,
        string sql,
        string? schema
    ) => new(name, table, expression, schema) { CapturedSql = sql, };

    /// <summary>Gets the constraint name.</summary>
    public string Name { get; }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the expected SQL expression.</summary>
    public string? Sql { get; }

    /// <summary>Gets the structured SQL expression when specified.</summary>
    public SafeMigrationSqlExpression? Expression { get; }
}
