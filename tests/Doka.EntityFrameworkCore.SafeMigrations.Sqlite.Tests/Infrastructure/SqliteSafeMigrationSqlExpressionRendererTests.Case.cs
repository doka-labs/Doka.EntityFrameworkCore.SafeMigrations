namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationSqlExpressionRendererTests
{
    /// <summary>
    /// Native integer and real token roles survive generated-source rendering without CLR conversion.
    /// </summary>
    /// <param name="sql">The exact provider-authored numeric token.</param>
    [Theory]
    [InlineData("2147483648")]
    [InlineData("1.234567890123456789e30")]
    [InlineData("0.000e0")]
    public void CapturedNumericRenderingPreservesNativeLexeme(string sql)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression));

        var rendered = renderer.Render(expression);

        Assert.Equal(sql, rendered);
    }

    /// <summary>SQLite must not silently reinterpret a provider-specific CONVERT function as CAST.</summary>
    [Fact]
    public void ParsedConvertRequiresItsSourceDialect()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        Assert.True(SafeMigrationSqlExpressionParser.TryParse("CONVERT(integer, value)", out var expression));

        var feature = renderer.GetUnsupportedFeature(expression!);

        Assert.Equal("structured_convert_dialect", feature);
        Assert.Throws<NotSupportedException>(() => renderer.Render(expression!));
    }

    /// <summary>Rendering retains ordered branches and the implicit SQL NULL when ELSE is omitted.</summary>
    [Fact]
    public void CaseRenderingPreservesOrderedClausesAndOmittedElse()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Case(
            [new(SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("value")), SafeMigrationSql.Literal(0)),
                new(SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("other")), SafeMigrationSql.Literal(1))]);

        var sql = renderer.Render(expression);

        Assert.Equal("CASE WHEN (\"value\" IS NULL) THEN 0 WHEN (\"other\" IS NOT NULL) THEN 1 END", sql);
        Assert.Null(renderer.GetUnsupportedFeature(expression));
    }

    /// <summary>Unsupported nested leaves cannot bypass validation through CASE.</summary>
    /// <param name="role">The condition, result, or ELSE role with an incompatible fragment.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CaseValidationChecksEveryRole(
        int role
    )
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
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
