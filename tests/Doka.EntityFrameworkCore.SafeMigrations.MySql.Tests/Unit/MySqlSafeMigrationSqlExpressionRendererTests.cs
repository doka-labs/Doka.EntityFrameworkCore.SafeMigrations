namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlSafeMigrationSqlExpressionRendererTests
{
    [Fact]
    public void Render_ProducesDeterministicSqlForEveryStructuredNode()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var value = SafeMigrationSql.Identifier("app", "Value");

        var expectations = new (SafeMigrationSqlExpression Expression, string Sql)[]
        {
            (value, "`app`.`Value`"), (SafeMigrationSql.Literal(null), "NULL"),
            (SafeMigrationSql.Literal(42), "42"), (SafeMigrationSql.Literal(42, "signed"), "CAST(42 AS signed)"),
            (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, value), "(NOT `app`.`Value`)"),
            (SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, value), "(-`app`.`Value`)"),
            (SafeMigrationSql.IsNull(value), "(`app`.`Value` IS NULL)"),
            (SafeMigrationSql.IsNotNull(value), "(`app`.`Value` IS NOT NULL)"),
            (SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10)),
                "(`app`.`Value` BETWEEN 1 AND 10)"),
            (SafeMigrationSql.Between(value, SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(10), negated: true),
                "(`app`.`Value` NOT BETWEEN 1 AND 10)"),
            (SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1), SafeMigrationSql.Literal(2)]),
                "(`app`.`Value` IN (1, 2))"),
            (SafeMigrationSql.In(value, [SafeMigrationSql.Literal(1)], negated: true),
                "(`app`.`Value` NOT IN (1))"),
            (SafeMigrationSql.Function("LOWER", value), "lower(`app`.`Value`)"),
            (SafeMigrationSql.Cast(value, "char"), "CAST(`app`.`Value` AS char)"),
            (SafeMigrationSql.Collate(value, "utf8mb4_bin"), "(`app`.`Value` COLLATE `utf8mb4_bin`)"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Date), "CURRENT_DATE"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Time, precision: 3), "CURRENT_TIME(3)"),
            (SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp), "CURRENT_TIMESTAMP"),
            (SafeMigrationSql.ProviderFragment("doka_mysql", "CURRENT_USER()"), "CURRENT_USER()"),
            (SafeMigrationSql.Opaque("value + 1"), "value + 1"),
        };

        foreach (var expectation in expectations)
        {
            Assert.Equal(expectation.Sql, renderer.Render(expectation.Expression));
        }
    }

    [Fact]
    public void Render_ProducesEveryBinaryOperator()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var operators = new[]
        {
            SafeMigrationSqlBinaryOperator.And, SafeMigrationSqlBinaryOperator.Or,
            SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSqlBinaryOperator.NotEqual,
            SafeMigrationSqlBinaryOperator.LessThan, SafeMigrationSqlBinaryOperator.LessThanOrEqual,
            SafeMigrationSqlBinaryOperator.GreaterThan, SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
            SafeMigrationSqlBinaryOperator.Add, SafeMigrationSqlBinaryOperator.Subtract,
            SafeMigrationSqlBinaryOperator.Multiply, SafeMigrationSqlBinaryOperator.Divide,
            SafeMigrationSqlBinaryOperator.Modulo,
        };

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
    public void Render_RejectsForeignFragmentsSchemaQualifiedCollationsAndUnknownLiterals()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);

        Assert.Throws<ArgumentNullException>(() => renderer.Render(null!));
        Assert.Throws<NotSupportedException>(() => renderer.Render(SafeMigrationSql.Literal(new object())));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.ProviderFragment("other", "CURRENT_USER()")));
        Assert.Throws<NotSupportedException>(() => renderer.Render(
            SafeMigrationSql.Collate(SafeMigrationSql.Identifier("value"), "utf8mb4_bin", "schema")));
    }

    private static SafeMigrationDbContext CreateContext() => new(
        "Server=localhost;Database=renderer;User ID=test;Password=test;AllowUserVariables=true",
        MySqlServerVersion.MySql(new Version(8, 4, 11)));

    private static MySqlSafeMigrationSqlExpressionRenderer CreateRenderer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
