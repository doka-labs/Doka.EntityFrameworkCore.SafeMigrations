namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlExpressionCanonicalizerTests
{
    /// <summary>CASE conditions retain Boolean boundaries, including nested and quoted keyword tokens.</summary>
    [Fact]
    public void CatalogDisplayCandidatesDoNotRegroupBooleanConditionsInsideCase()
    {
        // Arrange
        const string expression = "case when `CASE` = 1 and `value` = 'END or THEN' "
            + "then case when `value` is null or `value` = '' then 0 else 1 end else 0 end";

        // Act
        var candidates = MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(expression,
            includeMySqlEncodedDisplay: false);

        // Assert
        Assert.Equal(new[] { expression, "(" + expression + ")" }, candidates);
    }

    /// <summary>Each Boolean term remains exact when reused by multiple display candidates.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogDisplayCandidates_PreserveMultipleTermsAndQuotedBooleanWords(bool includeEncodedDisplay)
    {
        // Arrange
        const string expression = "`a` = 'x or y' AND `b` = 'and' OR `c` IS NULL";
        const string normalized = "`a` = 'x or y' and `b` = 'and' or `c` is null";

        // Act
        var candidates = MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(expression, includeEncodedDisplay);

        // Assert
        Assert.Equal(normalized, candidates[0]);
        Assert.Contains("((`a` = 'x or y') and (`b` = 'and') or (`c` is null))", candidates, StringComparer.Ordinal);
        Assert.Equal(candidates.Count, candidates.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CatalogDisplayCandidates_PreserveIdentifiersStringsAndBooleanBoundaries()
    {
        const string identifier = "`Co``de\\'_\u00fc`";

        var candidates = MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(
            $"(({identifier} IS NULL) OR ({identifier} <> ''))",
            includeMySqlEncodedDisplay: false);

        Assert.Contains($"{identifier} is null or {identifier} <> ''", candidates, StringComparer.Ordinal);
    }

    [Fact]
    public void CatalogDisplayCandidates_RemoveOnlyBalancedOuterParentheses()
    {
        var candidates = MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(
            "((`a` + `b`) * `c`)",
            includeMySqlEncodedDisplay: false);

        Assert.Contains("(`a` + `b`) * `c`", candidates, StringComparer.Ordinal);
        Assert.DoesNotContain("`a` + `b`) * `c", candidates, StringComparer.Ordinal);
    }

    [Fact]
    public void CatalogDisplayCandidates_AddMySqlEncodedMetadataWithoutReplacingCanonicalCandidate()
    {
        const string identifier = "`Co``de\\'_\u00fc`";

        var candidates = MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(
            $"{identifier} IS NULL OR {identifier} <> ''",
            includeMySqlEncodedDisplay: true);

        var renderedIdentifier = "`Co``de" + new string('\\', 3) + "'_\u00c3\u00bc`";

        Assert.Contains($"{identifier} is null or {identifier} <> ''", candidates, StringComparer.Ordinal);
        Assert.Contains(
            $"(({renderedIdentifier} is null) or ({renderedIdentifier} <> _utf8mb4\\'\\'))",
            candidates,
            StringComparer.Ordinal);
    }
}
