namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes one ordered key part of an index.
/// </summary>
public sealed class ExpectedIndexKeyDefinition
{
    /// <summary>Initializes an expected index key.</summary>
    /// <param name="column">The column name.</param>
    /// <param name="expression">The index or check expression, or null when absent.</param>
    /// <param name="descending">The ordered descending flags, or null for ascending keys.</param>
    /// <param name="prefixLength">The index prefix length, or null when unspecified.</param>
    /// <param name="collation">The expected database collation, or null when unspecified.</param>
    /// <param name="operatorClass">The provider operator class, or null when unspecified.</param>
    public ExpectedIndexKeyDefinition(
        string? column = null,
        string? expression = null,
        bool descending = false,
        int? prefixLength = null,
        string? collation = null,
        string? operatorClass = null
    )
    {
        Column = SafeMigrationDefinitionValidator.Optional(column, nameof(column));
        Expression = SafeMigrationDefinitionValidator.Optional(expression, nameof(expression));

        if ((Column is null) == (Expression is null))
        {
            throw new ArgumentException("Exactly one of column or expression must be specified.");
        }

        if (prefixLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength), "Prefix length must be positive.");
        }

        Descending = descending;
        PrefixLength = prefixLength;
        Collation = SafeMigrationDefinitionValidator.Optional(collation, nameof(collation));
        OperatorClass = SafeMigrationDefinitionValidator.Optional(operatorClass, nameof(operatorClass));
    }

    /// <summary>Gets the column name for a column key.</summary>
    public string? Column { get; }

    /// <summary>Gets the expression for a functional key.</summary>
    public string? Expression { get; }

    /// <summary>Gets whether this key part is descending.</summary>
    public bool Descending { get; }

    /// <summary>Gets the index prefix length when specified.</summary>
    public int? PrefixLength { get; }

    /// <summary>Gets the key collation when specified.</summary>
    public string? Collation { get; }

    /// <summary>Gets the operator class when specified.</summary>
    public string? OperatorClass { get; }
}

/// <summary>
/// Describes the complete provider-neutral definition of an index.
/// </summary>
public sealed class ExpectedIndexDefinition
{
    /// <summary>Initializes an expected index definition.</summary>
    /// <param name="name">The database object name.</param>
    /// <param name="table">The table name.</param>
    /// <param name="keys">The ordered index keys.</param>
    /// <param name="schema">The schema name, or null for the provider default.</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    /// <param name="filter">The index predicate, or null for an unfiltered index.</param>
    /// <param name="includedColumns">The non-key columns included by the index.</param>
    /// <param name="method">The provider index method, or null when unspecified.</param>
    /// <param name="nullsDistinct">The unique-index null-distinctness setting, or null when unspecified.</param>
    public ExpectedIndexDefinition(
        string name,
        string table,
        IEnumerable<ExpectedIndexKeyDefinition> keys,
        string? schema = null,
        bool unique = false,
        string? filter = null,
        IEnumerable<string>? includedColumns = null,
        string? method = null,
        bool? nullsDistinct = null
    )
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        Keys = SafeMigrationDefinitionValidator.Definitions(keys, nameof(keys), allowEmpty: false);
        Unique = unique;
        Filter = SafeMigrationDefinitionValidator.Optional(filter, nameof(filter));
        IncludedColumns = SafeMigrationDefinitionValidator.Identifiers(
            includedColumns ?? [],
            nameof(includedColumns),
            allowEmpty: true);

        Method = SafeMigrationDefinitionValidator.Optional(method, nameof(method));
        NullsDistinct = nullsDistinct;

        if (NullsDistinct is not null
            && !Unique)
        {
            throw new ArgumentException(
                "Null-distinctness is meaningful only for a unique index.",
                nameof(nullsDistinct));
        }

        var keyColumns = Keys
            .Select(static key => key.Column)
            .Where(static column => column is not null)
            .ToHashSet(StringComparer.Ordinal);

        if (IncludedColumns.Any(keyColumns.Contains))
        {
            throw new ArgumentException(
                "An included column must not also be an index key column.",
                nameof(includedColumns));
        }
    }

    /// <summary>Gets the index name.</summary>
    public string Name { get; }

    /// <summary>Gets the table name.</summary>
    public string Table { get; }

    /// <summary>Gets the schema name when specified.</summary>
    public string? Schema { get; }

    /// <summary>Gets the ordered index keys.</summary>
    public IReadOnlyList<ExpectedIndexKeyDefinition> Keys { get; }

    /// <summary>Gets whether the index is unique.</summary>
    public bool Unique { get; }

    /// <summary>Gets the filter expression when specified.</summary>
    public string? Filter { get; }

    /// <summary>Gets non-key columns included by the index.</summary>
    public IReadOnlyList<string> IncludedColumns { get; }

    /// <summary>Gets the index access method when specified.</summary>
    public string? Method { get; }

    /// <summary>Gets the null-distinctness policy when specified.</summary>
    public bool? NullsDistinct { get; }
}
