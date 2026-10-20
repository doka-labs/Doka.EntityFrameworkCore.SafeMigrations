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

    /// <summary>
    /// Validates native identifier quote roles without inspecting quote characters inside SQL literals.
    /// </summary>
    /// <param name="sql">The complete captured native expression.</param>
    /// <param name="identifierDelimiter">The identifier opening delimiter certified by the active provider.</param>
    /// <returns>Whether every parsed identifier role uses the native delimiter or unquoted native spelling.</returns>
    internal static bool IsCapturedSourceDialectCompatible(
        string sql,
        char identifierDelimiter
    ) => IsCapturedSourceDialectCompatible(sql, [identifierDelimiter]);

    /// <summary>Validates one provider's complete verified native identifier-quote allowlist.</summary>
    internal static bool IsCapturedSourceDialectCompatible(
        string sql,
        ReadOnlySpan<char> identifierDelimiters
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (sql.Length is 0 or > MaximumLength
            || identifierDelimiters.IsEmpty)
        {
            return false;
        }

        foreach (var delimiter in identifierDelimiters)
        {
            if (delimiter is not ('"' or '`' or '['))
            {
                return false;
            }
        }

        try
        {
            var parser = new Parser(sql, captureNumericSource: true, supportedDelimiters: identifierDelimiters);
            _ = parser.Parse();

            return true;
        }
        catch (Exception exception) when (exception is SqlExpressionParseException
                                              or ArgumentException
                                              or FormatException
                                              or OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Proves an exact captured-source difference confined to native terminal-column identifier quotes.
    /// </summary>
    /// <param name="left">The first complete captured source.</param>
    /// <param name="right">The second complete captured source.</param>
    /// <param name="identifierDelimiter">The provider-certified native identifier quote.</param>
    /// <param name="foldUnquotedIdentifiers">Whether unquoted ASCII case has provider-certified native folding.</param>
    /// <param name="columnComparer">The separately certified terminal-member identity, or null for exact names.</param>
    /// <returns>Whether every other source byte is identical and terminal members retain native identity.</returns>
    internal static bool SourcesDifferOnlyByNativeColumnQuotes(
        string left,
        string right,
        char identifierDelimiter,
        bool foldUnquotedIdentifiers = false,
        IEqualityComparer<string>? columnComparer = null
    )
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        // WHY: This proves byte/token variation, not native SQL validity.
        // Provider analysis must independently qualify syntax and bindings;
        // normalization cannot discharge an invariant unsupported source.
        if (left.Length is 0 or > MaximumLength
            || right.Length is 0 or > MaximumLength
            || identifierDelimiter is not ('"' or '`' or '['))
        {
            return false;
        }

        try
        {
            // WHY: Native double quotes do not identify PostgreSQL alone.
            // A separately supplied CI member policy can certify ASCII case
            // identity without inventing an unquoted-folding rule for SQLite.
            var nativeAsciiCaseIdentity = foldUnquotedIdentifiers || columnComparer?.Equals("A", "a") == true;

            var leftColumns = new List<ColumnReferenceToken>();
            var rightColumns = new List<ColumnReferenceToken>();
            var leftParser = new Parser(
                left,
                leftColumns,
                captureNumericSource: true,
                foldUnquotedIdentifiers: foldUnquotedIdentifiers);

            var rightParser = new Parser(
                right,
                rightColumns,
                captureNumericSource: true,
                foldUnquotedIdentifiers: foldUnquotedIdentifiers);

            _ = leftParser.Parse();
            _ = rightParser.Parse();
            if (leftColumns.Count != rightColumns.Count)
            {
                return false;
            }

            var leftOffset = 0;
            var rightOffset = 0;
            for (var index = 0; index < leftColumns.Count; index++)
            {
                var first = leftColumns[index];
                var second = rightColumns[index];
                var firstStart = first.Token.Start - (first.Token.Delimiter == '\0' ? 0 : 1);
                var secondStart = second.Token.Start - (second.Token.Delimiter == '\0' ? 0 : 1);
                if (!left
                        .AsSpan(leftOffset, firstStart - leftOffset)
                        .SequenceEqual(right.AsSpan(rightOffset, secondStart - rightOffset))
                    || !(columnComparer ?? StringComparer.Ordinal).Equals(first.Name, second.Name)
                    || !IsNativeColumnQuoteVariation(first, identifierDelimiter, nativeAsciiCaseIdentity)
                    || !IsNativeColumnQuoteVariation(second, identifierDelimiter, nativeAsciiCaseIdentity))
                {
                    return false;
                }

                leftOffset = first.Token.Start + first.Token.Length + (first.Token.Delimiter == '\0' ? 0 : 1);
                rightOffset = second.Token.Start + second.Token.Length + (second.Token.Delimiter == '\0' ? 0 : 1);
            }

            return left
                .AsSpan(leftOffset)
                .SequenceEqual(right.AsSpan(rightOffset));
        }
        catch (Exception exception) when (exception is SqlExpressionParseException
                                              or ArgumentException
                                              or FormatException
                                              or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Allows ASCII case variation only with native fold or separately certified CI member identity.</summary>
    private static bool IsNativeColumnQuoteVariation(
        ColumnReferenceToken column,
        char delimiter,
        bool nativeAsciiCaseIdentity
    ) => column.Token.Delimiter == delimiter
        || (column.Token.Delimiter == '\0'
            && (delimiter != '"'
                || nativeAsciiCaseIdentity
                || column
                    .Name
                    .AsSpan()
                    .IndexOfAnyInRange('A', 'Z')
                < 0));

    /// <summary>Collects the unquoted first token of column paths, where native ColId keyword rules apply.</summary>
    /// <param name="sql">The bounded captured expression.</param>
    /// <param name="identifiers">Distinct unquoted root names, or an empty array when parsing fails.</param>
    /// <param name="foldUnquotedIdentifiers">Whether the active provider folds unquoted ASCII uppercase names.</param>
    /// <returns>Whether the complete captured source grammar is valid.</returns>
    internal static bool TryCollectUnquotedColumnRootIdentifiers(
        string sql,
        out string[] identifiers,
        bool foldUnquotedIdentifiers = false
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        identifiers = [];
        if (sql.Length is 0 or > MaximumLength)
        {
            return false;
        }

        try
        {
            var columns = new List<ColumnReferenceToken>();
            var parser = new Parser(
                sql,
                columns,
                captureNumericSource: true,
                foldUnquotedIdentifiers: foldUnquotedIdentifiers);

            _ = parser.Parse();
            HashSet<string>? unquoted = null;
            foreach (var column in columns)
            {
                if (column.RootToken.Delimiter == '\0')
                {
                    (unquoted ??= new HashSet<string>(StringComparer.Ordinal)).Add(column.RootName);
                }
            }

            identifiers = unquoted?.ToArray() ?? [];

            return true;
        }
        catch (Exception exception) when (exception is SqlExpressionParseException
                                              or ArgumentException
                                              or FormatException
                                              or OverflowException)
        {
            return false;
        }
    }

    private static bool TryParse(
        string sql,
        [NotNullWhen(true)] out SafeMigrationSqlExpression? expression,
        out string failureCode,
        bool captureNumericSource,
        bool foldUnquotedIdentifiers = false
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

    /// <summary>Rewrites only parsed terminal column tokens while preserving every other source byte.</summary>
    /// <param name="sql">The complete captured expression.</param>
    /// <param name="source">The physical column member to replace.</param>
    /// <param name="target">The replacement physical member.</param>
    /// <param name="columnComparer">The provider-certified column identity comparer.</param>
    /// <param name="renamedSql">The rewritten source, or the original reference when no token changes.</param>
    /// <param name="renamedExpression">The complete reparsed expression on success.</param>
    /// <param name="unquotedIdentifierDelimiter">The verified native quote for originally unquoted members.</param>
    /// <param name="foldUnquotedIdentifiers">Whether native identifier roles fold unquoted ASCII uppercase.</param>
    /// <returns>Whether the complete rewrite retains the bounded grammar and certified identifier roles.</returns>
    internal static bool TryRenameColumnIdentifier(
        string sql,
        string source,
        string target,
        IEqualityComparer<string> columnComparer,
        [NotNullWhen(true)] out string? renamedSql,
        [NotNullWhen(true)] out SafeMigrationSqlExpression? renamedExpression,
        char? unquotedIdentifierDelimiter = null,
        bool foldUnquotedIdentifiers = false
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(columnComparer);

        renamedSql = null;
        renamedExpression = null;
        if (sql.Length is 0 or > MaximumLength
            || target.Length > MaximumLength)
        {
            return false;
        }

        try
        {
            var columns = new List<ColumnReferenceToken>();
            var parser = new Parser(
                sql,
                columns,
                captureNumericSource: true,
                foldUnquotedIdentifiers: foldUnquotedIdentifiers);

            var expression = parser.Parse();
            StringBuilder? rewritten = null;
            var copied = 0;
            foreach (var column in columns)
            {
                if (!columnComparer.Equals(column.Name, source)
                    || StringComparer.Ordinal.Equals(column.Name, target))
                {
                    continue;
                }

                var delimiter = column.Token.Delimiter == '\0' ? unquotedIdentifierDelimiter : column.Token.Delimiter;

                if (delimiter is not ('"' or '`' or '['))
                {
                    // WHY: Core cannot guess whether ANSI quotes denote
                    // identifiers on the active provider/session. Unknown
                    // native quoting loses proof instead of emitting SQL.
                    return false;
                }

                var closing = delimiter == '[' ? ']' : delimiter.Value;
                var replacementLength = target.Length + 2;
                foreach (var character in target)
                {
                    if (character == closing)
                    {
                        replacementLength++;
                    }
                }

                // WHY: Delimited lexer tokens expose their content span for
                // decoding. A source rewrite replaces the enclosing quotes
                // as well, including any doubled closing delimiter inside.
                var start = column.Token.Start - (column.Token.Delimiter == '\0' ? 0 : 1);
                var end = column.Token.Start + column.Token.Length + (column.Token.Delimiter == '\0' ? 0 : 1);
                if ((long)(rewritten?.Length ?? 0) + start - copied + replacementLength + sql.Length - end
                    > MaximumLength)
                {
                    return false;
                }

                rewritten ??= new StringBuilder(sql.Length);
                rewritten.Append(sql.AsSpan(copied, start - copied));
                rewritten.Append(delimiter.Value);
                foreach (var character in target)
                {
                    rewritten.Append(character);
                    if (character == closing)
                    {
                        rewritten.Append(closing);
                    }
                }

                rewritten.Append(closing);
                copied = end;
            }

            if (rewritten is null)
            {
                renamedSql = sql;
                renamedExpression = expression;

                return true;
            }

            rewritten.Append(sql.AsSpan(copied));
            var result = rewritten.ToString();
            if (!TryParseCapturedSource(result, out renamedExpression, foldUnquotedIdentifiers))
            {
                return false;
            }

            renamedSql = result;

            return true;
        }
        catch (Exception exception) when (exception is SqlExpressionParseException
                                              or ArgumentException
                                              or FormatException
                                              or OverflowException)
        {
            return false;
        }
    }

    private ref struct Parser
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

                var elseResult = MatchKeyword("ELSE") ? ParseOr() : null;
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
                    static (
                        destination,
                        original
                    ) =>
                    {
                        for (var index = 0; index < original.Length; index++)
                        {
                            var character = original[index];
                            destination[index] = character is >= 'A' and <= 'Z' ? (char)(character + 32) : character;
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

    private sealed class SqlExpressionParseException(string failureCode) : Exception
    {
        public string FailureCode { get; } = failureCode;
    }
}
