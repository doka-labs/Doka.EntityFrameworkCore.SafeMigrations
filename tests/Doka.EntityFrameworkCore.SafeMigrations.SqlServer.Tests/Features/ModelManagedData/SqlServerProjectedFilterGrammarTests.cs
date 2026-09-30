namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies the SQL Server-specific filtered-index predicate grammar used by row proofs and catalog planning.
/// </summary>
public sealed class SqlServerProjectedFilterGrammarTests
{
    /// <summary>Gets supported simple comparisons and unsupported general-expression counterexamples.</summary>
    public static IEnumerable<object[]> FilterCases()
    {
        var column = SafeMigrationSql.Identifier("Code");
        var other = SafeMigrationSql.Identifier("Other");
        var equal = new SafeMigrationSqlBinaryExpression(column, SafeMigrationSqlBinaryOperator.Equal,
            SafeMigrationSql.Literal(1));

        yield return [SafeMigrationSql.IsNotNull(column), true];
        yield return [equal, true];
        yield return [new SafeMigrationSqlBinaryExpression(equal, SafeMigrationSqlBinaryOperator.And,
            SafeMigrationSql.IsNotNull(other)), true];
        yield return [new SafeMigrationSqlInExpression(
            column, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]), true];
        yield return [new SafeMigrationSqlBinaryExpression(equal, SafeMigrationSqlBinaryOperator.Or,
            SafeMigrationSql.IsNotNull(other)), false];
        yield return [new SafeMigrationSqlBinaryExpression(column, SafeMigrationSqlBinaryOperator.Equal, other), false];
        yield return [new SafeMigrationSqlBinaryExpression(column, SafeMigrationSqlBinaryOperator.Equal,
            SafeMigrationSql.Literal(null)), false];
        yield return [new SafeMigrationSqlInExpression(column, [SafeMigrationSql.Literal(1)], negated: true), false];
        yield return [new SafeMigrationSqlInExpression(column, [SafeMigrationSql.Literal(null)]), false];
    }

    /// <summary>A general SELECT predicate is not automatically valid filtered-index DDL.</summary>
    [Theory]
    [MemberData(nameof(FilterCases))]
    public void FilterGrammar_UsesOnlyDocumentedCreateIndexPredicateForms(
        SafeMigrationSqlExpression filter,
        bool expected
    )
    {
        // Arrange
        var predicate = filter;

        // Act
        var supported = SqlServerSafeMigrationCatalogSqlBuilder.IsSupportedIndexFilter(predicate);

        // Assert
        Assert.Equal(expected, supported);
    }
}
