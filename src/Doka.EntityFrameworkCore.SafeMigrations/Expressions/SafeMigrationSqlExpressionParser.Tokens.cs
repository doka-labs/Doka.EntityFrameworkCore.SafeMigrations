namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationSqlExpressionParser
{
    private ref partial struct Parser
    {
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
            var closing = token.Delimiter == '['
                ? ']'
                : token.Delimiter;

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
    }

    private readonly record struct ColumnReferenceToken(
        Token Token,
        string Name,
        Token RootToken,
        string RootName
    );

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

    private sealed class SqlExpressionParseException : Exception
    {
        /// <summary>Retains a parser-owned failure category without exposing input SQL.</summary>
        /// <param name="failureCode">The stable classification code.</param>
        public SqlExpressionParseException(
            string failureCode
        )
        {
            FailureCode = failureCode;
        }

        /// <summary>Gets the stable parser failure category.</summary>
        public string FailureCode { get; }
    }
}
