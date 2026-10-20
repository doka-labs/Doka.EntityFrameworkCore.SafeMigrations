namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Verifies captured numeric and identifier roles without changing historical typed parser contracts.
/// </summary>
public sealed class SafeMigrationSqlCapturedTokenTests
{
    /// <summary>
    /// Captured source cannot lose an overloadable unary-plus role while ordinary parsing stays unchanged.
    /// </summary>
    /// <param name="sql">The bounded source containing an actual unary-plus token.</param>
    [Theory]
    [InlineData("+value")]
    [InlineData("+(value + 1)")]
    [InlineData("CASE WHEN value > 0 THEN +value ELSE 0 END")]
    [InlineData("value + +other")]
    [InlineData("CAST(+value AS bigint)")]
    public void CapturedUnaryPlusRetainsOpaqueBoundary(string sql)
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(sql, out _));

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression,
            out var failureCode);

        // Assert
        Assert.False(parsed);
        Assert.Null(expression);
        Assert.Equal("captured_unary_plus_not_supported", failureCode);
    }

    /// <summary>
    /// Binary addition and plus text inside quoted roles retain their existing captured-source support.
    /// </summary>
    /// <param name="sql">The valid source without a unary-plus role.</param>
    [Theory]
    [InlineData("value + other")]
    [InlineData("1e+3 > 0")]
    [InlineData("[+value] + 1")]
    [InlineData("\"+value\" + 1")]
    [InlineData("`+value` + 1")]
    [InlineData("'+' = '+'")]
    [InlineData("'value + +other' <> ''")]
    public void CapturedNonUnaryPlusRolesRemainSupported(string sql)
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(sql, out _));

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression);

        // Assert
        Assert.True(parsed);
        Assert.NotNull(expression);
    }

    /// <summary>
    /// Double quotes do not imply PostgreSQL folding when a different native column policy is supplied.
    /// </summary>
    /// <param name="caseInsensitive">Whether native metadata certifies case-insensitive column identities.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuppliedNativeColumnIdentityQualifiesDoubleQuoteCaseVariation(bool caseInsensitive)
    {
        // Arrange
        var comparer = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        // Act
        var equal = SafeMigrationSqlExpressionParser.SourcesDifferOnlyByNativeColumnQuotes(
            "SCORE + 'KeepCase'", "\"score\" + 'KeepCase'", '"', columnComparer: comparer);

        // Assert
        Assert.Equal(caseInsensitive, equal);
    }

    /// <summary>
    /// Only provider-certified unquoted ASCII folding can extend a terminal-column quote certificate.
    /// </summary>
    /// <param name="left">The captured source.</param>
    /// <param name="right">The candidate source with independent native binding qualification.</param>
    /// <param name="equivalent">Whether native column identity and every other source byte agree.</param>
    [Theory]
    [InlineData("VALUE + 1", "value + 1", true)]
    [InlineData("VALUE + 1", "\"value\" + 1", true)]
    [InlineData("VALUE + 1", "\"VALUE\" + 1", false)]
    [InlineData("VALUE + '\u00c6ABC'", "\"value\" + '\u00e6ABC'", false)]
    [InlineData("\"\u00c6\" + VALUE", "\"\u00c6\" + \"value\"", true)]
    [InlineData("\"\u00c6\" + VALUE", "\"\u00e6\" + \"value\"", false)]
    [InlineData("ABS(VALUE)", "abs(\"value\")", false)]
    [InlineData("CAST(VALUE AS bigint)", "CAST(\"value\" AS int8)", false)]
    public void FoldedSourceQuoteEquivalencePreservesAllOtherNativeRoles(
        string left,
        string right,
        bool equivalent
    )
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(left, out _));
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(right, out _));

        // Act
        var actual = SafeMigrationSqlExpressionParser.SourcesDifferOnlyByNativeColumnQuotes(
            left, right, '"', foldUnquotedIdentifiers: true);

        // Assert
        Assert.Equal(equivalent, actual);
    }

    /// <summary>Native root keyword checks neither miss a bare qualifier nor reject a legal post-dot label.</summary>
    /// <param name="sql">The structurally captured source.</param>
    /// <param name="expected">The one unquoted root, or null when every root is quoted.</param>
    [Theory]
    [InlineData("select + 1", "select")]
    [InlineData("owner.select + owner.other", "owner")]
    [InlineData("select.owner + 1", "select")]
    [InlineData("\"select\".owner + 1", null)]
    [InlineData("LOWER(owner.select) + CAST(owner.other AS bigint) + 'select'", "owner")]
    public void RootColumnCollectionRetainsNativeKeywordPositions(string sql, string? expected)
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out _));

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryCollectUnquotedColumnRootIdentifiers(sql,
            out var identifiers, foldUnquotedIdentifiers: true);

        // Assert
        Assert.True(parsed);
        Assert.Equal(expected is null ? [] : new[] { expected }, identifiers);
    }

    /// <summary>Native keyword proofs receive distinct unquoted column roots, never function or type roles.</summary>
    /// <param name="fold">Whether the provider requests native unquoted ASCII folding.</param>
    /// <param name="expected">The exact normalized unqualified member name.</param>
    [Theory]
    [InlineData(false, "VALUE")]
    [InlineData(true, "value")]
    public void RootColumnCollectionExcludesOtherTokenRoles(bool fold, string expected)
    {
        // Arrange
        const string sql = "LOWER(VALUE) + qualifier.VALUE + [Quoted] + CAST(other AS bigint) + 'VALUE'";

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryCollectUnquotedColumnRootIdentifiers(sql,
            out var identifiers, fold);

        // Assert
        Assert.True(parsed);
        Assert.Equal(new[] { expected, "qualifier", "other" }.Order(StringComparer.Ordinal),
            identifiers.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Provider folding changes only unquoted ASCII identifier roles, never quoted names or literals.
    /// </summary>
    [Fact]
    public void NativeIdentifierFoldingPreservesQuotedAndLiteralCase()
    {
        // Arrange
        const string sql = "VALUE + \"Value\" + \"\u00c6\" + '\u00c6ABC'";

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression,
            foldUnquotedIdentifiers: true);

        // Assert
        Assert.True(parsed);
        var names = new HashSet<string>(StringComparer.Ordinal);
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression!, names);
        Assert.Equal(["Value", "value", "\u00c6"], names.Order(StringComparer.Ordinal));
        var outer = Assert.IsType<SafeMigrationSqlBinaryExpression>(expression);
        Assert.Equal("\u00c6ABC", Assert.IsType<SafeMigrationSqlLiteralExpression>(outer.Right).Value);
    }

    /// <summary>
    /// Unquoted non-ASCII source remains outside the bounded grammar instead of receiving Unicode folding.
    /// </summary>
    /// <param name="sql">The native source whose encoding-dependent token rule is intentionally not certified.</param>
    [Theory]
    [InlineData("\u00c6 + 1")]
    [InlineData("A\u00c6 + 1")]
    public void NativeIdentifierFoldingDoesNotInventNonAsciiBindings(string sql)
    {
        // Arrange
        Assert.NotEmpty(sql);

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var expression,
            foldUnquotedIdentifiers: true);

        // Assert
        Assert.False(parsed);
        Assert.Null(expression);
    }

    /// <summary>Projection can discharge only native terminal-column quoting, never another source change.</summary>
    /// <param name="left">The captured projected source.</param>
    /// <param name="right">The newly authored source.</param>
    /// <param name="delimiter">The verified native identifier delimiter.</param>
    /// <param name="equivalent">Whether the difference consists only of certified native column quotes.</param>
    [Theory]
    [InlineData("\"value\" + 2147483648", "value + 2147483648", '"', true)]
    [InlineData("[value] + 1", "value + 1", '[', true)]
    [InlineData("`value` + 1", "value + 1", '`', true)]
    [InlineData("\"Value\" + 1", "Value + 1", '"', false)]
    [InlineData("\"value\" + 1", "value + 1.0", '"', false)]
    [InlineData("\"value\" + 1", "value  + 1", '"', false)]
    [InlineData("[value] + 1", "value + 1", '"', false)]
    [InlineData("\"value\" = 'a'", "value = 'A'", '"', false)]
    [InlineData("\"value\" IS NULL", "value is NULL", '"', false)]
    [InlineData("qualifier.\"value\" + 1", "\"qualifier\".value + 1", '"', false)]
    public void SourceQuoteEquivalenceRetainsAllOtherBytes(
        string left,
        string right,
        char delimiter,
        bool equivalent
    )
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(left, out _));
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(right, out _));

        // Act
        var actual = SafeMigrationSqlExpressionParser.SourcesDifferOnlyByNativeColumnQuotes(left, right, delimiter);

        // Assert
        Assert.Equal(equivalent, actual);
    }

    /// <summary>Unparsed national string prefixes cannot acquire a native column-quoting certificate.</summary>
    [Fact]
    public void SourceQuoteEquivalenceRejectsUnsupportedNationalPrefix()
    {
        // Arrange
        const string national = "\"value\" = N'x'";
        const string ordinary = "value = 'x'";
        Assert.False(SafeMigrationSqlExpressionParser.TryParseCapturedSource(national, out _));
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(ordinary, out _));

        // Act
        var equivalent = SafeMigrationSqlExpressionParser.SourcesDifferOnlyByNativeColumnQuotes(
            national, ordinary, '"');

        // Assert
        Assert.False(equivalent);
    }

    /// <summary>Captured function and CAST type roles cannot lose quoted native object identity.</summary>
    /// <param name="sql">The ambiguous quoted native binding.</param>
    [Theory]
    [InlineData("\"LOWER\"(\"name\")")]
    [InlineData("[COALESCE]([value], 0)")]
    [InlineData("`ABS`(`value`)")]
    [InlineData("CAST(value AS \"integer\")")]
    public void CapturedQuotedFunctionAndTypeRolesRemainOpaque(string sql)
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(sql, out _));

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var captured);

        // Assert
        Assert.False(parsed);
        Assert.Null(captured);
    }

    /// <summary>Provider quote checks inspect parsed identifier roles, never characters inside literal text.</summary>
    /// <param name="sql">The complete captured source.</param>
    /// <param name="delimiter">The native identifier quote.</param>
    /// <param name="compatible">Whether the quote roles belong to this provider.</param>
    [Theory]
    [InlineData("\"value\" > 0", '"', true)]
    [InlineData("[value] > 0", '"', false)]
    [InlineData("`value` > 0", '"', false)]
    [InlineData("[value] > 0", '[', true)]
    [InlineData("`value` > 0", '`', true)]
    [InlineData("value > 0 AND '[literal] `literal`' <> ''", '"', true)]
    [InlineData("value COLLATE [name] = 'x'", '"', false)]
    [InlineData("value COLLATE \"name\" = 'x'", '"', true)]
    public void SourceDialectChecksRetainTokenRoles(string sql, char delimiter, bool compatible)
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out _));

        // Act
        var actual = SafeMigrationSqlExpressionParser.IsCapturedSourceDialectCompatible(sql, delimiter);

        // Assert
        Assert.Equal(compatible, actual);
    }

    /// <summary>A provider can certify multiple native identifier delimiters without widening literal syntax.</summary>
    [Fact]
    public void SourceDialectAcceptsOnlyTheCompleteVerifiedAllowlist()
    {
        // Arrange
        const string sql = "[one] + `two` + \"three\"";

        // Act
        var complete = SafeMigrationSqlExpressionParser.IsCapturedSourceDialectCompatible(sql, "[`\"");
        var incomplete = SafeMigrationSqlExpressionParser.IsCapturedSourceDialectCompatible(sql, "[`");

        // Assert
        Assert.True(complete);
        Assert.False(incomplete);
    }

    /// <summary>Only generated-source capture retains a numeric lexeme and its immutable source storage.</summary>
    /// <param name="sql">The exact numeric token whose native type or precision must survive.</param>
    [Theory]
    [InlineData("2147483648")]
    [InlineData("1.234567890123456789e30")]
    [InlineData("00001")]
    [InlineData("0.000e0")]
    public void NumericSourceCaptureDoesNotChangeOrdinaryParserContracts(string sql)
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParse(sql, out var ordinary));

        // Act
        var parsed = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var captured);

        // Assert
        Assert.True(parsed);
        Assert.True(Assert.IsType<SafeMigrationSqlLiteralExpression>(ordinary).NumericSourceToken.IsEmpty);
        var literal = Assert.IsType<SafeMigrationSqlLiteralExpression>(captured);
        Assert.Equal(sql, literal.NumericSourceToken.Span.ToString());
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetString(
            literal.NumericSourceToken, out var source, out var start, out var length));

        Assert.Same(sql, source);
        Assert.Equal(0, start);
        Assert.Equal(sql.Length, length);
        Assert.False(SafeMigrationSqlExpressionContract.Equivalent(ordinary, captured));
    }

    /// <summary>Quoted tokens are replaced as whole tokens while unrelated source bytes remain exact.</summary>
    /// <param name="sql">The original source including native identifier delimiters.</param>
    /// <param name="source">The decoded physical source name.</param>
    /// <param name="target">The decoded replacement name.</param>
    /// <param name="expected">The exact source after the certified rewrite.</param>
    [Theory]
    [InlineData("[source] + 2147483648", "source", "new]name", "[new]]name] + 2147483648")]
    [InlineData("`source` + 1e3", "source", "new`name", "`new``name` + 1e3")]
    [InlineData("\"source\" + 1.0", "source", "new\"name", "\"new\"\"name\" + 1.0")]
    [InlineData("[old]]name] + 1", "old]name", "replacement", "[replacement] + 1")]
    [InlineData("qualifier.[source] + [qualifier]", "source", "target", "qualifier.[target] + [qualifier]")]
    public void RenamePreservesQuotedTokenBoundaries(string sql, string source, string target, string expected)
    {
        // Arrange
        Assert.True(SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out _));

        // Act
        var rewritten = SafeMigrationSqlExpressionParser.TryRenameColumnIdentifier(
            sql, source, target, StringComparer.Ordinal, out var renamedSql, out var renamed);

        // Assert
        Assert.True(rewritten);
        Assert.Equal(expected, renamedSql);
        Assert.NotNull(renamed);
        Assert.True(SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
            renamed, target, StringComparer.Ordinal));
    }

    /// <summary>Function names, type roles, literals, and qualifiers never become rename targets.</summary>
    [Fact]
    public void RenameRewritesOnlyTerminalColumnRoles()
    {
        // Arrange
        const string sql = "source([source]) + CAST(other AS source) + qualifier.[source]";

        // Act
        var rewritten = SafeMigrationSqlExpressionParser.TryRenameColumnIdentifier(
            sql, "source", "target", StringComparer.Ordinal, out var renamedSql, out var renamed);

        // Assert
        Assert.True(rewritten);
        Assert.Equal("source([target]) + CAST(other AS source) + qualifier.[target]", renamedSql);
        Assert.NotNull(renamed);
        Assert.False(SafeMigrationSqlExpressionInspector.ReferencesColumnIdentifier(
            renamed, "source", StringComparer.Ordinal));
    }

    /// <summary>
    /// Unquoted members require native quote authority even when the replacement resembles a keyword.
    /// </summary>
    /// <param name="delimiter">The verified provider quote.</param>
    /// <param name="expected">The exact token with that native quote.</param>
    [Theory]
    [InlineData('"', "\"select value\" + 1")]
    [InlineData('`', "`select value` + 1")]
    [InlineData('[', "[select value] + 1")]
    public void UnquotedRenameUsesVerifiedNativeDelimiter(char delimiter, string expected)
    {
        // Arrange
        const string sql = "source + 1";

        // Act
        var rewritten = SafeMigrationSqlExpressionParser.TryRenameColumnIdentifier(
            sql, "source", "select value", StringComparer.Ordinal,
            out var renamedSql, out var renamed, delimiter);

        // Assert
        Assert.True(rewritten);
        Assert.Equal(expected, renamedSql);
        Assert.NotNull(renamed);
    }

    /// <summary>Unproven quoting cannot emit a changed captured expression.</summary>
    [Fact]
    public void UnquotedRenameWithoutNativeDelimiterFailsClosed()
    {
        // Arrange
        const string sql = "source + 1";

        // Act
        var rewritten = SafeMigrationSqlExpressionParser.TryRenameColumnIdentifier(
            sql, "source", "target", StringComparer.Ordinal, out var renamedSql, out var renamed);

        // Assert
        Assert.False(rewritten);
        Assert.Null(renamedSql);
        Assert.Null(renamed);
    }
}
