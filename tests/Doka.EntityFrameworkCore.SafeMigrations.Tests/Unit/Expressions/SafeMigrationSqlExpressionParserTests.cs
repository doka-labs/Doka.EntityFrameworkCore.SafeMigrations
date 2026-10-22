namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationSqlExpressionParserTests
{
    /// <summary>Parses the relational CHECK predicate without losing identifier or literal structure.</summary>
    [Fact]
    public void Parse_RelationalLabCheckConstraintProducesStructuredExpression()
    {
        // Arrange
        const string sql = "`Amount` >= 0";

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParse(
            sql,
            out var expression,
            out var failureCode);

        // Assert
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

    /// <summary>Preserves SQL precedence, quoted identifiers and structured collation roles.</summary>
    [Fact]
    public void Parse_PreservesPrecedenceEscapingAndStructuredSqlRoles()
    {
        // Arrange
        const string sql = "NOT ([line]]item] BETWEEN -1 AND 10 OR lower(\"name\") IN ('a''b', 'c')) "
            + "AND CAST(`amount` AS decimal(18, 2)) >= 0 COLLATE pg_catalog.\"C\"";

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        // Assert
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

    /// <summary>Accepts each supported provider-neutral grammar form.</summary>
    /// <param name="sql">The supported SQL expression.</param>
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
        // Arrange: The theory supplies the SQL expression.

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        // Assert
        Assert.True(parsed);
        Assert.Empty(failureCode);
        Assert.NotNull(expression);
    }

    /// <summary>Rejects unsupported syntax with its exact stable failure code.</summary>
    /// <param name="sql">The rejected SQL expression.</param>
    /// <param name="expectedFailureCode">The exact parser failure code.</param>
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
        // Arrange: The theory supplies the SQL expression.

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        // Assert
        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal(expectedFailureCode, failureCode);
    }

    /// <summary>Rejects each independent resource limit with its stable parser failure code.</summary>
    /// <param name="limit">The serializable resource-limit case identifier.</param>
    /// <param name="expectedFailureCode">The exact parser failure code.</param>
    [Theory]
    [InlineData("length", "expression_too_long")]
    [InlineData("depth", "expression_too_deep")]
    [InlineData("list", "list_too_long")]
    [InlineData("arguments", "list_too_long")]
    [InlineData("tokens", "expression_too_complex")]
    public void Parse_EnforcesResourceLimit(
        string limit,
        string expectedFailureCode
    )
    {
        // Arrange
        var sql = CreateResourceLimitExpression(limit);

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParse(sql, out var expression, out var failureCode);

        // Assert
        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal(expectedFailureCode, failureCode);
    }

    /// <summary>Retains a proven CHECK predicate as a structured expression.</summary>
    [Fact]
    public void ExpectedDefinitionFactory_UsesStructuredComparableCheckExpression()
    {
        // Arrange
        var operation = new AddCheckConstraintOperation
        {
            Name = "ck_orders_amount",
            Table = "orders",
            Sql = "`amount` >= 0",
        };

        // Act
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);

        // Assert
        Assert.NotNull(definition.Expression);
        Assert.Null(definition.Sql);
    }

    /// <summary>Preserves unsupported CHECK syntax as opaque SQL rather than claiming a structural proof.</summary>
    [Fact]
    public void ExpectedDefinitionFactory_PreservesOpaqueUnsupportedCheckExpression()
    {
        // Arrange
        var operation = new AddCheckConstraintOperation
        {
            Name = "ck_orders_reference",
            Table = "orders",
            Sql = "reference LIKE 'ORD-%'",
        };

        // Act
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);

        // Assert
        Assert.Null(definition.Expression);
        Assert.Equal(operation.Sql, definition.Sql);
    }

    /// <summary>Captures a bounded current-value default including its declared precision.</summary>
    [Fact]
    public void ExpectedDefinitionFactory_ParsesBoundedSqlDefault()
    {
        // Arrange
        var operation = new AddColumnOperation
        {
            Name = "created_at",
            Table = "orders",
            ClrType = typeof(DateTime),
            ColumnType = "datetime(6)",
            IsNullable = false,
            DefaultValueSql = "CURRENT_TIMESTAMP(6)",
        };

        // Act
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);

        // Assert
        var current = Assert.IsType<SafeMigrationSqlCurrentValueExpression>(
            definition.DefaultValue.StructuredExpression);

        Assert.Equal(SafeMigrationDefaultValueKind.Sql, definition.DefaultValue.Kind);
        Assert.Equal(SafeMigrationSqlCurrentValue.Timestamp, current.Value);
        Assert.Equal(6, current.Precision);
        Assert.Null(definition.DefaultValue.SqlExpression);
    }

    /// <summary>Preserves unsupported provider-specific default syntax without a false structural proof.</summary>
    [Fact]
    public void ExpectedDefinitionFactory_PreservesOpaqueSqlDefault()
    {
        // Arrange
        var operation = new AddColumnOperation
        {
            Name = "expires_at",
            Table = "orders",
            ClrType = typeof(DateTime),
            ColumnType = "datetime(6)",
            IsNullable = false,
            DefaultValueSql = "CURRENT_TIMESTAMP(6) + INTERVAL 1 DAY",
        };

        // Act
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);

        // Assert
        Assert.Null(definition.DefaultValue.StructuredExpression);
        Assert.Equal(operation.DefaultValueSql, definition.DefaultValue.SqlExpression);
    }

    /// <summary>Builds the original boundary input without putting nonserializable values in discovery data.</summary>
    private static string CreateResourceLimitExpression(
        string limit
    ) => limit switch
    {
        "length" => new string('a', SafeMigrationSqlExpressionParser.MaximumLength + 1),
        "depth" => new string('(', SafeMigrationSqlExpressionParser.MaximumDepth + 1)
            + "amount"
            + new string(')', SafeMigrationSqlExpressionParser.MaximumDepth + 1),
        "list" => "amount IN ("
            + string.Join(", ", Enumerable.Range(0, SafeMigrationSqlExpressionParser.MaximumListItems + 1))
            + ")",
        "arguments" => "coalesce("
            + string.Join(", ", Enumerable.Range(0, SafeMigrationSqlExpressionParser.MaximumListItems + 1))
            + ")",
        "tokens" => string.Join(" + ", Enumerable.Repeat("1", SafeMigrationSqlExpressionParser.MaximumTokens + 1)),
        _ => throw new ArgumentOutOfRangeException(nameof(limit), limit, "Unknown parser resource limit."),
    };
}
