namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes an incoming source-model foreign key whose dependent rows must
/// not be changed implicitly by deletion of a model-managed principal row.
/// </summary>
public sealed class ExpectedModelManagedDataForeignKeyDefinition
{
    /// <summary>Initializes an incoming foreign-key definition.</summary>
    /// <param name="table">The dependent table.</param>
    /// <param name="columns">The ordered dependent columns.</param>
    /// <param name="principalColumns">The corresponding ordered principal-table columns.</param>
    /// <param name="schema">The dependent schema, or null for the provider default.</param>
    public ExpectedModelManagedDataForeignKeyDefinition(
        string table,
        IEnumerable<string> columns,
        IEnumerable<string> principalColumns,
        string? schema = null
    )
    {
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        Columns = SafeMigrationDefinitionValidator.Identifiers(columns, nameof(columns));
        PrincipalColumns = SafeMigrationDefinitionValidator.Identifiers(principalColumns, nameof(principalColumns));

        if (Columns.Count != PrincipalColumns.Count)
        {
            throw new ArgumentException(
                "Dependent and principal column lists must have the same length.",
                nameof(principalColumns));
        }
    }

    /// <summary>Gets the dependent table.</summary>
    public string Table { get; }

    /// <summary>Gets the dependent schema when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the ordered dependent columns.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the corresponding ordered principal-table columns.</summary>
    public IReadOnlyList<string> PrincipalColumns { get; }
}
