namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationSqlExpressionRendererTests
{
    /// <summary>Preserves one server-quoted candidate or native-deparsed shape.</summary>
    /// <param name="caseId">The stable catalog candidate identifier.</param>
    [Theory]
    [InlineData("qualified-identifier")]
    [InlineData("keyword-identifier")]
    [InlineData("dollar-identifier")]
    [InlineData("between")]
    [InlineData("in")]
    [InlineData("cast")]
    [InlineData("deparsed-additions")]
    public void RenderCatalogCandidateSql_PreservesServerAuthoritativeShape(string caseId)
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var value = SafeMigrationSql.Identifier("app", "value");

        (SafeMigrationSqlExpression Expression, string Expected, bool Deparsed, bool Exact) data = caseId switch
        {
            "qualified-identifier" => (value,
                "(pg_catalog.quote_ident('app') || '.' || pg_catalog.quote_ident('value'))", false, true),
            "keyword-identifier" => (SafeMigrationSql.Identifier("user"),
                "pg_catalog.quote_ident('user')", false, true),
            "dollar-identifier" => (SafeMigrationSql.Identifier("a$b"),
                "pg_catalog.quote_ident('a$b')", false, true),
            "between" => (SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10)),
                "('((' || pg_catalog.quote_ident('app') || '.' || pg_catalog.quote_ident('value') "
                + "|| ' >= 1) AND (' || pg_catalog.quote_ident('app') || '.' "
                + "|| pg_catalog.quote_ident('value') || ' <= 10))')", false, true),
            "in" => (SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
                " = ANY (ARRAY[1, 2])", false, false),
            "cast" => (SafeMigrationSql.Cast(value, "text"), ")::text", false, false),
            "deparsed-additions" => (SafeMigrationSql.Binary(
                    SafeMigrationSql.Binary(SafeMigrationSql.Identifier("user"), SafeMigrationSqlBinaryOperator.Add,
                        SafeMigrationSql.Identifier("a$b")),
                    SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Identifier("ordinary")),
                "('(' || pg_catalog.quote_ident('user') || ' + ' || pg_catalog.quote_ident('a$b') "
                + "|| ' + ' || pg_catalog.quote_ident('ordinary') || ')')", true, true),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Unknown catalog candidate case."),
        };

        // Act
        var actual = data.Deparsed
            ? renderer.RenderCatalogDeparsedCandidateSql(data.Expression, Literal)
            : renderer.RenderCatalogCandidateSql(data.Expression, Literal);

        // Assert
        if (data.Exact)
        {
            Assert.Equal(data.Expected, actual);
        }
        else
        {
            Assert.Contains(data.Expected, actual, StringComparison.Ordinal);
        }
    }
}
