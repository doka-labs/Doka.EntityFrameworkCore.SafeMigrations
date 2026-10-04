namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    /// <summary>Recognizes only canonical single-column PostgreSQL null-test index predicates.</summary>
    /// <param name="definition">The immutable authored index contract.</param>
    /// <returns>The authored structured filter or a proven canonical null test; otherwise null.</returns>
    private SafeMigrationSqlExpression? GetStructuredIndexFilter(
        ExpectedIndexDefinition definition
    )
    {
        if (definition.StructuredFilter is { } structured)
        {
            return structured;
        }

        if (definition.Filter is not { } sql
            || !SafeMigrationSqlExpressionParser.TryParse(sql, out var parsed)
            || parsed is not SafeMigrationSqlNullTestExpression
            {
                Operand: SafeMigrationSqlIdentifierExpression { Parts.Count: 1 } identifier,
            } nullTest)
        {
            return null;
        }

        // WHY: The shared parser accepts multiple providers' identifier syntax. Exact provider spelling
        // prevents alternate quoting or PostgreSQL's unquoted case folding from changing authored meaning.
        // Trim only PostgreSQL's ASCII SQL whitespace because the raw predicate is preserved for execution.
        var canonical = _sqlGenerationHelper.DelimitIdentifier(identifier.Parts[0])
            + (nullTest.Negated ? " IS NOT NULL" : " IS NULL");

        return sql.AsSpan().Trim(" \t\n\r\f\v".AsSpan()).SequenceEqual(canonical.AsSpan()) ? nullTest : null;
    }

    /// <summary>Resolves the same exact index-column prerequisites for Core projection and runtime SQL.</summary>
    /// <param name="intent">The authored index operation.</param>
    /// <returns>The physical columns required by keys, includes, and a recognized canonical filter.</returns>
    internal IReadOnlyList<string> IndexPrerequisiteColumns(
        EnsureIndexIntent intent
    )
    {
        ArgumentNullException.ThrowIfNull(intent);

        var columns = SafeMigrationPrerequisiteColumns.Local(intent);
        if (GetStructuredIndexFilter(intent.Definition) is not SafeMigrationSqlNullTestExpression
            {
                Operand: SafeMigrationSqlIdentifierExpression { Parts.Count: 1 } identifier,
            })
        {
            return columns;
        }

        var filterColumn = identifier.Parts[0];
        foreach (var column in columns)
        {
            if (StringComparer.Ordinal.Equals(column, filterColumn))
            {
                return columns;
            }
        }

        // WHY: Most null filters reference an existing key column. Reuse the immutable dependency list
        // for that case; only a genuinely additional predicate column needs one exact-sized snapshot.
        var result = new string[columns.Count + 1];
        for (var index = 0; index < columns.Count; index++)
        {
            result[index] = columns[index];
        }

        result[columns.Count] = filterColumn;

        return result;
    }
}
