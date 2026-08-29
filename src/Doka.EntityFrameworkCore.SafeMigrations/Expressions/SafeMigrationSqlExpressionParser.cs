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
internal static class SafeMigrationSqlExpressionParser
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
    )
    {
        ArgumentNullException.ThrowIfNull(sql);

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
            var parser = new Parser(sql);
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

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _source;
        private Token _current;
        private int _depth;
        private int _position;
        private int _tokenCount;

        public Parser(
            string source
        )
        {
            _source = source.AsSpan();
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

        private SafeMigrationSqlExpression ParseOr()
        {
            var expression = ParseAnd();
            while (MatchKeyword("OR"))
            {
                expression = SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.Or, ParseAnd());
            }

            return expression;
        }

        private SafeMigrationSqlExpression ParseAnd()
        {
            var expression = ParseNot();
            while (MatchKeyword("AND"))
            {
                expression = SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.And, ParseNot());
            }

            return expression;
        }

        private SafeMigrationSqlExpression ParseNot() => MatchKeyword("NOT")
            ? SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, ParseNot())
            : ParseComparison();

        private SafeMigrationSqlExpression ParseComparison()
        {
            var expression = ParseAdditive();

            if (MatchKeyword("IS"))
            {
                var negated = MatchKeyword("NOT");
                ExpectKeyword("NULL");
                return negated ? SafeMigrationSql.IsNotNull(expression) : SafeMigrationSql.IsNull(expression);
            }

            var negatedPredicate = MatchKeyword("NOT");
            if (MatchKeyword("BETWEEN"))
            {
                var lower = ParseAdditive();
                ExpectKeyword("AND");
                var upper = ParseAdditive();
                return SafeMigrationSql.Between(expression, lower, upper, negatedPredicate);
            }

            if (MatchKeyword("IN"))
            {
                Expect(TokenKind.OpenParenthesis, "expected_in_list");
                EnterDepth();
                try
                {
                    var values = new List<SafeMigrationSqlExpression>();
                    do
                    {
                        if (values.Count == MaximumListItems)
                        {
                            Fail("list_too_long");
                        }

                        values.Add(ParseOr());
                    } while (Match(TokenKind.Comma));

                    Expect(TokenKind.CloseParenthesis, "expected_closing_parenthesis");
                    return SafeMigrationSql.In(expression, values, negatedPredicate);
                }
                finally
                {
                    ExitDepth();
                }
            }

            if (negatedPredicate)
            {
                Fail("unsupported_not_predicate");
            }

            if (!TryReadComparisonOperator(out var @operator))
            {
                return expression;
            }

            return SafeMigrationSql.Binary(expression, @operator, ParseAdditive());
        }

        private SafeMigrationSqlExpression ParseAdditive()
        {
            var expression = ParseMultiplicative();
            while (_current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                var @operator = _current.Kind == TokenKind.Plus
                    ? SafeMigrationSqlBinaryOperator.Add
                    : SafeMigrationSqlBinaryOperator.Subtract;

                Advance();
                expression = SafeMigrationSql.Binary(expression, @operator, ParseMultiplicative());
            }

            return expression;
        }

        private SafeMigrationSqlExpression ParseMultiplicative()
        {
            var expression = ParseUnary();
            while (_current.Kind is TokenKind.Multiply or TokenKind.Divide or TokenKind.Modulo)
            {
                var @operator = _current.Kind switch
                {
                    TokenKind.Multiply => SafeMigrationSqlBinaryOperator.Multiply,
                    TokenKind.Divide => SafeMigrationSqlBinaryOperator.Divide,
                    TokenKind.Modulo => SafeMigrationSqlBinaryOperator.Modulo,
                    _ => throw new UnreachableException(),
                };

                Advance();
                expression = SafeMigrationSql.Binary(expression, @operator, ParseUnary());
            }

            return expression;
        }

        private SafeMigrationSqlExpression ParseUnary()
        {
            while (true)
            {
                if (Match(TokenKind.Plus))
                {
                    continue;
                }

                if (Match(TokenKind.Minus))
                {
                    return SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate, ParseUnary());
                }

                var expression = ParsePrimary();
                while (MatchKeyword("COLLATE"))
                {
                    var parts = ParseIdentifierPath();
                    if (parts.Count > 2)
                    {
                        Fail("invalid_collation_path");
                    }

                    expression = SafeMigrationSql.Collate(expression, parts[^1], parts.Count == 2 ? parts[0] : null);
                }

                return expression;
            }
        }

        private SafeMigrationSqlExpression ParsePrimary()
        {
            if (Match(TokenKind.OpenParenthesis))
            {
                EnterDepth();
                try
                {
                    var expression = ParseOr();
                    Expect(TokenKind.CloseParenthesis, "expected_closing_parenthesis");
                    return expression;
                }
                finally
                {
                    ExitDepth();
                }
            }

            if (_current.Kind == TokenKind.String)
            {
                var value = DecodeDelimited(_current);
                Advance();
                return SafeMigrationSql.Literal(value);
            }

            if (_current.Kind == TokenKind.Number)
            {
                var value = ParseNumber(_current);
                Advance();
                return SafeMigrationSql.Literal(value);
            }

            if (MatchKeyword("NULL"))
            {
                return SafeMigrationSql.Literal(null);
            }

            if (MatchKeyword("TRUE"))
            {
                return SafeMigrationSql.Literal(true);
            }

            if (MatchKeyword("FALSE"))
            {
                return SafeMigrationSql.Literal(false);
            }

            if (IsKeyword("CURRENT_DATE"))
            {
                Advance();
                return ParseCurrentValue(SafeMigrationSqlCurrentValue.Date);
            }

            if (IsKeyword("CURRENT_TIME"))
            {
                Advance();
                return ParseCurrentValue(SafeMigrationSqlCurrentValue.Time);
            }

            if (IsKeyword("CURRENT_TIMESTAMP"))
            {
                Advance();
                return ParseCurrentValue(SafeMigrationSqlCurrentValue.Timestamp);
            }

            if (IsKeyword("CAST"))
            {
                return ParseCast();
            }

            if (_current.Kind != TokenKind.Identifier)
            {
                Fail("expected_operand");
            }

            var name = ReadIdentifier();
            if (Match(TokenKind.OpenParenthesis))
            {
                EnterDepth();
                try
                {
                    var arguments = new List<SafeMigrationSqlExpression>();
                    if (!Match(TokenKind.CloseParenthesis))
                    {
                        do
                        {
                            if (arguments.Count == MaximumListItems)
                            {
                                Fail("list_too_long");
                            }

                            arguments.Add(ParseOr());
                        } while (Match(TokenKind.Comma));

                        Expect(TokenKind.CloseParenthesis, "expected_closing_parenthesis");
                    }

                    return SafeMigrationSql.Function(name, arguments.ToArray());
                }
                finally
                {
                    ExitDepth();
                }
            }

            var parts = new List<string>
            {
                name,
            };

            while (Match(TokenKind.Dot))
            {
                parts.Add(ReadIdentifier());
            }

            return new SafeMigrationSqlIdentifierExpression(parts);
        }

        private SafeMigrationSqlExpression ParseCast()
        {
            Advance();
            Expect(TokenKind.OpenParenthesis, "expected_cast_parenthesis");
            EnterDepth();
            try
            {
                var operand = ParseOr();
                ExpectKeyword("AS");
                var storeType = ParseStoreType();
                Expect(TokenKind.CloseParenthesis, "expected_closing_parenthesis");
                return SafeMigrationSql.Cast(operand, storeType);
            }
            finally
            {
                ExitDepth();
            }
        }

        private string ParseStoreType()
        {
            if (_current.Kind != TokenKind.Identifier)
            {
                Fail("expected_store_type");
            }

            var start = _current.Start;
            var nestedParentheses = 0;
            var sawIdentifier = false;
            while (_current.Kind != TokenKind.End)
            {
                if (_current.Kind == TokenKind.CloseParenthesis
                    && nestedParentheses == 0)
                {
                    break;
                }

                switch (_current.Kind)
                {
                    case TokenKind.Identifier:
                        sawIdentifier = true;
                        break;
                    case TokenKind.Number:
                    case TokenKind.Comma:
                    case TokenKind.Dot:
                        break;
                    case TokenKind.OpenParenthesis:
                        nestedParentheses++;
                        break;
                    case TokenKind.CloseParenthesis:
                        nestedParentheses--;
                        break;
                    default:
                        Fail("invalid_store_type");
                        break;
                }

                if (nestedParentheses < 0)
                {
                    Fail("invalid_store_type");
                }

                Advance();
            }

            if (!sawIdentifier
                || nestedParentheses != 0)
            {
                Fail("invalid_store_type");
            }

            var end = _current.Start;
            return _source[start..end]
                .Trim()
                .ToString();
        }

        private SafeMigrationSqlExpression ParseCurrentValue(
            SafeMigrationSqlCurrentValue value
        )
        {
            if (!Match(TokenKind.OpenParenthesis))
            {
                return SafeMigrationSql.Current(value);
            }

            if (_current.Kind != TokenKind.Number)
            {
                Fail("invalid_current_value_precision");
            }

            if (!int.TryParse(TokenText(_current), NumberStyles.None, CultureInfo.InvariantCulture, out var precision))
            {
                Fail("invalid_current_value_precision");
            }

            Advance();
            Expect(TokenKind.CloseParenthesis, "expected_closing_parenthesis");
            return SafeMigrationSql.Current(value, precision);
        }

        private List<string> ParseIdentifierPath()
        {
            var parts = new List<string>
            {
                ReadIdentifier(),
            };

            while (Match(TokenKind.Dot))
            {
                parts.Add(ReadIdentifier());
            }

            return parts;
        }

        private string ReadIdentifier()
        {
            if (_current.Kind != TokenKind.Identifier)
            {
                Fail("expected_identifier");
            }

            var value = _current.Delimiter == '\0'
                ? TokenText(_current)
                    .ToString()
                : DecodeDelimited(_current);

            Advance();
            return value;
        }

        private object ParseNumber(
            Token token
        )
        {
            var value = TokenText(token);
            if (value.ContainsAny('e', 'E'))
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue)
                    || !double.IsFinite(doubleValue))
                {
                    throw new SqlExpressionParseException("invalid_number");
                }

                return doubleValue;
            }

            if (value.Contains('.'))
            {
                return decimal.TryParse(
                    value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var decimalValue)
                    ? decimalValue
                    : throw new SqlExpressionParseException("invalid_number");
            }

            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var intValue))
            {
                return intValue;
            }

            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var longValue))
            {
                return longValue;
            }

            return decimal.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var integerDecimal)
                ? integerDecimal
                : throw new SqlExpressionParseException("invalid_number");
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
            && TokenText(_current).Equals(keyword, StringComparison.OrdinalIgnoreCase);

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

        private void Advance()
        {
            while (_position < _source.Length
                   && char.IsWhiteSpace(_source[_position]))
            {
                _position++;
            }

            if (_position == _source.Length)
            {
                _current = new Token(TokenKind.End, _position, 0, '\0');
                return;
            }

            _tokenCount++;
            if (_tokenCount > MaximumTokens)
            {
                throw new SqlExpressionParseException("expression_too_complex");
            }

            var start = _position;
            var character = _source[_position++];
            _current = character switch
            {
                '(' => new Token(TokenKind.OpenParenthesis, start, 1, '\0'),
                ')' => new Token(TokenKind.CloseParenthesis, start, 1, '\0'),
                ',' => new Token(TokenKind.Comma, start, 1, '\0'),
                '.' => new Token(TokenKind.Dot, start, 1, '\0'),
                '+' => new Token(TokenKind.Plus, start, 1, '\0'),
                '-' when Peek('-') => throw new SqlExpressionParseException("comments_not_supported"),
                '-' => new Token(TokenKind.Minus, start, 1, '\0'),
                '*' => new Token(TokenKind.Multiply, start, 1, '\0'),
                '/' when Peek('*') => throw new SqlExpressionParseException("comments_not_supported"),
                '/' => new Token(TokenKind.Divide, start, 1, '\0'),
                '%' => new Token(TokenKind.Modulo, start, 1, '\0'),
                '=' => new Token(TokenKind.Equal, start, 1, '\0'),
                '<' when Peek('=') => ReadTwoCharacterToken(TokenKind.LessThanOrEqual, start),
                '<' when Peek('>') => ReadTwoCharacterToken(TokenKind.NotEqual, start),
                '<' => new Token(TokenKind.LessThan, start, 1, '\0'),
                '>' when Peek('=') => ReadTwoCharacterToken(TokenKind.GreaterThanOrEqual, start),
                '>' => new Token(TokenKind.GreaterThan, start, 1, '\0'),
                '!' when Peek('=') => ReadTwoCharacterToken(TokenKind.NotEqual, start),
                '\'' => ReadDelimitedToken(TokenKind.String, '\'', '\''),
                '"' => ReadDelimitedToken(TokenKind.Identifier, '"', '"'),
                '`' => ReadDelimitedToken(TokenKind.Identifier, '`', '`'),
                '[' => ReadDelimitedToken(TokenKind.Identifier, '[', ']'),
                _ when char.IsAsciiDigit(character) => ReadNumberToken(start),
                _ when IsIdentifierStart(character) => ReadIdentifierToken(start),
                _ => throw new SqlExpressionParseException("invalid_token"),
            };
        }

        private Token ReadTwoCharacterToken(
            TokenKind kind,
            int start
        )
        {
            _position++;
            return new Token(kind, start, 2, '\0');
        }

        private Token ReadDelimitedToken(
            TokenKind kind,
            char opening,
            char closing
        )
        {
            var contentStart = _position;
            while (_position < _source.Length)
            {
                // Backslash escaping is provider and server-mode dependent.
                // Only delimiter doubling has identical meaning throughout the
                // bounded provider-neutral grammar.
                if (_source[_position] == '\\')
                {
                    throw new SqlExpressionParseException("provider_escape_not_supported");
                }

                if (_source[_position] != closing)
                {
                    _position++;
                    continue;
                }

                if (_position + 1 < _source.Length
                    && _source[_position + 1] == closing)
                {
                    _position += 2;
                    continue;
                }

                var contentLength = _position - contentStart;
                _position++;
                if (contentLength == 0
                    && kind == TokenKind.Identifier)
                {
                    throw new SqlExpressionParseException("empty_identifier");
                }

                return new Token(kind, contentStart, contentLength, opening);
            }

            throw new SqlExpressionParseException("unterminated_delimited_token");
        }

        private Token ReadNumberToken(
            int start
        )
        {
            while (_position < _source.Length
                   && char.IsAsciiDigit(_source[_position]))
            {
                _position++;
            }

            if (_position < _source.Length
                && _source[_position] == '.')
            {
                _position++;
                if (_position == _source.Length
                    || !char.IsAsciiDigit(_source[_position]))
                {
                    throw new SqlExpressionParseException("invalid_number");
                }

                while (_position < _source.Length
                       && char.IsAsciiDigit(_source[_position]))
                {
                    _position++;
                }
            }

            if (_position < _source.Length
                && _source[_position] is 'e' or 'E')
            {
                _position++;
                if (_position < _source.Length
                    && _source[_position] is '+' or '-')
                {
                    _position++;
                }

                if (_position == _source.Length
                    || !char.IsAsciiDigit(_source[_position]))
                {
                    throw new SqlExpressionParseException("invalid_number");
                }

                while (_position < _source.Length
                       && char.IsAsciiDigit(_source[_position]))
                {
                    _position++;
                }
            }

            return new Token(TokenKind.Number, start, _position - start, '\0');
        }

        private Token ReadIdentifierToken(
            int start
        )
        {
            while (_position < _source.Length
                   && IsIdentifierPart(_source[_position]))
            {
                _position++;
            }

            return new Token(TokenKind.Identifier, start, _position - start, '\0');
        }

        private string DecodeDelimited(
            Token token
        )
        {
            var raw = TokenText(token);
            var closing = token.Delimiter == '[' ? ']' : token.Delimiter;
            var escapedIndex = raw.IndexOf(closing);
            if (escapedIndex < 0)
            {
                return raw.ToString();
            }

            var builder = new StringBuilder(raw.Length);
            var start = 0;
            while (escapedIndex >= 0)
            {
                builder.Append(raw[start..escapedIndex]);
                builder.Append(closing);
                start = escapedIndex + 2;
                escapedIndex = raw[start..]
                    .IndexOf(closing);
                if (escapedIndex >= 0)
                {
                    escapedIndex += start;
                }
            }

            builder.Append(raw[start..]);
            return builder.ToString();
        }

        private ReadOnlySpan<char> TokenText(
            Token token
        ) => _source.Slice(token.Start, token.Length);

        private bool Peek(
            char expected
        ) => _position < _source.Length && _source[_position] == expected;

        private static bool IsIdentifierStart(
            char value
        ) => value == '_' || char.IsAsciiLetter(value);

        private static bool IsIdentifierPart(
            char value
        ) => value is '_' or '$' || char.IsAsciiLetterOrDigit(value);

        [DoesNotReturn]
        private static void Fail(
            string failureCode
        ) => throw new SqlExpressionParseException(failureCode);
    }

    private readonly record struct Token(
        TokenKind Kind,
        int Start,
        int Length,
        char Delimiter
    );

    private enum TokenKind
    {
        End,
        Identifier,
        String,
        Number,
        OpenParenthesis,
        CloseParenthesis,
        Comma,
        Dot,
        Plus,
        Minus,
        Multiply,
        Divide,
        Modulo,
        Equal,
        NotEqual,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual,
    }

    private sealed class SqlExpressionParseException(string failureCode) : Exception
    {
        public string FailureCode { get; } = failureCode;
    }
}
