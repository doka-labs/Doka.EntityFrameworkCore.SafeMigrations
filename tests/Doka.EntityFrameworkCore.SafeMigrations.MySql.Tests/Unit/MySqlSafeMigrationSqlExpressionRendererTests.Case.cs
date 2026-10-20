namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationSqlExpressionRendererTests
{
    /// <summary>Generated source retains native numeric spelling in both ordinary and default render paths.</summary>
    /// <param name="sql">The exact provider-authored numeric token.</param>
    [Theory]
    [InlineData("2147483648")]
    [InlineData("1.234567890123456789e30")]
    [InlineData("0.000e0")]
    public void CapturedNumericRenderingPreservesNativeLexeme(string sql)
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression));

        var ordinary = renderer.Render(expression);
        var defaultSql = renderer.RenderDefault(expression);

        Assert.Equal(sql, ordinary);
        Assert.Equal(sql, defaultSql);
    }

    /// <summary>MySQL's expression-first CONVERT must not be reinterpreted as SQL Server's type-first cast.</summary>
    /// <param name="defaultMode">Whether the independent default renderer is exercised.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsedConvertCannotInvertMySqlColumnAndTypeRoles(bool defaultMode)
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        Assert.True(SafeMigrationSqlExpressionParser.TryParse("CONVERT(signed, unsigned)", out var expression));

        // Act
        var exception = Record.Exception(() =>
        {
            _ = defaultMode ? renderer.RenderDefault(expression) : renderer.Render(expression);
        });

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Equal("structured_convert_dialect", renderer.GetUnsupportedFeature(expression));
    }

    /// <summary>CASE rendering retains order, omitted ELSE, and SQL-mode-independent default leaves.</summary>
    [Fact]
    public void CaseRenderingRetainsBranchRolesInOrdinaryAndDefaultModes()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Case(
            [new(SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("value")), SafeMigrationSql.Literal("a\\b"))]);

        var ordinary = renderer.Render(expression);
        var defaultSql = renderer.RenderDefault(expression);

        Assert.StartsWith("CASE WHEN (`value` IS NULL) THEN ", ordinary, StringComparison.Ordinal);
        Assert.EndsWith(" END", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain(" ELSE ", ordinary, StringComparison.Ordinal);
        Assert.Equal("CASE WHEN (`value` IS NULL) THEN convert(0x615c62 using utf8mb4) END", defaultSql);
        Assert.Null(renderer.GetUnsupportedFeature(expression));
    }

    /// <summary>Unsupported nested leaves remain visible in every CASE role.</summary>
    /// <param name="role">The branch role containing a foreign provider fragment.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CaseValidationChecksConditionResultAndElse(
        int role
    )
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var unsupported = SafeMigrationSql.ProviderFragment("foreign", "x");
        var expression = SafeMigrationSql.Case(
            [new(role == 0 ? unsupported : SafeMigrationSql.Literal(true),
                role == 1 ? unsupported : SafeMigrationSql.Literal(1))],
            role == 2 ? unsupported : SafeMigrationSql.Literal(0));

        var feature = renderer.GetUnsupportedFeature(expression);

        Assert.Equal("provider_fragment_mismatch", feature);
    }
}
