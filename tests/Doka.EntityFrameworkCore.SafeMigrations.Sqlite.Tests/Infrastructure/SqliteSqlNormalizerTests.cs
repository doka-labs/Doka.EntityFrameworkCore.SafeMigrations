namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqliteSqlNormalizerTests
{
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
