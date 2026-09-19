namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqliteSqlIdentifierScannerTests
{
    [Theory]
    [InlineData("SELECT * FROM \"order\"\"items\";", "order\"items")]
    [InlineData("SELECT * FROM `order``items`;", "order`items")]
    [InlineData("SELECT * FROM [order items];", "order items")]
    [InlineData("SELECT * FROM main.\"mixedCase\";", "MIXEDCASE")]
    public void ReferencesIdentifier_DecodesEverySQLiteIdentifierForm(
        string sql,
        string identifier
    )
    {
        var referencesIdentifier = SqliteSqlIdentifierScanner.ReferencesIdentifier(sql, identifier);

        Assert.True(referencesIdentifier);
    }

    [Theory]
    [InlineData("SELECT 'order\"items';")]
    [InlineData("SELECT 1 /* order\"items */;")]
    [InlineData("SELECT 1 -- order\"items\n;")]
    public void ReferencesIdentifier_IgnoresLiteralAndCommentText(
        string sql
    )
    {
        var referencesIdentifier = SqliteSqlIdentifierScanner.ReferencesIdentifier(sql, "order\"items");

        Assert.False(referencesIdentifier);
    }

    [Fact]
    public void ReferencesIdentifier_FoldsAsciiWithoutCollapsingNonAsciiCase()
    {
        const string uppercase = "\u00C9";
        const string lowercase = "\u00E9";

        var asciiMatch = SqliteSqlIdentifierScanner.ReferencesIdentifier("SELECT value FROM Items;", "items");
        var unicodeMismatch = SqliteSqlIdentifierScanner.ReferencesIdentifier(
            $"SELECT value FROM \"{uppercase}\";",
            lowercase);

        Assert.True(asciiMatch);
        Assert.False(unicodeMismatch);
        Assert.False(SqliteIdentifierComparer.Instance.Equals(uppercase, lowercase));
        Assert.NotEqual(
            SqliteIdentifierComparer.Normalize(uppercase),
            SqliteIdentifierComparer.Normalize(lowercase));
    }

    [Fact]
    public void ReferencesIdentifier_AcceptsEveryHighByteIdentifierStart()
    {
        const string identifier = "\u20AC";

        var referencesIdentifier = SqliteSqlIdentifierScanner.ReferencesIdentifier(
            $"SELECT Id FROM {identifier};",
            identifier);

        Assert.True(referencesIdentifier);
    }

    [Theory]
    [InlineData("WITHOUT ROWID")]
    [InlineData("WITHOUT /*gap*/ ROWID")]
    [InlineData("WITHOUT --gap\nROWID")]
    public void ContainsKeywordSequence_TreatsCommentsAsWhitespace(
        string sql
    )
    {
        var containsSequence = SqliteSqlIdentifierScanner.ContainsKeywordSequence(
            sql,
            "WITHOUT",
            "ROWID");

        Assert.True(containsSequence);
    }

    [Theory]
    [InlineData("WITHOUT 'ROWID'")]
    [InlineData("'WITHOUT' ROWID")]
    [InlineData("WITHOUT \"gap\" ROWID")]
    [InlineData("WITHOUT, ROWID")]
    [InlineData("WITHOUT /* ROWID")]
    public void ContainsKeywordSequence_IgnoresLiteralsAndIncompleteComments(
        string sql
    )
    {
        var containsSequence = SqliteSqlIdentifierScanner.ContainsKeywordSequence(
            sql,
            "WITHOUT",
            "ROWID");

        Assert.False(containsSequence);
    }

    [Fact]
    public void ReadIdentifierFollowingKeyword_IgnoresStringLiterals()
    {
        var identifier = SqliteSqlIdentifierScanner.ReadIdentifierFollowingKeyword(
            "Value TEXT DEFAULT 'COLLATE RTRIM' COLLATE NOCASE",
            "COLLATE");

        Assert.Equal("NOCASE", identifier);
    }

    [Fact]
    public void ReadLastIdentifierFollowingKeyword_ReturnsTheLatestConstraintName()
    {
        var identifier = SqliteSqlIdentifierScanner.ReadLastIdentifierFollowingKeyword(
            "Status TEXT CONSTRAINT nn NOT NULL CONSTRAINT ck ",
            "CONSTRAINT");

        Assert.Equal("ck", identifier);
    }

    [Fact]
    public void IndexOfCharacterFollowingKeyword_IgnoresLiteralAndCommentText()
    {
        const string sql = "Value TEXT DEFAULT 'AS (phantom)' GENERATED ALWAYS AS /* gap */ (Id + 1)";

        var openParenthesis = SqliteSqlIdentifierScanner.IndexOfCharacterFollowingKeyword(sql, "AS", '(');

        Assert.Equal(sql.LastIndexOf('('), openParenthesis);
    }
}
