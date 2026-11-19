namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies the SQL Server-specific filtered-index predicate grammar used by row proofs and catalog planning.
/// </summary>
public sealed class SqlServerProjectedFilterGrammarTests
{
    /// <summary>Gets supported simple comparisons and unsupported general-expression counterexamples.</summary>
    public static TheoryData<string, bool> FilterCases() => new()
    {
        { "not-null", true },
        { "equal", true },
        { "and", true },
        { "in", true },
        { "or", false },
        { "column-equality", false },
        { "null-equality", false },
        { "not-in", false },
        { "null-in", false },
    };

    /// <summary>Creates one predicate without asking test discovery to serialize its expression graph.</summary>
    /// <param name="source">The closed, serializable predicate-case identity.</param>
    /// <returns>The unchanged predicate shape used by native grammar validation.</returns>
    private static SafeMigrationSqlExpression Filter(
        string source
    )
    {
        var column = SafeMigrationSql.Identifier("Code");
        var other = SafeMigrationSql.Identifier("Other");
        var equal = new SafeMigrationSqlBinaryExpression(
            column,
            SafeMigrationSqlBinaryOperator.Equal,
            SafeMigrationSql.Literal(1));

        return source switch
        {
            "not-null" => SafeMigrationSql.IsNotNull(column),
            "equal" => equal,
            "and" => new SafeMigrationSqlBinaryExpression(
                equal,
                SafeMigrationSqlBinaryOperator.And,
                SafeMigrationSql.IsNotNull(other)),
            "in" => new SafeMigrationSqlInExpression(
                column,
                [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
            "or" => new SafeMigrationSqlBinaryExpression(
                equal,
                SafeMigrationSqlBinaryOperator.Or,
                SafeMigrationSql.IsNotNull(other)),
            "column-equality" => new SafeMigrationSqlBinaryExpression(
                column,
                SafeMigrationSqlBinaryOperator.Equal,
                other),
            "null-equality" => new SafeMigrationSqlBinaryExpression(
                column,
                SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal(null)),
            "not-in" => new SafeMigrationSqlInExpression(column, [SafeMigrationSql.Literal(1)], negated: true),
            "null-in" => new SafeMigrationSqlInExpression(column, [SafeMigrationSql.Literal(null)]),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }

    /// <summary>A general SELECT predicate is not automatically valid filtered-index DDL.</summary>
    /// <param name="source">The discoverable identity of the unchanged predicate graph.</param>
    /// <param name="expected">Whether native filtered-index grammar supports the expression.</param>
    [Theory]
    [MemberData(nameof(FilterCases))]
    public void FilterGrammar_UsesOnlyDocumentedCreateIndexPredicateForms(
        string source,
        bool expected
    )
    {
        // Arrange
        var predicate = Filter(source);

        // Act
        var supported = SqlServerSafeMigrationCatalogSqlBuilder.IsSupportedIndexFilter(predicate);

        // Assert
        Assert.Equal(expected, supported);
    }
}
