namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects native CASE deparser candidates without a database dependency.</summary>
public sealed class MySqlGeneratedExpressionDisplayTests
{
    /// <summary>The two native engines use different parentheses around the same structured CASE predicate.</summary>
    /// <param name="isMariaDb">Whether the expected display uses MariaDB's minimal grouping.</param>
    /// <param name="expected">The independently observed exact native catalog spelling.</param>
    [Theory]
    [InlineData(true, "`right_bound` > case when `left_bound` = 9223372036854775807 "
        + "then `left_bound` else `left_bound` + 1 end")]
    [InlineData(false, "(`right_bound` > (case when (`left_bound` = 9223372036854775807) "
        + "then `left_bound` else (`left_bound` + 1) end))")]
    public void GuardedCaseMatchesNativeCatalogDisplay(bool isMariaDb, string expected)
    {
        // Arrange
        using var context = CreateContext();
        var renderer = Renderer(context);
        var left = SafeMigrationSql.Identifier("left_bound");
        var expression = Binary(SafeMigrationSql.Identifier("right_bound"),
            SafeMigrationSqlBinaryOperator.GreaterThan, SafeMigrationSql.Case(
            [
                new SafeMigrationSqlCaseWhenClause(Binary(left, SafeMigrationSqlBinaryOperator.Equal,
                    SafeMigrationSql.Literal(long.MaxValue)), left),
            ], Binary(left, SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(1))));

        // Act
        var result = MySqlGeneratedExpressionDisplay.Render(expression, renderer, isMariaDb);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Minimal grouping must not flatten arithmetic, right-associative branches or Boolean negation.</summary>
    /// <param name="operation">The arithmetic or Boolean tree whose grouping is significant.</param>
    /// <param name="expected">The candidate with all required grouping preserved.</param>
    [Theory]
    [InlineData("multiply", "(`a` + 1) * `b`")]
    [InlineData("subtract", "`a` - (`b` - 1)")]
    [InlineData("not-add", "(`a` = 0) + 1")]
    [InlineData("not-compare", "`a` <= `b`")]
    public void MariaDisplayRetainsRequiredOperatorBinding(string operation, string expected)
    {
        // Arrange
        using var context = CreateContext();
        var a = SafeMigrationSql.Identifier("a");
        var b = SafeMigrationSql.Identifier("b");
        var one = SafeMigrationSql.Literal(1);
        var expression = operation switch
        {
            "multiply" => Binary(Binary(a, SafeMigrationSqlBinaryOperator.Add, one),
                SafeMigrationSqlBinaryOperator.Multiply, b),
            "subtract" => Binary(a, SafeMigrationSqlBinaryOperator.Subtract,
                Binary(b, SafeMigrationSqlBinaryOperator.Subtract, one)),
            "not-add" => Binary(SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, a),
                SafeMigrationSqlBinaryOperator.Add, one),
            "not-compare" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not,
                Binary(a, SafeMigrationSqlBinaryOperator.GreaterThan, b)),
            _ => throw new UnreachableException(),
        };

        // Act
        var result = MySqlGeneratedExpressionDisplay.Render(expression, Renderer(context), isMariaDb: true);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Native spelling never changes literal contents or quoted identifier case.</summary>
    [Fact]
    public void CaseDisplayPreservesQuotedTokenContents()
    {
        // Arrange
        using var context = CreateContext();
        var expression = SafeMigrationSql.Case(
        [
            new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("When And")),
                SafeMigrationSql.Literal("CASE and END")),
        ], SafeMigrationSql.Literal("Else Then"));

        // Act
        var result = MySqlGeneratedExpressionDisplay.Render(expression, Renderer(context), isMariaDb: true);

        // Assert
        Assert.Equal("case when `When And` is null then 'CASE and END' else 'Else Then' end", result);
    }

    /// <summary>Builds expression operands without passing source SQL through a normalizer.</summary>
    private static SafeMigrationSqlExpression Binary(SafeMigrationSqlExpression left,
        SafeMigrationSqlBinaryOperator operation, SafeMigrationSqlExpression right) =>
        SafeMigrationSql.Binary(left, operation, right);

    /// <summary>Uses the actual provider's identifier and literal escaping without opening a connection.</summary>
    private static MySqlSafeMigrationSqlExpressionRenderer Renderer(DbContext context) => new(
        context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    /// <summary>Creates an offline provider scope for deterministic native SQL generation.</summary>
    private static DbContext CreateContext() => new(new DbContextOptionsBuilder()
        .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
            MySqlServerVersion.MySql(new Version(8, 4, 11))).UseMySqlSafeMigrations().Options);
}
