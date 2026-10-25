namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises literal-role guards independently of native metadata collation.</summary>
public sealed class MySqlExpressionCatalogComparisonTests
{
    /// <summary>Counts UTF-8 bytes rather than UTF-16 units before and within supplementary literal tokens.</summary>
    [Fact]
    public void SupplementaryLiteralPreservesFollowingByteOffsets()
    {
        // Arrange
        const string candidate = "`id` = '\U0001F642' AND `ID` = 'A''B'";

        // Act
        var predicate = MySqlExpressionCatalogComparison.Build("catalog", [candidate], Literal);

        // Assert
        Assert.Contains("CONVERT(catalog USING utf8mb4)", predicate, StringComparison.Ordinal);
        Assert.Contains("SUBSTRING(CAST(CONVERT(catalog USING utf8mb4) AS BINARY), 8, 6)", predicate,
            StringComparison.Ordinal);
        Assert.Contains("SUBSTRING(CAST(CONVERT(catalog USING utf8mb4) AS BINARY), 26, 6)", predicate,
            StringComparison.Ordinal);
    }

    /// <summary>A quote inside a backtick identifier is not a value token.</summary>
    [Fact]
    public void QuotedIdentifiersUseOnlyStructuralCaseNormalization()
    {
        // Arrange
        const string candidate = "`A'B` + 1";

        // Act
        var predicate = MySqlExpressionCatalogComparison.Build("catalog", [candidate], Literal);

        // Assert
        Assert.Contains("LOWER(CONVERT(catalog USING utf8mb4))", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("SUBSTRING", predicate, StringComparison.Ordinal);
    }

    /// <summary>Escaped and empty tokens remain complete exact-value guards.</summary>
    [Fact]
    public void EscapedAndEmptyLiteralsUseCompleteQuoteBoundaries()
    {
        // Arrange
        const string candidate = "'a\\'b' = ''";

        // Act
        var predicate = MySqlExpressionCatalogComparison.Build("catalog", [candidate], Literal);

        // Assert
        Assert.Contains("AS BINARY), 1, 6)", predicate, StringComparison.Ordinal);
        Assert.Contains("AS BINARY), 10, 2)", predicate, StringComparison.Ordinal);
    }

    /// <summary>No expected display can never accidentally match a catalog expression.</summary>
    [Fact]
    public void EmptyCandidatesAreFailClosed()
    {
        // Arrange
        var candidates = Array.Empty<string>();

        // Act
        var predicate = MySqlExpressionCatalogComparison.Build("catalog", candidates, Literal);

        // Assert
        Assert.Equal("FALSE", predicate);
    }

    /// <summary>Captures value ranges before MySQL escapes its literal delimiters into non-SQL catalog text.</summary>
    [Fact]
    public void EncodedEmbeddedQuoteRetainsExactValueTokenRole()
    {
        // Arrange
        const string expression = "'A''B'";

        // Act
        var candidate = MySqlExpressionCanonicalizer.CreateComparisonCandidate(expression, mySqlEncoded: true);
        var predicate = MySqlExpressionCatalogComparison.Build("catalog", [candidate], Literal);

        // Assert
        var token = Assert.Single(candidate.Literals);
        Assert.Equal(@"_utf8mb4\'A\\\'B\'", candidate.Text);
        Assert.Equal(candidate.Text, token.Text);
        Assert.Equal(1, token.Start);
        Assert.Equal(Encoding.UTF8.GetByteCount(candidate.Text), token.Length);
        Assert.Contains("SUBSTRING", predicate, StringComparison.Ordinal);
    }

    /// <summary>Tests parameter-role placement without interpreting or executing the SQL expression.</summary>
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
