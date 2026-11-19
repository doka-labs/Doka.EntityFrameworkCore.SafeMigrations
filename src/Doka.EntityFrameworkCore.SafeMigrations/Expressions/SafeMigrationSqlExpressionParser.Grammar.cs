namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationSqlExpressionParser
{
    private ref partial struct Parser
    {
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

                return negated
                    ? SafeMigrationSql.IsNotNull(expression)
                    : SafeMigrationSql.IsNull(expression);
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

            return TryReadComparisonOperator(out var @operator)
                ? SafeMigrationSql.Binary(expression, @operator, ParseAdditive())
                : expression;
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
                // WHY: The surrounding loop admits only these three operators; all other token kinds are invalid here.
                // ReSharper disable once SwitchExpressionHandlesSomeKnownEnumValuesWithExceptionInDefault
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
                    if (_captureNumericSource)
                    {
                        // WHY: The portable tree has no unary-plus node. Dropping
                        // an overloadable native operator would certify a
                        // different expression, even when its scalar type agrees.
                        Fail("captured_unary_plus_not_supported");
                    }

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

                    expression = SafeMigrationSql.Collate(
                        expression,
                        parts[^1],
                        parts.Count == 2
                            ? parts[0]
                            : null);
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

            // WHY: Each token guard decodes and returns its own value; case scopes add no useful state grouping.
            // ReSharper disable once ConvertIfStatementToSwitchStatement
            if (_current.Kind == TokenKind.String)
            {
                var value = DecodeDelimited(_current);
                Advance();

                return SafeMigrationSql.Literal(value);
            }

            if (_current.Kind == TokenKind.Number)
            {
                var value = ParseNumber(_current);
                var numericSource = _captureNumericSource
                    ? _sourceText.AsMemory(_current.Start, _current.Length)
                    : default;

                Advance();

                return new SafeMigrationSqlLiteralExpression(value, storeType: null, numericSource);
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

            if (IsKeyword("CASE"))
            {
                return ParseCase();
            }

            if (IsKeyword("CONVERT"))
            {
                return ParseConvert();
            }

            if (_current.Kind != TokenKind.Identifier)
            {
                Fail("expected_operand");
            }

            if (IsKeyword("WHEN")
                || IsKeyword("THEN")
                || IsKeyword("ELSE")
                || IsKeyword("END"))
            {
                Fail("expected_operand");
            }

            var terminal = _current;
            var root = terminal;
            var name = ReadIdentifier();
            if (Match(TokenKind.OpenParenthesis))
            {
                if (_captureNumericSource && terminal.Delimiter != '\0')
                {
                    // WHY: Quoted function names can select a different native
                    // function than their unquoted spelling. The portable AST
                    // has no quoted-function role, so capture stays opaque.
                    Fail("quoted_function_not_supported");
                }

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

                    // WHY: Preserve the parser's exact ordered array handoff to the span-based factory.
                    // ReSharper disable once UseCollectionExpression
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
                terminal = _current;
                parts.Add(ReadIdentifier());
            }

            _columnReferences?.Add(new ColumnReferenceToken(terminal, parts[^1], root, name));

            return new SafeMigrationSqlIdentifierExpression(parts);
        }
    }
}
