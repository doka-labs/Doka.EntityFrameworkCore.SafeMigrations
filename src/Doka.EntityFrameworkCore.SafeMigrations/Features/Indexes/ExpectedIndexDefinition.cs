namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes one ordered key part of an index.
/// </summary>
public sealed class ExpectedIndexKeyDefinition
{
    /// <summary>Initializes an expected index key.</summary>
    /// <param name="column">The column name.</param>
    /// <param name="expression">The index or check expression, or null when absent.</param>
    /// <param name="sortOrder">The requested sort direction.</param>
    /// <param name="nullOrder">The requested null placement.</param>
    /// <param name="prefixLength">The index prefix length, or null when unspecified.</param>
    /// <param name="collation">
    /// The expected collation identity, or null for the provider-default key
    /// semantics. Null never disables comparison.
    /// </param>
    /// <param name="operatorClass">The provider operator class, or null when unspecified.</param>
    /// <param name="structuredExpression">The structured functional-key expression, or null when absent.</param>
    public ExpectedIndexKeyDefinition(
        string? column = null,
        string? expression = null,
        SafeMigrationIndexSortOrder sortOrder = SafeMigrationIndexSortOrder.ProviderDefault,
        SafeMigrationIndexNullOrder nullOrder = SafeMigrationIndexNullOrder.ProviderDefault,
        int? prefixLength = null,
        SafeMigrationCollationIdentifier? collation = null,
        string? operatorClass = null,
        SafeMigrationSqlExpression? structuredExpression = null
    )
    {
        Column = SafeMigrationDefinitionValidator.Optional(column, nameof(column));
        Expression = SafeMigrationDefinitionValidator.Optional(expression, nameof(expression));
        StructuredExpression = structuredExpression;

        var expressionCount = (Column is null ? 0 : 1)
            + (Expression is null ? 0 : 1)
            + (StructuredExpression is null ? 0 : 1);

        if (expressionCount != 1)
        {
            throw new ArgumentException("Exactly one of column or expression must be specified.");
        }

        if (prefixLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength), "Prefix length must be positive.");
        }

        if (!Enum.IsDefined(sortOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder));
        }

        if (!Enum.IsDefined(nullOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(nullOrder));
        }

        SortOrder = sortOrder;
        NullOrder = nullOrder;
        PrefixLength = prefixLength;
        Collation = collation;
        OperatorClass = SafeMigrationDefinitionValidator.Optional(operatorClass, nameof(operatorClass));
    }

    /// <summary>Gets the column name for a column key.</summary>
    public string? Column { get; }

    /// <summary>Gets the expression for a functional key.</summary>
    public string? Expression { get; }

    /// <summary>Gets the structured functional-key expression when specified.</summary>
    public SafeMigrationSqlExpression? StructuredExpression { get; }

    /// <summary>Gets the requested sort direction.</summary>
    public SafeMigrationIndexSortOrder SortOrder { get; }

    /// <summary>Gets the requested null placement.</summary>
    public SafeMigrationIndexNullOrder NullOrder { get; }

    /// <summary>Gets the index prefix length when specified.</summary>
    public int? PrefixLength { get; }

    /// <summary>Gets the key collation identity when specified.</summary>
    public SafeMigrationCollationIdentifier? Collation { get; }

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
    /// <param name="structuredFilter">The structured filter expression, or null when unfiltered.</param>
    public ExpectedIndexDefinition(
        string name,
        string table,
        IEnumerable<ExpectedIndexKeyDefinition> keys,
        string? schema = null,
        bool unique = false,
        string? filter = null,
        IEnumerable<string>? includedColumns = null,
        string? method = null,
        bool? nullsDistinct = null,
        SafeMigrationSqlExpression? structuredFilter = null
    )
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        Keys = SafeMigrationDefinitionValidator.Definitions(keys, nameof(keys), allowEmpty: false);
        Unique = unique;
        Filter = SafeMigrationDefinitionValidator.Optional(filter, nameof(filter));
        StructuredFilter = structuredFilter;
        if (Filter is not null
            && StructuredFilter is not null)
        {
            throw new ArgumentException(
                "An index filter must use either opaque SQL or a structured expression, not both.",
                nameof(structuredFilter));
        }

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

    /// <summary>Gets the structured filter expression when specified.</summary>
    public SafeMigrationSqlExpression? StructuredFilter { get; }

    /// <summary>Gets non-key columns included by the index.</summary>
    public IReadOnlyList<string> IncludedColumns { get; }

    /// <summary>Gets the index access method when specified.</summary>
    public string? Method { get; }

    /// <summary>Gets the null-distinctness policy when specified.</summary>
    public bool? NullsDistinct { get; }
}
