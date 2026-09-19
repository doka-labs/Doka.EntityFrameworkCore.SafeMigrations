namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqliteSafeMigrationSqlExpressionRendererTests
{
    [Fact]
    public void Render_ProducesDeterministicSqlForEveryStructuredNode()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var value = SafeMigrationSql.Identifier("app", "Value");
        var expectations = new (SafeMigrationSqlExpression Expression, string Sql)[]
        {
            (value, "\"app\".\"Value\""),
            (SafeMigrationSql.Literal(null), "NULL"),
            (SafeMigrationSql.Literal(null, "INTEGER"), "CAST(NULL AS INTEGER)"),
            (SafeMigrationSql.Literal(42), "42"),
            (SafeMigrationSql.Literal(42, "INTEGER"), "CAST(42 AS INTEGER)"),
            (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value), "(NOT \"app\".\"Value\")"),
            (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, value), "(-\"app\".\"Value\")"),
            (SafeMigrationSql.IsNull(value), "(\"app\".\"Value\" IS NULL)"),
            (SafeMigrationSql.IsNotNull(value), "(\"app\".\"Value\" IS NOT NULL)"),
            (SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10)),
                "(\"app\".\"Value\" BETWEEN 1 AND 10)"),
            (SafeMigrationSql.Between(
                    value,
                    SafeMigrationSql.Literal(1),
                    SafeMigrationSql.Literal(10),
                    negated: true),
                "(\"app\".\"Value\" NOT BETWEEN 1 AND 10)"),
            (SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
                "(\"app\".\"Value\" IN (1, 2))"),
            (SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1)], negated: true),
                "(\"app\".\"Value\" NOT IN (1))"),
            (SafeMigrationSql.Function("LOWER", value), "LOWER(\"app\".\"Value\")"),
            (SafeMigrationSql.Cast(value, "TEXT"), "CAST(\"app\".\"Value\" AS TEXT)"),
            (SafeMigrationSql.Collate(value, "NOCASE"), "(\"app\".\"Value\" COLLATE \"NOCASE\")"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Date), "CURRENT_DATE"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time), "CURRENT_TIME"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp), "CURRENT_TIMESTAMP"),
            (SafeMigrationSql.ProviderFragment("microsoft_sqlite", "CURRENT_USER"), "CURRENT_USER"),
            (SafeMigrationSql.Opaque("value + 1"), "value + 1"),
        };

        foreach (var expectation in expectations)
        {
            var sql = renderer.Render(expectation.Expression);
            var unsupportedFeature = renderer.GetUnsupportedFeature(expectation.Expression);

            Assert.Equal(expectation.Sql, sql);
            Assert.Null(unsupportedFeature);
        }

    }

    [Fact]
    public void Render_ProducesEveryBinaryOperator()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var operators = Enum.GetValues<SafeMigrationSqlBinaryOperator>();

        var rendered = operators
            .Select(value => renderer.Render(
                SafeMigrationSql.Binary(SafeMigrationSql.Literal(1), value, SafeMigrationSql.Literal(2))))
            .ToArray();

        Assert.Equal(
            [
                "(1 AND 2)",
                "(1 OR 2)",
                "(1 = 2)",
                "(1 <> 2)",
                "(1 < 2)",
                "(1 <= 2)",
                "(1 > 2)",
                "(1 >= 2)",
                "(1 + 2)",
                "(1 - 2)",
                "(1 * 2)",
                "(1 / 2)",
                "(1 % 2)"
            ],
            rendered);
    }

    [Fact]
    public void GetUnsupportedFeature_ClassifiesNestedProviderBoundaries()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var unknownLiteral = SafeMigrationSql.Literal(new object());
        var nestedUnknownLiteral = SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Literal(0), unknownLiteral);
        var unsafeStoreType = SafeMigrationSql.Cast(
            SafeMigrationSql.Identifier("value"),
            "INTEGER); DROP TABLE items; --");

        var qualifiedCollation = SafeMigrationSql.Collate(
            SafeMigrationSql.Identifier("value"),
            "NOCASE",
            "main");

        var preciseCurrentValue = SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp, precision: 6);
        var foreignFragment = SafeMigrationSql.ProviderFragment("foreign_provider", "CURRENT_USER");

        Assert.Equal("structured_literal_mapping", renderer.GetUnsupportedFeature(unknownLiteral));
        Assert.Equal("structured_literal_mapping", renderer.GetUnsupportedFeature(nestedUnknownLiteral));
        Assert.Equal("structured_cast_type", renderer.GetUnsupportedFeature(unsafeStoreType));
        Assert.Equal(
            "schema_qualified_expression_collation",
            renderer.GetUnsupportedFeature(qualifiedCollation));
        Assert.Equal("current_value_precision", renderer.GetUnsupportedFeature(preciseCurrentValue));
        Assert.Equal("provider_fragment_mismatch", renderer.GetUnsupportedFeature(foreignFragment));
        Assert.Throws<ArgumentNullException>(() => renderer.GetUnsupportedFeature(null!));
    }

    [Fact]
    public void Render_RejectsUnrenderableProviderBoundaries()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);

        Assert.Throws<ArgumentNullException>(() => renderer.Render(null!));
        Assert.Throws<NotSupportedException>(() => renderer.Render(SafeMigrationSql.Literal(new object())));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.Literal(1, "INTEGER); DROP TABLE items; --")));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.Cast(SafeMigrationSql.Literal(1), "INTEGER); DROP TABLE items; --")));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.Collate(SafeMigrationSql.Identifier("value"), "NOCASE", "main")));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp, precision: 6)));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.ProviderFragment("foreign_provider", "CURRENT_USER")));
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("VARCHAR(32)")]
    [InlineData("DECIMAL(18, 4)")]
    [InlineData("DOUBLE PRECISION")]
    public void Render_AcceptsBoundedSQLiteStoreTypes(
        string storeType
    )
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        var sql = renderer.Render(expression);

        Assert.Equal($"CAST(\"value\" AS {storeType})", sql);
        Assert.Null(renderer.GetUnsupportedFeature(expression));
    }

    [Theory]
    [InlineData("t\u00EBxt")]
    [InlineData("INTEGER; DROP TABLE items")]
    [InlineData("VARCHAR(-1)")]
    [InlineData("VARCHAR(1, 2, 3)")]
    [InlineData("\"TEXT\"")]
    public void Render_RejectsStoreTypesOutsideTheBoundedGrammar(
        string storeType
    )
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        var exception = Assert.Throws<NotSupportedException>(() => renderer.Render(expression));

        Assert.Contains("not safe to render", exception.Message, StringComparison.Ordinal);
        Assert.Equal("structured_cast_type", renderer.GetUnsupportedFeature(expression));
    }

    private static SqliteSafeMigrationSqlExpressionRenderer CreateRenderer(
        DbContext context
    ) => new(
        context.GetService<IRelationalTypeMappingSource>(),
        context.GetService<ISqlGenerationHelper>());
}
