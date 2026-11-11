namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects native CASE deparser candidates without a database dependency.</summary>
public sealed class MySqlGeneratedExpressionDisplayTests
{
    /// <summary>The two native engines use different parentheses around the same structured CASE predicate.</summary>
    /// <param name="isMariaDb">Whether the expected display uses MariaDB's minimal grouping.</param>
    /// <param name="expected">The independently observed exact native catalog spelling.</param>
    [Theory]
    [InlineData(
        true,
        "`right_bound` > case when `left_bound` = 9223372036854775807 "
        + "then `left_bound` else `left_bound` + 1 end")]
    [InlineData(
        false,
        "(`right_bound` > (case when (`left_bound` = 9223372036854775807) "
        + "then `left_bound` else (`left_bound` + 1) end))")]
    public void GuardedCaseMatchesNativeCatalogDisplay(
        bool isMariaDb,
        string expected
    )
    {
        // Arrange
        using var context = CreateContext();
        var renderer = Renderer(context);
        var left = SafeMigrationSql.Identifier("left_bound");
        var expression = Binary(
            SafeMigrationSql.Identifier("right_bound"),
            SafeMigrationSqlBinaryOperator.GreaterThan,
            SafeMigrationSql.Case(
                [
                    new SafeMigrationSqlCaseWhenClause(
                        Binary(
                            left,
                            SafeMigrationSqlBinaryOperator.Equal,
                            SafeMigrationSql.Literal(long.MaxValue)),
                        left),
                ],
                Binary(left, SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Literal(1))));

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
    public void MariaDisplayRetainsRequiredOperatorBinding(
        string operation,
        string expected
    )
    {
        // Arrange
        using var context = CreateContext();
        var a = SafeMigrationSql.Identifier("a");
        var b = SafeMigrationSql.Identifier("b");
        var one = SafeMigrationSql.Literal(1);
        var expression = operation switch
        {
            "multiply" => Binary(
                Binary(a, SafeMigrationSqlBinaryOperator.Add, one),
                SafeMigrationSqlBinaryOperator.Multiply,
                b),
            "subtract" => Binary(
                a,
                SafeMigrationSqlBinaryOperator.Subtract,
                Binary(b, SafeMigrationSqlBinaryOperator.Subtract, one)),
            "not-add" => Binary(
                SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, a),
                SafeMigrationSqlBinaryOperator.Add,
                one),
            "not-compare" => SafeMigrationSql.Unary(
                SafeMigrationSqlUnaryOperator.Not,
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
                new SafeMigrationSqlCaseWhenClause(
                    SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("When And")),
                    SafeMigrationSql.Literal("CASE and END")),
            ],
            SafeMigrationSql.Literal("Else Then"));

        // Act
        var result = MySqlGeneratedExpressionDisplay.Render(expression, Renderer(context), isMariaDb: true);

        // Assert
        Assert.Equal("case when `When And` is null then 'CASE and END' else 'Else Then' end", result);
    }

    /// <summary>Functional-key display safely encodes typed values before native catalog serialization.</summary>
    /// <param name="value">The exact structured string or character value.</param>
    /// <param name="literal">The independently expected native-display value token.</param>
    [Theory]
    [InlineData("tail\\", "'tail\\\\'")]
    [InlineData("before\\'after", "'before\\\\''after'")]
    [InlineData("line\nbreak", "'line\\nbreak'")]
    [InlineData("line\rbreak", "'line\\rbreak'")]
    [InlineData("nul\0byte", "'nul\\0byte'")]
    [InlineData("tab\tvalue", "'tab\tvalue'")]
    [InlineData("back\bspace", "'back\bspace'")]
    [InlineData("ctrl\u001avalue", "'ctrl\\Zvalue'")]
    [InlineData("ctrl\u0001value", "'ctrl\u0001value'")]
    [InlineData("A'B", "'A''B'")]
    [InlineData('\\', "'\\\\'")]
    [InlineData('\'', "''''")]
    public void FunctionalKeyDisplayRetainsOrdinaryProviderLiteralEncoding(
        object value,
        string literal
    )
    {
        // Arrange
        using var context = CreateContext();
        var expression = SafeMigrationSql.Function("CONCAT",
            SafeMigrationSql.Identifier("value"),
            SafeMigrationSql.Literal(value));

        // Act
        var display = MySqlGeneratedExpressionDisplay.Render(expression,
            Renderer(context),
            isMariaDb: false,
            nativeIndexStringLiterals: true);

        var candidates = MySqlExpressionCanonicalizer.BuildComparisonCandidates(display,
            includeMySqlEncodedDisplay: true);

        // Assert
        Assert.Equal($"concat(`value`,{literal})", display);
        Assert.NotEmpty(candidates);
        Assert.DoesNotContain("convert(", display, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ordinary index encoding must not replace the captured or stable CHECK spelling contracts.</summary>
    [Fact]
    public void FunctionalKeyEncodingDoesNotChangeCheckDisplayModes()
    {
        // Arrange
        using var context = CreateContext();
        var expression = SafeMigrationSql.Function("CONCAT",
            SafeMigrationSql.Identifier("value"),
            SafeMigrationSql.Literal("A'B"));

        // Act
        var captured = MySqlGeneratedExpressionDisplay.Render(expression,
            Renderer(context),
            isMariaDb: false,
            preserveCapturedStringLiterals: true);

        var stable = MySqlGeneratedExpressionDisplay.Render(expression, Renderer(context), isMariaDb: false);

        // Assert
        Assert.Equal("concat(`value`,'A''B')", captured);
        Assert.Equal("concat(`value`,convert(0x412742 using utf8mb4))", stable);
    }

    /// <summary>Catalog encoding preserves independently captured MySQL value bytes, including escape layers.</summary>
    /// <param name="value">The exact structured value before native serialization.</param>
    /// <param name="nativeHex">The independently observed HEX(EXPRESSION) from MySQL 8.4.13.</param>
    [Theory]
    [InlineData("tail\\", "636F6E636174286076616C7565602C5F757466386D62345C277461696C5C5C5C5C5C2729")]
    [InlineData("before\\'after",
        "636F6E636174286076616C7565602C5F757466386D62345C27"
        + "6265666F72655C5C5C5C5C5C5C2761667465725C2729")]
    [InlineData("line\nbreak", "636F6E636174286076616C7565602C5F757466386D62345C276C696E655C5C6E627265616B5C2729")]
    [InlineData("nul\0byte", "636F6E636174286076616C7565602C5F757466386D62345C276E756C5C5C30627974655C2729")]
    [InlineData("tab\tvalue", "636F6E636174286076616C7565602C5F757466386D62345C277461620976616C75655C2729")]
    public void FunctionalKeyCatalogCandidatesRetainNativeValueBytes(
        string value,
        string nativeHex
    )
    {
        // Arrange
        using var context = CreateContext();
        var expression = SafeMigrationSql.Function("CONCAT",
            SafeMigrationSql.Identifier("value"),
            SafeMigrationSql.Literal(value));

        // Act
        var display = MySqlGeneratedExpressionDisplay.Render(expression,
            Renderer(context),
            isMariaDb: false,
            nativeIndexStringLiterals: true);

        var candidates = MySqlExpressionCanonicalizer.BuildComparisonCandidates(display,
            includeMySqlEncodedDisplay: true);

        // Assert
        Assert.Contains(candidates,
            candidate => Convert.ToHexString(Encoding.UTF8.GetBytes(candidate.Text)) == nativeHex);
    }

    /// <summary>Builds expression operands without passing source SQL through a normalizer.</summary>
    private static SafeMigrationSqlExpression Binary(
        SafeMigrationSqlExpression left,
        SafeMigrationSqlBinaryOperator operation,
        SafeMigrationSqlExpression right
    ) => SafeMigrationSql.Binary(left, operation, right);

    /// <summary>Uses the actual provider's identifier and literal escaping without opening a connection.</summary>
    private static MySqlSafeMigrationSqlExpressionRenderer Renderer(
        DbContext context
    ) => new(
        context.GetService<IRelationalTypeMappingSource>(),
        context.GetService<ISqlGenerationHelper>());

    /// <summary>Creates an offline provider scope for deterministic native SQL generation.</summary>
    private static DbContext CreateContext() => new(
        new DbContextOptionsBuilder()
            .UseMySql(
                "Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test",
                MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations()
            .Options);
}
