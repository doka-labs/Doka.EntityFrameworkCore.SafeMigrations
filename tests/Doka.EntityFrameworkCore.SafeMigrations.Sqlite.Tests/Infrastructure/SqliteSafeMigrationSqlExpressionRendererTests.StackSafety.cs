namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationSqlExpressionRendererTests
{
    /// <summary>Public expression trees retain their SQL beyond the parser's separate grammar limits.</summary>
    /// <param name="shape">The operand role carrying the next nested expression.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    [InlineData("case_condition")]
    [InlineData("case_result")]
    [InlineData("case_else")]
    [InlineData("in_operand")]
    [InlineData("in_value")]
    [InlineData("function")]
    [InlineData("between_operand")]
    [InlineData("between_lower")]
    [InlineData("between_upper")]
    [InlineData("cast")]
    [InlineData("collate")]
    [InlineData("null_test")]
    public void DeepPublicTreesPreserveExactSqlAndSupportedStatus(string shape)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        const int depth = 20_000;
        var expression = CreateDeepExpression(shape, depth, SafeMigrationSql.Literal(1));
        var (prefix, suffix) = DeepExpressionSql(shape);
        var expected = new StringBuilder();

        for (var index = 0; index < depth; index++)
        {
            expected.Append(prefix);
        }

        expected.Append('1');
        for (var index = 0; index < depth; index++)
        {
            expected.Append(suffix);
        }

        var sql = renderer.Render(expression);
        var feature = renderer.GetUnsupportedFeature(expression);

        Assert.Equal(expected.ToString(), sql);
        Assert.Null(feature);
    }

    /// <summary>Deep accepted prefixes do not hide an incompatible leaf or change its failure code.</summary>
    /// <param name="shape">The operand role carrying the incompatible expression.</param>
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("unary")]
    [InlineData("case_condition")]
    [InlineData("case_result")]
    [InlineData("case_else")]
    [InlineData("in_operand")]
    [InlineData("in_value")]
    [InlineData("function")]
    public void DeepPublicTreesRetainUnsupportedLeafClassification(string shape)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = CreateDeepExpression(
            shape,
            20_000,
            SafeMigrationSql.ProviderFragment("another_provider", "foreign_leaf"));

        var feature = renderer.GetUnsupportedFeature(expression);
        var exception = Assert.Throws<NotSupportedException>(() => renderer.Render(expression));

        Assert.Equal("provider_fragment_mismatch", feature);
        Assert.Contains("another_provider", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Validation visits only the prefix needed to identify the first unsupported feature.</summary>
    /// <param name="shape">The ordered branching construct with a later unknown subclass.</param>
    [Theory]
    [InlineData("binary")]
    [InlineData("between")]
    [InlineData("in")]
    [InlineData("function")]
    [InlineData("case_condition")]
    [InlineData("case_result")]
    [InlineData("case_else")]
    [InlineData("cast")]
    [InlineData("collate")]
    public void UnsupportedPrefixShortCircuitsUnknownLaterNodes(string shape)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = CreateUnsupportedPrefix(shape);
        var expected = shape switch
        {
            "cast" => "structured_cast_type",
            "collate" => "schema_qualified_expression_collation",
            _ => "provider_fragment_mismatch",
        };

        var feature = renderer.GetUnsupportedFeature(expression);
        var exception = Record.Exception(() => renderer.Render(expression));

        Assert.Equal(expected, feature);
        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>Unknown public subclasses remain explicit failures rather than silently accepted leaves.</summary>
    [Fact]
    public void UnknownPublicSubclassesStillFailWhenReached()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Literal(1), new UnknownExpression());

        var validation = Record.Exception(() => renderer.GetUnsupportedFeature(expression));
        var rendering = Record.Exception(() => renderer.Render(expression));

        Assert.IsType<UnreachableException>(validation);
        Assert.IsType<UnreachableException>(rendering);
    }

    /// <summary>Shallow SQL fixes separators and literal visitation order independently of deep fixtures.</summary>
    [Fact]
    public void OrderedBranchesAndListsPreserveExactLiteralOrder()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Case(
            [new(
                SafeMigrationSql.In(SafeMigrationSql.Literal("operand"),
                    [SafeMigrationSql.Literal("first"), SafeMigrationSql.Literal("second")]),
                SafeMigrationSql.Function("COALESCE",
                    SafeMigrationSql.Literal("then"), SafeMigrationSql.Literal(null))),
                new(SafeMigrationSql.IsNull(SafeMigrationSql.Literal("condition")),
                    SafeMigrationSql.Literal("result"))],
            SafeMigrationSql.Between(SafeMigrationSql.Literal("else"),
                SafeMigrationSql.Literal("lower"), SafeMigrationSql.Literal("upper")));

        var sql = renderer.Render(expression);

        Assert.Equal(
            "CASE WHEN ('operand' IN ('first', 'second')) THEN COALESCE('then', NULL)"
            + " WHEN ('condition' IS NULL) THEN 'result' ELSE ('else' BETWEEN 'lower' AND 'upper') END",
            sql);
    }

    /// <summary>Builds deep fixtures iteratively so the test itself cannot exhaust the process stack.</summary>
    private static SafeMigrationSqlExpression CreateDeepExpression(
        string shape,
        int depth,
        SafeMigrationSqlExpression leaf
    )
    {
        var expression = leaf;
        var one = SafeMigrationSql.Literal(1);
        var two = SafeMigrationSql.Literal(2);
        var three = SafeMigrationSql.Literal(3);

        for (var index = 0; index < depth; index++)
        {
            expression = shape switch
            {
                "left" => SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.Add, one),
                "right" => SafeMigrationSql.Binary(one, SafeMigrationSqlBinaryOperator.Add, expression),
                "unary" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, expression),
                "case_condition" => SafeMigrationSql.Case([new(expression, two)], three),
                "case_result" => SafeMigrationSql.Case([new(one, expression)], three),
                "case_else" => SafeMigrationSql.Case([new(one, two)], expression),
                "in_operand" => SafeMigrationSql.In(expression, [two, three]),
                "in_value" => SafeMigrationSql.In(one, [expression, three]),
                "function" => SafeMigrationSql.Function("COALESCE", expression, two),
                "between_operand" => SafeMigrationSql.Between(expression, two, three),
                "between_lower" => SafeMigrationSql.Between(one, expression, three),
                "between_upper" => SafeMigrationSql.Between(one, two, expression),
                "cast" => SafeMigrationSql.Cast(expression, "INTEGER"),
                "collate" => SafeMigrationSql.Collate(expression, "NOCASE"),
                "null_test" => SafeMigrationSql.IsNull(expression),
                _ => throw new ArgumentOutOfRangeException(nameof(shape)),
            };
        }

        return expression;
    }

    /// <summary>Defines the independently concatenated SQL shell for each recursive operand role.</summary>
    private static (string Prefix, string Suffix) DeepExpressionSql(string shape) => shape switch
    {
        "left" => ("(", " + 1)"),
        "right" => ("(1 + ", ")"),
        "unary" => ("(NOT ", ")"),
        "case_condition" => ("CASE WHEN ", " THEN 2 ELSE 3 END"),
        "case_result" => ("CASE WHEN 1 THEN ", " ELSE 3 END"),
        "case_else" => ("CASE WHEN 1 THEN 2 ELSE ", " END"),
        "in_operand" => ("(", " IN (2, 3))"),
        "in_value" => ("(1 IN (", ", 3))"),
        "function" => ("COALESCE(", ", 2)"),
        "between_operand" => ("(", " BETWEEN 2 AND 3)"),
        "between_lower" => ("(1 BETWEEN ", " AND 3)"),
        "between_upper" => ("(1 BETWEEN 2 AND ", ")"),
        "cast" => ("CAST(", " AS INTEGER)"),
        "collate" => ("(", " COLLATE \"NOCASE\")"),
        "null_test" => ("(", " IS NULL)"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    /// <summary>Places an unsupported prefix before an unknown node that must not be inspected.</summary>
    private static SafeMigrationSqlExpression CreateUnsupportedPrefix(string shape)
    {
        var unsupported = SafeMigrationSql.ProviderFragment("another_provider", "unsupported");
        var unknown = new UnknownExpression();
        var one = SafeMigrationSql.Literal(1);

        return shape switch
        {
            "binary" => SafeMigrationSql.Binary(unsupported, SafeMigrationSqlBinaryOperator.Add, unknown),
            "between" => SafeMigrationSql.Between(one, unsupported, unknown),
            "in" => SafeMigrationSql.In(one, [unsupported, unknown]),
            "function" => SafeMigrationSql.Function("COALESCE", unsupported, unknown),
            "case_condition" => SafeMigrationSql.Case([new(unsupported, unknown)]),
            "case_result" => SafeMigrationSql.Case([new(one, unsupported)], unknown),
            "case_else" => SafeMigrationSql.Case([new(one, unsupported), new(unknown, one)]),
            "cast" => SafeMigrationSql.Cast(unknown, "INTEGER; unsafe"),
            "collate" => SafeMigrationSql.Collate(unknown, "NOCASE", "main"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    /// <summary>Probes the existing failure behavior for an unrecognized public expression subtype.</summary>
    private sealed class UnknownExpression : SafeMigrationSqlExpression;
}
