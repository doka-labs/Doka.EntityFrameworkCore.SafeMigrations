namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies searched CASE rendering without opening a SQL Server connection.</summary>
public sealed class SqlServerSafeMigrationSqlExpressionRendererTests
{
    private const string ConnectionString = "Server=127.0.0.1;Database=renderer;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Captured integer-to-decimal boundaries retain their native T-SQL token roles.</summary>
    /// <param name="sql">The exact provider-authored numeric token.</param>
    [Theory]
    [InlineData("2147483648")]
    [InlineData("1.234567890123456789e30")]
    [InlineData("0.000e0")]
    public void CapturedNumericRenderingPreservesNativeLexeme(string sql)
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
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(
            "CONVERT(bit, CASE WHEN [right_bound] > [left_bound] THEN 1 ELSE 0 END)", out var expression));

        var sql = renderer.Render(expression!);

        Assert.Equal("CAST(CASE WHEN ([right_bound] > [left_bound]) THEN 1 ELSE 0 END AS bit)", sql);
        Assert.DoesNotContain("[bit]", sql, StringComparison.Ordinal);
        Assert.Null(renderer.GetUnsupportedFeature(expression!));
    }

    /// <summary>Predicates selected by CASE retain UNKNOWN rather than silently mapping it to false.</summary>
    [Fact]
    public void PredicateResultsUseNullableScalarConversion()
    {
        using var context = new SafeMigrationDbContext(ConnectionString);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Case(
            [new(SafeMigrationSql.Identifier("enabled"), SafeMigrationSql.Binary(
                SafeMigrationSql.Identifier("value"), SafeMigrationSqlBinaryOperator.GreaterThan,
                SafeMigrationSql.Literal(0)))]);

        var sql = renderer.Render(expression);

        Assert.Equal("CASE WHEN ([enabled] = CAST(1 AS bit)) THEN CASE WHEN ([value] > 0) "
            + "THEN CAST(1 AS bit) WHEN NOT ([value] > 0) THEN CAST(0 AS bit) ELSE NULL END END", sql);
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
                [new(SafeMigrationSql.Literal(true), SafeMigrationSql.Cast(expression, "int"))]);
        }

        var feature = renderer.GetUnsupportedFeature(expression);

        Assert.Equal(unsupported ? "structured_case_nesting" : null, feature);
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
            [new(role == 0 ? unsupported : SafeMigrationSql.Literal(true),
                role == 1 ? unsupported : SafeMigrationSql.Literal(1))],
            role == 2 ? unsupported : SafeMigrationSql.Literal(0));

        var feature = renderer.GetUnsupportedFeature(expression);

        Assert.Equal("opaque_sql_expression", feature);
        Assert.Throws<NotSupportedException>(() => renderer.Render(expression));
    }

    private static SqlServerSafeMigrationSqlExpressionRenderer CreateRenderer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());
}
