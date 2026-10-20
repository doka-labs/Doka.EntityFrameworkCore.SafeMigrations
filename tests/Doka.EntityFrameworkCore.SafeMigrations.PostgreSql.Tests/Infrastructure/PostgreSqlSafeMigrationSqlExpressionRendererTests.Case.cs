namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationSqlExpressionRendererTests
{
    /// <summary>Metadata function qualification reuses the bounded native type proof from catalog rendering.</summary>
    /// <param name="sql">The captured argument expression.</param>
    /// <param name="expectedOid">The exact builtin type OID, or null for an unproven result.</param>
    [Theory]
    [InlineData("1", "23")]
    [InlineData("2147483648", "20")]
    [InlineData("1e3", "1700")]
    [InlineData("1.234567890123456789e30", "1700")]
    [InlineData("-2147483648", "23")]
    [InlineData("-9223372036854775808", "20")]
    [InlineData("-(-9223372036854775808)", "1700")]
    [InlineData("LOWER(value)", null)]
    public void NativeResultTypeRetainsSourceNumericDomains(string sql, string? expectedOid)
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression,
            foldUnquotedIdentifiers: true));

        // Act
        var type = PostgreSqlSafeMigrationSqlExpressionRenderer.RenderNativeResultTypeSql(expression, Literal);

        // Assert
        Assert.Equal(expectedOid, type);
    }

    /// <summary>Typed public floating literals do not acquire the NUMERIC token semantics of captured SQL.</summary>
    [Fact]
    public void NativeResultTypeKeepsTypedLiteralSemantics()
    {
        // Arrange
        var expression = SafeMigrationSql.Literal(1e3);

        // Act
        var type = PostgreSqlSafeMigrationSqlExpressionRenderer.RenderNativeResultTypeSql(expression, Literal);

        // Assert
        Assert.Equal("701", type);
    }

    /// <summary>
    /// Source-aware rendering keeps numeric precision and delegates native canonical spelling to PostgreSQL.
    /// </summary>
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
        var native = renderer.RenderCatalogCapturedNativeDeparsedCandidateSql(expression, Literal);

        Assert.Equal(sql, ordinary);
        Assert.Contains(sql, native, StringComparison.Ordinal);
        Assert.Contains("::", native, StringComparison.Ordinal);
    }

    /// <summary>Every PostgreSQL render path rejects SQL Server-only CONVERT source semantics.</summary>
    /// <param name="mode">The independent renderer entry point.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ParsedConvertIsRejectedAcrossPostgreSqlRenderModes(int mode)
    {
        // Arrange
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        Assert.True(SafeMigrationSqlExpressionParser.TryParse("CONVERT(integer, value)", out var expression));
        Func<string?> render = mode switch
        {
            0 => () => renderer.Render(expression),
            1 => () => renderer.RenderCatalogCandidateSql(expression, Literal),
            2 => () => renderer.RenderCatalogDeparsedCandidateSql(expression, Literal),
            3 => () => renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal),
            4 => () => renderer.RenderCatalogCapturedNativeDeparsedCandidateSql(expression, Literal),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        // Act
        var exception = Record.Exception(() => { _ = render(); });

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Equal("structured_convert_dialect", renderer.GetUnsupportedFeature(expression));
    }

    /// <summary>Native widening keeps integer cast nodes rather than folding them like UNKNOWN string input.</summary>
    [Fact]
    public void CaseNativeIntegerCastRetainsItsInputType()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Case(
            [new(SafeMigrationSql.Literal(true), SafeMigrationSql.Cast(SafeMigrationSql.Literal(1), "bigint"))],
            SafeMigrationSql.Cast(SafeMigrationSql.Literal(2), "bigint"));

        var candidate = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, Literal);

        Assert.NotNull(candidate);
        Assert.Contains("THEN (1)::bigint", candidate, StringComparison.Ordinal);
        Assert.Contains("ELSE (2)::bigint", candidate, StringComparison.Ordinal);
        Assert.DoesNotContain("''1''::bigint", candidate, StringComparison.Ordinal);
    }

    /// <summary>Shared renderers cannot leak CASE indentation or type caches across concurrent calls.</summary>
    [Fact]
    public void ParallelCaseCatalogRenderingRetainsInvocationOwnedState()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var flat = SafeMigrationSql.Case(
            [new(SafeMigrationSql.Literal(true), SafeMigrationSql.Literal(1))], SafeMigrationSql.Literal(0));

        var nested = SafeMigrationSql.Case(
            [new(SafeMigrationSql.Literal(false), flat)], SafeMigrationSql.Literal(2));

        var expectedFlat = renderer.RenderCatalogNativeDeparsedCandidateSql(flat, Literal);
        var expectedNested = renderer.RenderCatalogNativeDeparsedCandidateSql(nested, Literal);
        var results = new string?[64];

        Parallel.For(0, results.Length, index => results[index] =
            renderer.RenderCatalogNativeDeparsedCandidateSql(index % 2 == 0 ? flat : nested, Literal));

        Assert.NotNull(expectedFlat);
        Assert.NotNull(expectedNested);
        Assert.NotEqual(expectedFlat, expectedNested);
        for (var index = 0; index < results.Length; index++)
        {
            Assert.Equal(index % 2 == 0 ? expectedFlat : expectedNested, results[index]);
        }
    }

    /// <summary>All catalog render modes retain the searched CASE and descend into every branch.</summary>
    [Fact]
    public void CaseRendersAcrossDdlAndEveryCatalogCandidate()
    {
        using var context = CreateContext();
        var renderer = CreateRenderer(context);
        var expression = SafeMigrationSql.Case(
            [new(SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("value")), SafeMigrationSql.Literal("empty"))],
            SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Identifier("value"), SafeMigrationSql.Literal("x")));

        var sql = renderer.Render(expression);
        var candidate = renderer.RenderCatalogCandidateSql(expression, static text => "'" + text + "'");
        var deparsed = renderer.RenderCatalogDeparsedCandidateSql(expression, static text => "'" + text + "'");
        var native = renderer.RenderCatalogNativeDeparsedCandidateSql(expression, static text => "'" + text + "'");

        Assert.Equal("CASE WHEN (value IS NULL) THEN 'empty' ELSE coalesce(value, 'x') END", sql);
        Assert.Contains("CASE WHEN", candidate, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN", deparsed, StringComparison.Ordinal);
        Assert.NotNull(native);
        Assert.Contains("COALESCE", native, StringComparison.Ordinal);
        Assert.Contains("::text", native, StringComparison.Ordinal);
        Assert.Null(renderer.GetUnsupportedFeature(expression));
    }

    /// <summary>CASE does not hide incompatible provider fragments in any branch position.</summary>
    /// <param name="role">The branch position with an unsupported fragment.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CaseValidationVisitsEveryBranchRole(
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
