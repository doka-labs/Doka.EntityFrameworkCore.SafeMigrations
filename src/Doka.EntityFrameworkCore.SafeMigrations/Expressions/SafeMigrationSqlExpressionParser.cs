namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Parses the bounded provider-neutral SQL subset that SafeMigrations can
/// compare structurally across authoring, catalog, and rename boundaries.
/// </summary>
/// <remarks>
/// The parser intentionally accepts only syntax whose meaning is stable across
/// supported providers. Length, token, list, and nesting limits make rejection
/// deterministic for untrusted generated or catalog SQL. A failed parse never
/// exposes a partial expression tree.
/// </remarks>
internal static partial class SafeMigrationSqlExpressionParser
{
    /// <summary>Gets the maximum accepted SQL-text length.</summary>
    internal const int MaximumLength = 16_384;

    /// <summary>Gets the maximum accepted parenthesis and function nesting depth.</summary>
    internal const int MaximumDepth = 64;

    /// <summary>Gets the maximum accepted number of values in one list or argument set.</summary>
    internal const int MaximumListItems = 256;

    /// <summary>Gets the maximum accepted token count.</summary>
    internal const int MaximumTokens = 1_024;

    /// <summary>Attempts to parse provider-neutral SQL into a structural expression.</summary>
    /// <param name="sql">The SQL expression text.</param>
    /// <param name="expression">The complete expression when parsing succeeds; otherwise null.</param>
    /// <returns><see langword="true" /> when the complete input is accepted.</returns>
    public static bool TryParse(
        string sql,
        [NotNullWhen(true)] out SafeMigrationSqlExpression? expression
    ) => TryParse(sql, out expression, out _);

    /// <summary>
    /// Attempts to parse provider-neutral SQL and reports a stable failure code
    /// when the complete input cannot be represented safely.
    /// </summary>
    /// <param name="sql">The SQL expression text.</param>
    /// <param name="expression">The complete expression when parsing succeeds; otherwise null.</param>
    /// <param name="failureCode">An empty string on success; otherwise the stable rejection code.</param>
    /// <returns><see langword="true" /> when the complete input is accepted.</returns>
    public static bool TryParse(
        string sql,
        [NotNullWhen(true)] out SafeMigrationSqlExpression? expression,
        out string failureCode
    ) => TryParse(sql, out expression, out failureCode, captureNumericSource: false);

    /// <summary>
    /// Captures validated generated metadata without discarding provider-owned numeric token spelling.
    /// </summary>
    internal static bool TryParseCapturedSource(
        string sql,
        [NotNullWhen(true)] out SafeMigrationSqlExpression? expression,
        bool foldUnquotedIdentifiers = false
    ) => TryParse(sql, out expression, out _, captureNumericSource: true, foldUnquotedIdentifiers);

    /// <summary>Captures generated metadata and reports a stable grammar rejection without a partial tree.</summary>
    internal static bool TryParseCapturedSource(
        string sql,
        [NotNullWhen(true)] out SafeMigrationSqlExpression? expression,
        out string failureCode,
        bool foldUnquotedIdentifiers = false
    ) => TryParse(sql, out expression, out failureCode, captureNumericSource: true, foldUnquotedIdentifiers);

    private static bool TryParse(
        string sql,
        [NotNullWhen(true)] out SafeMigrationSqlExpression? expression,
        out string failureCode,
        bool captureNumericSource,
        bool foldUnquotedIdentifiers = false
    )
    {
        ArgumentNullException.ThrowIfNull(sql);

        // WHY: Separate rejection guards retain the distinct failure codes without a statement-switch scaffold.
        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (sql.Length == 0)
        {
            expression = null;
            failureCode = "empty_expression";

            return false;
        }

        if (sql.Length > MaximumLength)
        {
            expression = null;
            failureCode = "expression_too_long";

            return false;
        }

        try
        {
            var parser = new Parser(
                sql,
                captureNumericSource: captureNumericSource,
                foldUnquotedIdentifiers: foldUnquotedIdentifiers);

            expression = parser.Parse();
            failureCode = string.Empty;

            return true;
        }
        catch (SqlExpressionParseException exception)
        {
            expression = null;
            failureCode = exception.FailureCode;

            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            expression = null;
            failureCode = "invalid_expression";

            return false;
        }
    }

    private ref partial struct Parser
    {
        private readonly string _sourceText;
        private readonly ReadOnlySpan<char> _source;
        private readonly List<ColumnReferenceToken>? _columnReferences;
        private readonly bool _captureNumericSource;
        private readonly ReadOnlySpan<char> _supportedDelimiters;
        private readonly bool _foldUnquotedIdentifiers;
        private Token _current;
        private int _depth;
        private int _position;
        private int _tokenCount;

        public Parser(
            string source,
            List<ColumnReferenceToken>? columnReferences = null,
            bool captureNumericSource = false,
            ReadOnlySpan<char> supportedDelimiters = default,
            bool foldUnquotedIdentifiers = false
        )
        {
            _sourceText = source;
            _source = source.AsSpan();
            _columnReferences = columnReferences;
            _captureNumericSource = captureNumericSource;
            _supportedDelimiters = supportedDelimiters;
            _foldUnquotedIdentifiers = foldUnquotedIdentifiers;
            _current = default;
            _depth = 0;
            _position = 0;
            _tokenCount = 0;
            Advance();
        }

        public SafeMigrationSqlExpression Parse()
        {
            var expression = ParseOr();
            if (_current.Kind != TokenKind.End)
            {
                Fail("trailing_token");
            }

            return expression;
        }

        private bool TryReadComparisonOperator(
            out SafeMigrationSqlBinaryOperator @operator
        )
        {
            @operator = _current.Kind switch
            {
                TokenKind.Equal => SafeMigrationSqlBinaryOperator.Equal,
                TokenKind.NotEqual => SafeMigrationSqlBinaryOperator.NotEqual,
                TokenKind.LessThan => SafeMigrationSqlBinaryOperator.LessThan,
                TokenKind.LessThanOrEqual => SafeMigrationSqlBinaryOperator.LessThanOrEqual,
                TokenKind.GreaterThan => SafeMigrationSqlBinaryOperator.GreaterThan,
                TokenKind.GreaterThanOrEqual => SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                _ => default,
            };

            if (_current.Kind is not (TokenKind.Equal
                or TokenKind.NotEqual
                or TokenKind.LessThan
                or TokenKind.LessThanOrEqual
                or TokenKind.GreaterThan
                or TokenKind.GreaterThanOrEqual))
            {
                return false;
            }

            Advance();

            return true;
        }

        private bool MatchKeyword(
            string keyword
        )
        {
            if (!IsKeyword(keyword))
            {
                return false;
            }

            Advance();

            return true;
        }

        private bool IsKeyword(
            string keyword
        ) => _current is { Kind: TokenKind.Identifier, Delimiter: '\0' }
            && TokenText(_current)
                .Equals(keyword, StringComparison.OrdinalIgnoreCase);

        private void ExpectKeyword(
            string keyword
        )
        {
            if (!MatchKeyword(keyword))
            {
                Fail("expected_" + keyword.ToLowerInvariant());
            }
        }

        private bool Match(
            TokenKind kind
        )
        {
            if (_current.Kind != kind)
            {
                return false;
            }

            Advance();

            return true;
        }

        private void Expect(
            TokenKind kind,
            string failureCode
        )
        {
            if (!Match(kind))
            {
                Fail(failureCode);
            }
        }

        private void EnterDepth()
        {
            _depth++;
            if (_depth > MaximumDepth)
            {
                Fail("expression_too_deep");
            }
        }

        private void ExitDepth() => _depth--;

        [DoesNotReturn]
        private static void Fail(
            string failureCode
        ) => throw new SqlExpressionParseException(failureCode);
    }
}
