namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlExpressionCanonicalizerTests
{
    [Fact]
    public void CatalogDisplayCandidates_PreserveTokenBoundariesAndRenderMySqlMetadata()
    {
        const string identifier = "`Co``de\\'_\u00fc`";

        var candidates = MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(
            $"{identifier} IS NULL OR {identifier} <> ''");

        var renderedIdentifier = "`Co``de" + new string('\\', 3) + "'_\u00c3\u00bc`";

        Assert.Contains(
            $"(({renderedIdentifier} is null) or " + $"({renderedIdentifier} <> _utf8mb4\\'\\'))",
            candidates,
            StringComparer.Ordinal);
    }
}
