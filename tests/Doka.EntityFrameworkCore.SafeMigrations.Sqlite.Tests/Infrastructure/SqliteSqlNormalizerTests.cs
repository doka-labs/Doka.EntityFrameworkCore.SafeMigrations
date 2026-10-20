namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqliteSqlNormalizerTests
{
    /// <summary>Structural comparison changes identifier delimiters without erasing native literal spelling.</summary>
    /// <param name="left">The actual captured source.</param>
    /// <param name="right">The expected captured source.</param>
    /// <param name="equivalent">Whether source-preserving structural comparison can prove equivalence.</param>
    [Theory]
    [InlineData("\"Source\" > 0", "source > 0", true)]
    [InlineData("[Source] > 0", "`source` > 0", true)]
    [InlineData("(((Source > 0)))", "\"Source\" > 0", true)]
    [InlineData("Source = 'Active'", "\"Source\" = 'ACTIVE'", false)]
    [InlineData("Source = 'a  b'", "\"Source\" = 'a b'", false)]
    [InlineData("Source > 1", "\"Source\" > 1.0", false)]
    [InlineData("Source > -9223372036854775808", "\"Source\" > -9223372036854775808.0", false)]
    [InlineData("Source >= 0", "\"Source\" > 0", false)]
    [InlineData("Source == 1", "\"Source\" = 1", false)]
    public void CapturedComparisonPreservesNativeTokenSemantics(string left, string right, bool equivalent)
    {
        // Arrange
        var actual = left;
        var expected = right;

        // Act
        var result = SqliteSqlNormalizer.EquivalentCapturedSource(actual, expected);

        // Assert
        Assert.Equal(equivalent, result);
    }

    /// <summary>Missing expressions compare only with another missing expression.</summary>
    /// <param name="left">The actual optional source.</param>
    /// <param name="right">The expected optional source.</param>
    /// <param name="equivalent">Whether both sources are absent.</param>
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "source", false)]
    [InlineData("source", null, false)]
    public void CapturedComparisonRetainsAbsentExpressionBoundary(string? left, string? right, bool equivalent)
    {
        // Arrange
        var actual = left;
        var expected = right;

        // Act
        var result = SqliteSqlNormalizer.EquivalentCapturedSource(actual, expected);

        // Assert
        Assert.Equal(equivalent, result);
    }

    [Theory]
    [InlineData("((length(\"Code\") > 0))", "length(\"Code\") > 0")]
    [InlineData("(([Code] <> '(archived)'))", "[Code] <> '(archived)'")]
    [InlineData("((`Code` <> 'it''s (valid)'))", "`Code` <> 'it''s (valid)'")]
    public void Equivalent_IgnoresOnlyRedundantOuterParentheses(
        string left,
        string right
    )
    {
        var equivalent = SqliteSqlNormalizer.Equivalent(left, right);

        Assert.True(equivalent);
    }

    [Theory]
    [InlineData("(Code = 'a') OR (Code = 'b')", "Code = 'a' OR Code = 'b'")]
    [InlineData("([Code] = '(a)') AND ([State] = 'active')", "[Code] = '(a)' AND [State] = 'active'")]
    [InlineData("Status = 'Active'", "Status = 'ACTIVE'")]
    [InlineData("Code = 'a b'", "Code = 'a  b'")]
    [InlineData("(Code = 'unterminated)", "Code = 'unterminated")]
    public void Equivalent_PreservesExpressionStructure(
        string left,
        string right
    )
    {
        var equivalent = SqliteSqlNormalizer.Equivalent(left, right);

        Assert.False(equivalent);
    }
}
