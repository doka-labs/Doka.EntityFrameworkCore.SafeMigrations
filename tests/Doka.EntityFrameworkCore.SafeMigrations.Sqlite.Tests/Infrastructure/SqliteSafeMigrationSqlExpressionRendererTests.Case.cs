namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Provides isolated SQLite coverage for safe migration SQL expression renderer.
/// </summary>
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
    public void CapturedNumericRenderingPreservesNativeLexeme(
        string sql
    )
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        if (!SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression))
        {
            throw new InvalidOperationException(
                "The captured numeric fixture must parse before rendering is exercised.");
        }

        // Act
        var rendered = renderer.Render(expression);

        // Assert
        Assert.Equal(sql, rendered);
    }

    /// <summary>SQLite must not silently reinterpret a provider-specific CONVERT function as CAST.</summary>
    [Fact]
    public void ParsedConvertRequiresItsSourceDialect()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        if (!SafeMigrationSqlExpressionParser.TryParse("CONVERT(integer, value)", out var expression))
        {
            throw new InvalidOperationException(
                "The CONVERT fixture must parse before dialect validation is exercised.");
        }

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);
        var exception = Record.Exception(() => renderer.Render(expression));

        // Assert
        Assert.Equal("structured_convert_dialect", feature);
        Assert.IsType<NotSupportedException>(exception);
    }

    /// <summary>Rendering retains ordered branches and the implicit SQL NULL when ELSE is omitted.</summary>
    [Fact]
    public void CaseRenderingPreservesOrderedClausesAndOmittedElse()
    {
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Case(
        [
            new SafeMigrationSqlCaseWhenClause(
                SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("value")),
                SafeMigrationSql.Literal(0)),
            new SafeMigrationSqlCaseWhenClause(
                SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("other")),
                SafeMigrationSql.Literal(1)),
        ]);

        // Act
        var sql = renderer.Render(expression);
        var feature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal("CASE WHEN (\"value\" IS NULL) THEN 0 WHEN (\"other\" IS NOT NULL) THEN 1 END", sql);
        Assert.Null(feature);
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
        // Arrange
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var context = new SqliteSafeMigrationTestContext(connection, registerSafeMigrations: false);
        var renderer = CreateRenderer(context);
        var unsupported = SafeMigrationSql.ProviderFragment("foreign", "x");
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

        // Act
        var feature = renderer.GetUnsupportedFeature(expression);

        // Assert
        Assert.Equal("provider_fragment_mismatch", feature);
    }
}
