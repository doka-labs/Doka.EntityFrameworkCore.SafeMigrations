namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationSqlExpressionParserTests
{
    [Fact]
    public void Parse_RelationalLabCheckConstraintProducesStructuredExpression()
    {
        var parsed = SafeMigrationSqlExpressionParser.TryParse(
            "`Amount` >= 0",
            out var expression,
            out var failureCode);

        var binary = Assert.IsType<SafeMigrationSqlBinaryExpression>(expression);
        var identifier = Assert.IsType<SafeMigrationSqlIdentifierExpression>(binary.Left);
        var literal = Assert.IsType<SafeMigrationSqlLiteralExpression>(binary.Right);

        Assert.True(parsed);
        Assert.Empty(failureCode);
        Assert.Equal(["Amount"], identifier.Parts);
        Assert.Equal(SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, binary.Operator);
        Assert.Equal(0, literal.Value);
        Assert.True(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression));
    }

    [Fact]
    public void Parse_PreservesPrecedenceEscapingAndStructuredSqlRoles()
    {
        const string sql = "NOT ([line]]item] BETWEEN -1 AND 10 OR lower(\"name\") IN ('a''b', 'c')) "
            + "AND CAST(`amount` AS decimal(18, 2)) >= 0 COLLATE pg_catalog.\"C\"";

        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        Assert.True(parsed);
        Assert.Empty(failureCode);
        Assert.NotNull(expression);
        Assert.True(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(expression));

        var root = Assert.IsType<SafeMigrationSqlBinaryExpression>(expression);
        var comparison = Assert.IsType<SafeMigrationSqlBinaryExpression>(root.Right);
        var collatedLiteral = Assert.IsType<SafeMigrationSqlCollateExpression>(comparison.Right);

        Assert.Equal(SafeMigrationSqlBinaryOperator.And, root.Operator);
        Assert.Equal(SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, comparison.Operator);
        Assert.Equal("C", collatedLiteral.Name);
        Assert.Equal("pg_catalog", collatedLiteral.Schema);
    }

    [Theory]
    [InlineData("amount IS NULL")]
    [InlineData("amount IS NOT NULL")]
    [InlineData("amount NOT BETWEEN 1 AND 10")]
    [InlineData("amount NOT IN (1, 2, 3)")]
    [InlineData("CURRENT_DATE = CURRENT_DATE")]
    [InlineData("CURRENT_TIME(3) <= CURRENT_TIMESTAMP(6)")]
    [InlineData("amount + 2 * 3 = 7")]
    [InlineData("amount != 0")]
    [InlineData("ratio = 1.25")]
    [InlineData("ratio = 1e3")]
    [InlineData("coalesce() IS NULL")]
    public void Parse_SupportsBoundedProviderNeutralGrammar(
        string sql
    )
    {
        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        Assert.True(parsed);
        Assert.Empty(failureCode);
        Assert.NotNull(expression);
    }

    [Theory]
    [InlineData("", "empty_expression")]
    [InlineData("amount LIKE '%x%'", "trailing_token")]
    [InlineData("amount = @value", "invalid_token")]
    [InlineData("amount >= 0; DROP TABLE users", "invalid_token")]
    [InlineData("amount >= 0 -- accepted", "comments_not_supported")]
    [InlineData("amount >= /* accepted */ 0", "comments_not_supported")]
    [InlineData("amount NOT NULL", "unsupported_not_predicate")]
    [InlineData("amount IN ()", "expected_operand")]
    [InlineData("1e9999", "invalid_number")]
    [InlineData("name = 'line\\nbreak'", "provider_escape_not_supported")]
    [InlineData("`escaped\\identifier` = 1", "provider_escape_not_supported")]
    [InlineData("'unterminated", "unterminated_delimited_token")]
    public void Parse_RejectsAmbiguousOrUnboundedSql(
        string sql,
        string expectedFailureCode
    )
    {
        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal(expectedFailureCode, failureCode);
    }

    [Fact]
    public void Parse_EnforcesResourceLimits()
    {
        var tooLong = new string('a', SafeMigrationSqlExpressionParser.MaximumLength + 1);
        var tooDeep = new string('(', SafeMigrationSqlExpressionParser.MaximumDepth + 1)
            + "amount"
            + new string(')', SafeMigrationSqlExpressionParser.MaximumDepth + 1);

        var tooManyValues = "amount IN ("
            + string.Join(", ", Enumerable.Range(0, SafeMigrationSqlExpressionParser.MaximumListItems + 1))
            + ")";

        var tooManyArguments = "coalesce("
            + string.Join(", ", Enumerable.Range(0, SafeMigrationSqlExpressionParser.MaximumListItems + 1))
            + ")";

        var tooManyTokens = string.Join(
            " + ",
            Enumerable.Repeat("1", SafeMigrationSqlExpressionParser.MaximumTokens + 1));

        AssertParseFailure(tooLong, "expression_too_long");
        AssertParseFailure(tooDeep, "expression_too_deep");
        AssertParseFailure(tooManyValues, "list_too_long");
        AssertParseFailure(tooManyArguments, "list_too_long");
        AssertParseFailure(tooManyTokens, "expression_too_complex");
    }

    [Fact]
    public void ExpectedDefinitionFactory_UsesStructuredExpressionOnlyWhenProven()
    {
        var supported = new AddCheckConstraintOperation
        {
            Name = "ck_orders_amount",
            Table = "orders",
            Sql = "`amount` >= 0",
        };

        var unsupported = new AddCheckConstraintOperation
        {
            Name = "ck_orders_reference",
            Table = "orders",
            Sql = "reference LIKE 'ORD-%'",
        };

        var structured = SafeMigrationExpectedDefinitionFactory.From(supported);
        var opaque = SafeMigrationExpectedDefinitionFactory.From(unsupported);

        Assert.NotNull(structured.Expression);
        Assert.Null(structured.Sql);
        Assert.Null(opaque.Expression);
        Assert.Equal(unsupported.Sql, opaque.Sql);
    }

    [Fact]
    public void ExpectedDefinitionFactory_ParsesBoundedSqlDefaultsAndPreservesOpaqueSql()
    {
        var supported = new AddColumnOperation
        {
            Name = "created_at",
            Table = "orders",
            ClrType = typeof(DateTime),
            ColumnType = "datetime(6)",
            IsNullable = false,
            DefaultValueSql = "CURRENT_TIMESTAMP(6)",
        };

        var unsupported = new AddColumnOperation
        {
            Name = "expires_at",
            Table = "orders",
            ClrType = typeof(DateTime),
            ColumnType = "datetime(6)",
            IsNullable = false,
            DefaultValueSql = "CURRENT_TIMESTAMP(6) + INTERVAL 1 DAY",
        };

        var structured = SafeMigrationExpectedDefinitionFactory.From(supported);
        var opaque = SafeMigrationExpectedDefinitionFactory.From(unsupported);
        var current = Assert.IsType<SafeMigrationSqlCurrentValueExpression>(
            structured.DefaultValue.StructuredExpression);

        Assert.Equal(SafeMigrationDefaultValueKind.Sql, structured.DefaultValue.Kind);
        Assert.Equal(SafeMigrationSqlCurrentValue.Timestamp, current.Value);
        Assert.Equal(6, current.Precision);
        Assert.Null(structured.DefaultValue.SqlExpression);
        Assert.Null(opaque.DefaultValue.StructuredExpression);
        Assert.Equal(unsupported.DefaultValueSql, opaque.DefaultValue.SqlExpression);
    }

    private static void AssertParseFailure(
        string sql,
        string expectedFailureCode
    )
    {
        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal(expectedFailureCode, failureCode);
    }
}
