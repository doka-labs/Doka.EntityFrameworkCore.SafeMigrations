namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Provides isolated SQLite coverage for SQL identifier scanner.
/// </summary>
public sealed class SqliteSqlIdentifierScannerTests
{
    /// <summary>
    /// Dependency absence distinguishes complete native identifier tokens from literal and prefix text.
    /// </summary>
    /// <param name="sql">The native generated-expression source.</param>
    /// <param name="identifier">The decoded physical column name.</param>
    /// <param name="expected">Whether an identifier token keeps the dependency unproven.</param>
    [Theory]
    [InlineData("source << length('extra')", "extra", false)]
    [InlineData("source /* extra */ << 1", "extra", false)]
    [InlineData("source -- extra\n << 1", "extra", false)]
    [InlineData("extra_source << 1", "extra", false)]
    [InlineData("length('ex''tra') << 1", "ex'tra", false)]
    [InlineData("\"ex\"\"tra\" << 1", "ex\"tra", true)]
    [InlineData("`ex``tra` << 1", "ex`tra", true)]
    [InlineData("[ex tra] << 1", "ex tra", true)]
    [InlineData("\u20AC_source << 1", "source", false)]
    [InlineData("\"\u00C9\" << 1", "\u00E9", false)]
    [InlineData("\"SoUrCe\" << 1", "source", true)]
    [InlineData("\uFEFFsource << 1", "source", true)]
    [InlineData("id + \uFEFFsource << 1", "source", true)]
    [InlineData("source\uFEFFtail << 1", "source\uFEFFtail", true)]
    [InlineData("`\uFEFFsource` << 1", "\uFEFFsource", true)]
    [InlineData("'\uFEFFsource' << 1", "source", false)]
    [InlineData("source + \u00A0operand << 1", "\u00A0operand", true)]
    [InlineData("source + \u2003operand << 1", "\u2003operand", true)]
    public void GeneratedDependencyAbsenceRetainsNativeIdentifierBoundaries(
        string sql,
        string identifier,
        bool expected
    )
    {
        // Arrange
        // Native source is metadata, not an expression executed by this proof.

        // Act
        var referenced = SqliteSqlIdentifierScanner.ReferencesIdentifier(sql, identifier);

        // Assert
        Assert.Equal(expected, referenced);
    }

    /// <summary>Reading one identifier cannot discard the first byte of a following comment or quoted token.</summary>
    /// <param name="sql">The valid native source with adjacent trivia.</param>
    /// <param name="expected">Whether the source identifier occurs outside comment text.</param>
    [Theory]
    [InlineData("other/*'*/ << source", true)]
    [InlineData("other/*\"*/ << source", true)]
    [InlineData("other/*`*/ << source", true)]
    [InlineData("other/*[*/ << source", true)]
    [InlineData("other--'\n << source", true)]
    [InlineData("other--\r'\n << source", true)]
    [InlineData("other/* source */ << 1", false)]
    [InlineData("other-- source\n << 1", false)]
    public void AdjacentCommentAfterIdentifierCannotHideLaterSource(
        string sql,
        bool expected
    )
    {
        // Arrange
        const string source = "source";

        // Act
        var referenced = SqliteSqlIdentifierScanner.ReferencesIdentifier(sql, source);

        // Assert
        Assert.Equal(expected, referenced);
    }

    /// <summary>Adjacent keyword and quoted-identifier tokens retain the complete decoded physical name.</summary>
    [Fact]
    public void AdjacentKeywordCannotHideEscapedQuotedSource()
    {
        // Arrange
        const string sql = "CASE WHEN source > 0 THEN\"so\"\"urce\" ELSE 0 END << 1";

        // Act
        var referenced = SqliteSqlIdentifierScanner.ReferencesIdentifier(sql, "so\"urce");

        // Assert
        Assert.True(referenced);
    }

    /// <summary>
    /// Generic parsing is excluded only when unquoted high-byte whitespace can change native binding.
    /// </summary>
    /// <param name="sql">The native expression with a high-byte character in a known token role.</param>
    /// <param name="expected">Whether generic whitespace handling could erase a physical identifier character.</param>
    [Theory]
    [InlineData("\u00A0operand + 1", true)]
    [InlineData("source + \u2003operand", true)]
    [InlineData("source + '\u00A0operand'", false)]
    [InlineData("source + \"\u00A0operand\"", false)]
    [InlineData("source + [\u2003operand]", false)]
    [InlineData("source + `\u00A0operand`", false)]
    [InlineData("source /* \u00A0operand */ + 1", false)]
    [InlineData("source -- \u2003operand\n + 1", false)]
    [InlineData("source \t\n\f\r + 1", false)]
    public void NativeWhitespaceCompatibilityRetainsQuotedAndCommentRoles(
        string sql,
        bool expected
    )
    {
        // Arrange
        var native = sql;

        // Act
        var incompatible = SqliteSqlIdentifierScanner.HasNonNativeWhitespace(native);

        // Assert
        Assert.Equal(expected, incompatible);
    }

    /// <summary>
    /// References identifier decodes every SQLite identifier form.
    /// </summary>
    /// <param name="sql">The native SQL fixture exercised by this independent case.</param>
    /// <param name="identifier">The identifier defining this isolated fixture case.</param>
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
        // Arrange: The fixed or theory-supplied SQL/value is the complete input.

        // Act
        var referencesIdentifier = SqliteSqlIdentifierScanner.ReferencesIdentifier(sql, identifier);

        // Assert
        Assert.True(referencesIdentifier);
    }

    /// <summary>
    /// References identifier ignores literal and comment text.
    /// </summary>
    /// <param name="sql">The native SQL fixture exercised by this independent case.</param>
    [Theory]
    [InlineData("SELECT 'order\"items';")]
    [InlineData("SELECT 1 /* order\"items */;")]
    [InlineData("SELECT 1 -- order\"items\n;")]
    public void ReferencesIdentifier_IgnoresLiteralAndCommentText(
        string sql
    )
    {
        // Arrange: The fixed or theory-supplied SQL/value is the complete input.

        // Act
        var referencesIdentifier = SqliteSqlIdentifierScanner.ReferencesIdentifier(sql, "order\"items");

        // Assert
        Assert.False(referencesIdentifier);
    }

    /// <summary>
    /// References identifier folds ascii without collapsing non ascii case.
    /// </summary>
    [Fact]
    public void ReferencesIdentifier_FoldsAsciiWithoutCollapsingNonAsciiCase()
    {
        // Arrange
        const string uppercase = "\u00C9";
        const string lowercase = "\u00E9";

        // Act
        var asciiMatch = SqliteSqlIdentifierScanner.ReferencesIdentifier("SELECT value FROM Items;", "items");
        var unicodeMismatch = SqliteSqlIdentifierScanner.ReferencesIdentifier(
            $"SELECT value FROM \"{uppercase}\";",
            lowercase);

        var unicodeEqual = SqliteIdentifierComparer.Instance.Equals(uppercase, lowercase);
        var normalizedUppercase = SqliteIdentifierComparer.Normalize(uppercase);
        var normalizedLowercase = SqliteIdentifierComparer.Normalize(lowercase);

        // Assert
        Assert.True(asciiMatch);
        Assert.False(unicodeMismatch);
        Assert.False(unicodeEqual);
        Assert.NotEqual(normalizedUppercase, normalizedLowercase);
    }

    /// <summary>
    /// References identifier accepts every high byte identifier start.
    /// </summary>
    [Fact]
    public void ReferencesIdentifier_AcceptsEveryHighByteIdentifierStart()
    {
        // Arrange
        const string identifier = "\u20AC";

        // Act
        var referencesIdentifier = SqliteSqlIdentifierScanner.ReferencesIdentifier(
            $"SELECT Id FROM {identifier};",
            identifier);

        // Assert
        Assert.True(referencesIdentifier);
    }

    /// <summary>
    /// Contains keyword sequence treats comments as whitespace.
    /// </summary>
    /// <param name="sql">The native SQL fixture exercised by this independent case.</param>
    [Theory]
    [InlineData("WITHOUT ROWID")]
    [InlineData("WITHOUT /*gap*/ ROWID")]
    [InlineData("WITHOUT --gap\nROWID")]
    public void ContainsKeywordSequence_TreatsCommentsAsWhitespace(
        string sql
    )
    {
        // Arrange: The fixed or theory-supplied SQL/value is the complete input.

        // Act
        var containsSequence = SqliteSqlIdentifierScanner.ContainsKeywordSequence(
            sql,
            "WITHOUT",
            "ROWID");

        // Assert
        Assert.True(containsSequence);
    }

    /// <summary>
    /// Contains keyword sequence ignores literals and incomplete comments.
    /// </summary>
    /// <param name="sql">The native SQL fixture exercised by this independent case.</param>
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
        // Arrange: The fixed or theory-supplied SQL/value is the complete input.

        // Act
        var containsSequence = SqliteSqlIdentifierScanner.ContainsKeywordSequence(
            sql,
            "WITHOUT",
            "ROWID");

        // Assert
        Assert.False(containsSequence);
    }

    /// <summary>
    /// Read identifier following keyword ignores string literals.
    /// </summary>
    [Fact]
    public void ReadIdentifierFollowingKeyword_IgnoresStringLiterals()
    {
        // Arrange: The fixed or theory-supplied SQL/value is the complete input.

        // Act
        var identifier = SqliteSqlIdentifierScanner.ReadIdentifierFollowingKeyword(
            "Value TEXT DEFAULT 'COLLATE RTRIM' COLLATE NOCASE",
            "COLLATE");

        // Assert
        Assert.Equal("NOCASE", identifier);
    }

    /// <summary>
    /// Read last identifier following keyword returns the latest constraint name.
    /// </summary>
    [Fact]
    public void ReadLastIdentifierFollowingKeyword_ReturnsTheLatestConstraintName()
    {
        // Arrange: The fixed or theory-supplied SQL/value is the complete input.

        // Act
        var identifier = SqliteSqlIdentifierScanner.ReadLastIdentifierFollowingKeyword(
            "Status TEXT CONSTRAINT nn NOT NULL CONSTRAINT ck ",
            "CONSTRAINT");

        // Assert
        Assert.Equal("ck", identifier);
    }

    /// <summary>
    /// Index of character following keyword ignores literal and comment text.
    /// </summary>
    [Fact]
    public void IndexOfCharacterFollowingKeyword_IgnoresLiteralAndCommentText()
    {
        // Arrange
        const string sql = "Value TEXT DEFAULT 'AS (phantom)' GENERATED ALWAYS AS /* gap */ (Id + 1)";

        // Act
        var openParenthesis = SqliteSqlIdentifierScanner.IndexOfCharacterFollowingKeyword(sql, "AS", '(');

        // Assert
        Assert.Equal(sql.LastIndexOf('('), openParenthesis);
    }
}
