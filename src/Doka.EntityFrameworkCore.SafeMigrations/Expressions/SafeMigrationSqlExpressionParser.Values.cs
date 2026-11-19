namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationSqlExpressionParser
{
    private ref partial struct Parser
    {
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

        /// <summary>Preserves searched CASE order and the same resource bounds as function arguments.</summary>
        private SafeMigrationSqlExpression ParseCase()
        {
            Advance();
            EnterDepth();
            try
            {
                var clauses = new List<SafeMigrationSqlCaseWhenClause>();
                do
                {
                    ExpectKeyword("WHEN");
                    if (clauses.Count == MaximumListItems)
                    {
                        Fail("list_too_long");
                    }

                    var condition = ParseOr();
                    ExpectKeyword("THEN");
                    var result = ParseOr();
                    clauses.Add(new SafeMigrationSqlCaseWhenClause(condition, result));
                } while (IsKeyword("WHEN"));

                var elseResult = MatchKeyword("ELSE")
                    ? ParseOr()
                    : null;

                ExpectKeyword("END");

                return SafeMigrationSql.Case(clauses, elseResult);
            }
            finally
            {
                ExitDepth();
            }
        }

        /// <summary>Captures SQL Server's two-argument conversion with an explicit type role.</summary>
        private SafeMigrationSqlCastExpression ParseConvert()
        {
            Advance();
            Expect(TokenKind.OpenParenthesis, "expected_cast_parenthesis");
            EnterDepth();
            try
            {
                // WHY: A CONVERT type is not a column dependency. Style
                // arguments have different semantics and stay fail-closed.
                var storeType = ParseStoreType(stopAtComma: true);
                Expect(TokenKind.Comma, "expected_convert_separator");
                var operand = ParseOr();
                Expect(TokenKind.CloseParenthesis, "expected_closing_parenthesis");

                return new SafeMigrationSqlCastExpression(operand, storeType, usesSqlServerConvert: true);
            }
            finally
            {
                ExitDepth();
            }
        }

        private string ParseStoreType(
            bool stopAtComma = false
        )
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
                if ((_current.Kind == TokenKind.CloseParenthesis || (stopAtComma && _current.Kind == TokenKind.Comma))
                    && nestedParentheses == 0)
                {
                    break;
                }

                // WHY: Only store-type grammar tokens are accepted; every other known token must fail closed.
                // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                switch (_current.Kind)
                {
                    case TokenKind.Identifier:
                        if (_captureNumericSource && _current.Delimiter != '\0')
                        {
                            // WHY: A quoted type may name a user-defined type
                            // rather than the builtin portable CAST target.
                            Fail("quoted_cast_type_not_supported");
                        }

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

            if (!_supportedDelimiters.IsEmpty
                && _current.Delimiter != '\0'
                && !_supportedDelimiters.Contains(_current.Delimiter))
            {
                // WHY: The structural grammar recognizes all provider quote
                // styles, but original captured SQL is still executed by one
                // native dialect. A different quote cannot certify a match.
                Fail("identifier_quote_dialect");
            }

            var value = _current.Delimiter == '\0'
                ? TokenText(_current)
                    .ToString()
                : DecodeDelimited(_current);

            if (_foldUnquotedIdentifiers
                && _current.Delimiter == '\0'
                && value
                    .AsSpan()
                    .IndexOfAnyInRange('A', 'Z')
                >= 0)
            {
                // WHY: PostgreSQL folds only unquoted identifier roles.
                // Quoted names and SQL literal contents retain exact case.
                value = string.Create(
                    value.Length,
                    value,
                    static (destination,
                        original) =>
                    {
                        // WHY: Write each folded ASCII code unit to the same destination offset without changing quoted
                        // roles.
                        // ReSharper disable once ForCanBeConvertedToForeach
                        // ReSharper disable once LoopCanBeConvertedToQuery
                        for (var index = 0; index < original.Length; index++)
                        {
                            var character = original[index];
                            destination[index] = character is >= 'A' and <= 'Z'
                                ? (char)(character + 32)
                                : character;
                        }
                    });
            }

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
    }
}
