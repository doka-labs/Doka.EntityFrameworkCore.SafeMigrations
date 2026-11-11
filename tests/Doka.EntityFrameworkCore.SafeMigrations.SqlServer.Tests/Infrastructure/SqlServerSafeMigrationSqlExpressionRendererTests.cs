namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies searched CASE rendering without opening a SQL Server connection.</summary>
public sealed class SqlServerSafeMigrationSqlExpressionRendererTests
{
    private const string ConnectionString = "Server=127.0.0.1;Database=renderer;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Every scalar operand preserves the three-valued result of a typed predicate.</summary>
    /// <param name="role">The scalar role that consumes the predicate.</param>
    [Theory]
    [InlineData("cast")]
    [InlineData("function")]
    [InlineData("comparison")]
    [InlineData("arithmetic")]
    [InlineData("null-test")]
    [InlineData("between")]
    [InlineData("in")]
    public void ScalarOperandsConvertPredicatesWithoutLosingUnknown(
        string role
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = CreateRenderer(context);
        var predicate = SafeMigrationSql.Binary(SafeMigrationSql.Identifier("value"),
            SafeMigrationSqlBinaryOperator.GreaterThan,
            SafeMigrationSql.Literal(0));

        var expression = role switch
        {
            "cast" => SafeMigrationSql.Cast(predicate, "int"),
            "function" => SafeMigrationSql.Function("COALESCE", predicate, SafeMigrationSql.Literal(false)),
            "comparison" => SafeMigrationSql.Binary(predicate,
                SafeMigrationSqlBinaryOperator.Equal,
                SafeMigrationSql.Literal(true)),
            "arithmetic" => SafeMigrationSql.Binary(predicate,
                SafeMigrationSqlBinaryOperator.Add,
                SafeMigrationSql.Literal(1)),
            "null-test" => SafeMigrationSql.IsNull(predicate),
            "between" => SafeMigrationSql.Between(predicate,
                SafeMigrationSql.Literal(false),
                SafeMigrationSql.Literal(true)),
            "in" => SafeMigrationSql.In(SafeMigrationSql.Literal(true), [predicate]),
            _ => throw new UnreachableException(),
        };

        // Act
        var sql = renderer.Render(expression);

        // Assert
        Assert.Contains("CASE WHEN ([value] > 0) THEN CAST(1 AS bit) WHEN NOT ([value] > 0) "
            + "THEN CAST(0 AS bit) ELSE NULL END",
            sql,
            StringComparison.Ordinal);

        Assert.Null(renderer.GetUnsupportedFeature(expression));
    }

    /// <summary>A computed scalar root converts a predicate while a CHECK root retains its native condition.</summary>
    [Fact]
    public void ScalarRootAndPredicateRootKeepDistinctNativeRoles()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("value"));

        // Act
        var scalar = renderer.RenderScalar(expression);
        var predicate = renderer.Render(expression);

        // Assert
        Assert.Equal("CASE WHEN ([value] IS NULL) THEN CAST(1 AS bit) WHEN NOT ([value] IS NULL) "
            + "THEN CAST(0 AS bit) ELSE NULL END",
            scalar);

        Assert.Equal("([value] IS NULL)", predicate);
    }

    /// <summary>Synthetic scalar conversions participate in SQL Server's real CASE nesting budget.</summary>
    /// <param name="depth">The explicit CASE nesting beneath the predicate.</param>
    /// <param name="unsupported">Whether its additional scalar conversion exceeds the native limit.</param>
    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    public void ScalarPredicateCaseDepthIncludesItsConversion(
        int depth,
        bool unsupported
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal(1);

        // WHY: Build an exact native CASE depth without recursive test fixture construction.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < depth; index++)
        {
            expression = SafeMigrationSql.Case(
                [new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(true), expression)]);
        }

        var predicate = SafeMigrationSql.IsNull(expression);

        // Act
        var scalarFeature = renderer.GetUnsupportedScalarFeature(predicate);
        var predicateFeature = renderer.GetUnsupportedFeature(predicate);

        // Assert
        Assert.Equal(unsupported
                ? "structured_case_nesting"
                : null,
            scalarFeature);

        Assert.Null(predicateFeature);
    }

    /// <summary>Captured integer-to-decimal boundaries retain their native T-SQL token roles.</summary>
    /// <param name="sql">The exact provider-authored numeric token.</param>
    [Theory]
    [InlineData("2147483648")]
    [InlineData("1.234567890123456789e30")]
    [InlineData("0.000e0")]
    public void CapturedNumericRenderingPreservesNativeLexeme(
        string sql
    )
    {
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = CreateRenderer(context);
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression));

        var rendered = renderer.Render(expression);

        Assert.Equal(sql, rendered);
    }

    /// <summary>Typed CONVERT metadata renders as a cast and never turns its type token into a column.</summary>
    [Fact]
    public void CaseRendersTypedConversionWithPredicateAndScalarRoles()
    {
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = CreateRenderer(context);
        Assert.True(
            SafeMigrationSqlExpressionParser.TryParse(
                "CONVERT(bit, CASE WHEN [right_bound] > [left_bound] THEN 1 ELSE 0 END)",
                out var expression));

        var sql = renderer.Render(expression);

        Assert.Equal("CAST(CASE WHEN ([right_bound] > [left_bound]) THEN 1 ELSE 0 END AS bit)", sql);
        Assert.DoesNotContain("[bit]", sql, StringComparison.Ordinal);
        Assert.Null(renderer.GetUnsupportedFeature(expression));
    }

    /// <summary>Predicates selected by CASE retain UNKNOWN rather than silently mapping it to false.</summary>
    [Fact]
    public void PredicateResultsUseNullableScalarConversion()
    {
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Case(
        [
            new SafeMigrationSqlCaseWhenClause(
                SafeMigrationSql.Identifier("enabled"),
                SafeMigrationSql.Binary(
                    SafeMigrationSql.Identifier("value"),
                    SafeMigrationSqlBinaryOperator.GreaterThan,
                    SafeMigrationSql.Literal(0))),
        ]);

        var sql = renderer.Render(expression);

        Assert.Equal(
            "CASE WHEN ([enabled] = CAST(1 AS bit)) THEN CASE WHEN ([value] > 0) "
            + "THEN CAST(1 AS bit) WHEN NOT ([value] > 0) THEN CAST(0 AS bit) ELSE NULL END END",
            sql);
    }

    /// <summary>CASE depth validation includes casts between conditional nodes.</summary>
    /// <param name="depth">The requested nested CASE count.</param>
    /// <param name="unsupported">Whether the SQL Server limit must reject the expression.</param>
    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void CaseNestingRespectsServerLimit(
        int depth,
        bool unsupported
    )
    {
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Literal(1);
        for (var index = 0; index < depth; index++)
        {
            expression = SafeMigrationSql.Case(
            [
                new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(true),
                    SafeMigrationSql.Cast(expression, "int")),
            ]);
        }

        var feature = renderer.GetUnsupportedFeature(expression);

        Assert.Equal(
            unsupported
                ? "structured_case_nesting"
                : null,
            feature);
    }

    /// <summary>Unsupported fragments in any CASE role fail before SQL generation.</summary>
    /// <param name="role">The branch role that contains an opaque expression.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CaseValidationVisitsEveryRole(
        int role
    )
    {
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = CreateRenderer(context);
        var unsupported = SafeMigrationSql.Opaque("unknown_sql");
        var expression = SafeMigrationSql.Case(
            [
                new SafeMigrationSqlCaseWhenClause(
                    role == 0
                        ? unsupported
                        : SafeMigrationSql.Literal(true),
                    role == 1
                        ? unsupported
                        : SafeMigrationSql.Literal(1)),
            ],
            role == 2
                ? unsupported
                : SafeMigrationSql.Literal(0));

        var feature = renderer.GetUnsupportedFeature(expression);

        Assert.Equal("opaque_sql_expression", feature);
        Assert.Throws<NotSupportedException>(() => renderer.Render(expression));
    }

    /// <summary>BETWEEN validation checks its operand and both bounds, allowing only fully supported trees.</summary>
    /// <param name="unsupportedRole">The unsupported child index, or minus one for an entirely supported tree.</param>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BetweenValidationVisitsEveryRole(
        int unsupportedRole
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = CreateRenderer(context);
        var unsupported = SafeMigrationSql.Opaque("unknown_sql");
        var expression = SafeMigrationSql.Between(
            unsupportedRole == 0
                ? unsupported
                : SafeMigrationSql.Identifier("Value"),
            unsupportedRole == 1
                ? unsupported
                : SafeMigrationSql.Literal(1),
            unsupportedRole == 2
                ? unsupported
                : SafeMigrationSql.Literal(3));

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal(
            unsupportedRole < 0
                ? null
                : "opaque_sql_expression",
            feature);
    }

    private static SqlServerSafeMigrationSqlExpressionRenderer CreateRenderer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
