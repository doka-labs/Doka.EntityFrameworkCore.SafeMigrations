namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlSafeMigrationSqlExpressionRendererTests
{
    [Fact]
    public void Render_ProducesDeterministicSqlForEveryStructuredNode()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var value = SafeMigrationSql.Identifier("app", "Value");

        var expectations = new (SafeMigrationSqlExpression Expression, string Sql)[]
        {
            (value, "app.\"Value\""), (SafeMigrationSql.Literal(null), "NULL"),
            (SafeMigrationSql.Literal(42), "42"), (SafeMigrationSql.Literal(42, "bigint"), "42::bigint"),
            (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value), "(NOT app.\"Value\")"),
            (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, value), "(-app.\"Value\")"),
            (SafeMigrationSql.IsNull(value), "(app.\"Value\" IS NULL)"),
            (SafeMigrationSql.IsNotNull(value), "(app.\"Value\" IS NOT NULL)"),
            (SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10)),
                "(app.\"Value\" BETWEEN 1 AND 10)"),
            (SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
                "(app.\"Value\" IN (1, 2))"),
            (SafeMigrationSql.Function("LOWER", value), "lower(app.\"Value\")"),
            (SafeMigrationSql.Cast(value, "text"), "CAST(app.\"Value\" AS text)"),
            (SafeMigrationSql.Collate(value, "C", "pg_catalog"), "(app.\"Value\" COLLATE pg_catalog.\"C\")"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Date), "CURRENT_DATE"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time, precision: 3), "CURRENT_TIME(3)"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp), "CURRENT_TIMESTAMP"),
            (SafeMigrationSql.ProviderFragment("npgsql_postgresql", "CURRENT_USER"), "CURRENT_USER"),
            (SafeMigrationSql.Opaque("value + 1"), "value + 1"),
        };

        foreach (var expectation in expectations)
        {
            Assert.Equal(expectation.Sql, renderer.Render(expectation.Expression));
        }
    }

    [Fact]
    public void RenderCatalogCandidateSql_UsesServerAuthoritativeIdentifierQuotingAndCatalogShapes()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var value = SafeMigrationSql.Identifier("app", "value");

        Assert.Equal(
            "(pg_catalog.quote_ident('app') || '.' || pg_catalog.quote_ident('value'))",
            renderer.RenderCatalogCandidateSql(value, Literal));
        Assert.Equal(
            "pg_catalog.quote_ident('user')",
            renderer.RenderCatalogCandidateSql(SafeMigrationSql.Identifier("user"), Literal));
        Assert.Equal(
            "pg_catalog.quote_ident('a$b')",
            renderer.RenderCatalogCandidateSql(SafeMigrationSql.Identifier("a$b"), Literal));
        Assert.Equal(
            "('((' || pg_catalog.quote_ident('app') || '.' || pg_catalog.quote_ident('value') || ' >= 1) AND (' "
            + "|| pg_catalog.quote_ident('app') || '.' || pg_catalog.quote_ident('value') || ' <= 10))')",
            renderer.RenderCatalogCandidateSql(
                SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10)),
                Literal));

        var inCandidate = renderer.RenderCatalogCandidateSql(
            SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
            Literal);
        var castCandidate = renderer.RenderCatalogCandidateSql(SafeMigrationSql.Cast(value, "text"), Literal);
        var deparsedCandidate = renderer.RenderCatalogDeparsedCandidateSql(
            SafeMigrationSql.Binary(
                SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("user"),
                    SafeMigrationSqlBinaryOperator.Add,
                    SafeMigrationSql.Identifier("a$b")),
                SafeMigrationSqlBinaryOperator.Add,
                SafeMigrationSql.Identifier("ordinary")),
            Literal);

        Assert.Contains(" = ANY (ARRAY[1, 2])", inCandidate, StringComparison.Ordinal);
        Assert.Contains(")::text", castCandidate, StringComparison.Ordinal);
        Assert.Equal(
            "('(' || pg_catalog.quote_ident('user') || ' + ' || pg_catalog.quote_ident('a$b') || ' + ' "
            + "|| pg_catalog.quote_ident('ordinary') || ')')",
            deparsedCandidate);
    }

    [Fact]
    public void Render_ProducesEveryBinaryOperatorAndRejectsUnsupportedValues()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var operators = Enum.GetValues<SafeMigrationSqlBinaryOperator>();

        var rendered = operators
            .Select(value => renderer.Render(
                SafeMigrationSql.Binary(SafeMigrationSql.Literal(1), value, SafeMigrationSql.Literal(2))))
            .ToArray();

        Assert.Equal(13, rendered.Length);
        Assert.Contains("(1 % 2)", rendered, StringComparer.Ordinal);
        Assert.Throws<ArgumentNullException>(() => renderer.Render(null!));
        Assert.Throws<NotSupportedException>(() => renderer.Render(SafeMigrationSql.Literal(new object())));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.ProviderFragment("other", "CURRENT_USER")));
    }

    private static SafeMigrationDbContext CreateContext() =>
        new("Host=localhost;Database=renderer;Username=test;Password=test");

    private static PostgreSqlSafeMigrationSqlExpressionRenderer CreateRenderer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static string Literal(
        string value
    ) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
