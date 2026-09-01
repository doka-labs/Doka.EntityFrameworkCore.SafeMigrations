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
            (SafeMigrationSql.Literal(null, "int"), "CAST(NULL AS SIGNED)"),
            (SafeMigrationSql.Literal(42), "42"), (SafeMigrationSql.Literal(42, "int"), "CAST(42 AS SIGNED)"),
            (SafeMigrationSql.Literal(42, "signed"), "CAST(42 AS SIGNED)"),
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
            (SafeMigrationSql.Cast(value, "char"), "CAST(`app`.`Value` AS CHAR)"),
            (SafeMigrationSql.Cast(value, "int"), "CAST(`app`.`Value` AS SIGNED)"),
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

    [Theory]
    [InlineData("tinyint(1)", "SIGNED")]
    [InlineData("integer", "SIGNED")]
    [InlineData("bigint unsigned", "UNSIGNED")]
    [InlineData("int(11) zerofill", "UNSIGNED")]
    [InlineData("decimal(18, 4)", "DECIMAL(18,4)")]
    [InlineData("decimal(18)", "DECIMAL(18)")]
    [InlineData("numeric", "DECIMAL")]
    [InlineData("float", "FLOAT")]
    [InlineData("double precision", "DOUBLE")]
    [InlineData("char", "CHAR")]
    [InlineData("varchar(320)", "CHAR(320)")]
    [InlineData("longtext", "CHAR")]
    [InlineData("binary", "BINARY")]
    [InlineData("binary(16)", "BINARY(16)")]
    [InlineData("date", "DATE")]
    [InlineData("datetime", "DATETIME")]
    [InlineData("datetime(6)", "DATETIME(6)")]
    [InlineData("time", "TIME")]
    [InlineData("time(3)", "TIME(3)")]
    public void Render_NormalizesColumnStoreTypesToCommonCastTargets(
        string storeType,
        string expectedCastType
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        var sql = renderer.Render(expression);

        Assert.Equal($"CAST(`value` AS {expectedCastType})", sql);
        Assert.Null(renderer.GetUnsupportedFeature(expression));
    }

    [Theory]
    [InlineData("t\u00EBxt")]
    [InlineData("timestamp(6)")]
    [InlineData("json")]
    [InlineData("varchar(20) CHARACTER SET utf8mb4")]
    [InlineData("varchar(0)")]
    [InlineData("varbinary(16)")]
    [InlineData("binary(0)")]
    [InlineData("binary(999999999999999999999999999999)")]
    [InlineData("decimal(0)")]
    [InlineData("decimal(66, 0)")]
    [InlineData("decimal(10, 31)")]
    [InlineData("decimal(5, 6)")]
    [InlineData("datetime(7)")]
    [InlineData("time(7)")]
    [InlineData("int); DROP TABLE items; --")]
    public void Render_RejectsStoreTypesOutsideTheCommonCastGrammar(
        string storeType
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        var exception = Assert.Throws<NotSupportedException>(() => renderer.Render(expression));

        Assert.Contains("common MySQL and MariaDB CAST target", exception.Message, StringComparison.Ordinal);
        Assert.Equal("structured_cast_type", renderer.GetUnsupportedFeature(expression));
    }

    [Fact]
    public void Render_RejectsOversizedStoreTypeBeforePatternEvaluation()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var storeType = new string('a', 129);
        var expression = SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), storeType);

        var exception = Assert.Throws<NotSupportedException>(() => renderer.Render(expression));

        Assert.Contains("common MySQL and MariaDB CAST target", exception.Message, StringComparison.Ordinal);
        Assert.Equal("structured_cast_type", renderer.GetUnsupportedFeature(expression));
    }

    [Fact]
    public void TryRender_RejectsInvalidDirectInput()
    {
        var whitespaceAccepted = MySqlSafeMigrationCastTypeRenderer.TryRender(" ", out var whitespaceResult);

        Assert.False(whitespaceAccepted);
        Assert.Empty(whitespaceResult);
        Assert.Throws<ArgumentNullException>(
            () => MySqlSafeMigrationCastTypeRenderer.TryRender(null!, out _));
    }

    [Fact]
    public void GetUnsupportedFeature_ClassifiesProviderSpecificExpressionFailures()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);

        var unknownLiteral = SafeMigrationSql.Literal(new object());
        var qualifiedCollation = SafeMigrationSql.Collate(
            SafeMigrationSql.Identifier("value"),
            "utf8mb4_bin",
            "schema");

        var matchingProviderFragment = SafeMigrationSql.ProviderFragment("doka_mysql", "CURRENT_USER()");
        var foreignProviderFragment = SafeMigrationSql.ProviderFragment("foreign_provider", "CURRENT_USER()");

        Assert.Equal("structured_literal_mapping", renderer.GetUnsupportedFeature(unknownLiteral));
        Assert.Equal(
            "schema_qualified_expression_collation",
            renderer.GetUnsupportedFeature(qualifiedCollation));
        Assert.Null(renderer.GetUnsupportedFeature(matchingProviderFragment));
        Assert.Equal("provider_fragment_mismatch", renderer.GetUnsupportedFeature(foreignProviderFragment));
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
            SafeMigrationSql.Cast(SafeMigrationSql.Literal(1), "int); DROP TABLE items; --")));
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
