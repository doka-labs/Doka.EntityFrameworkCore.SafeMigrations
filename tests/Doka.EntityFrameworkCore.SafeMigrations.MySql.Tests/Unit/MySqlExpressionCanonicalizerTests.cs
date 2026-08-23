namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlExpressionCanonicalizerTests
{
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
