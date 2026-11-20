namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationSqlExpressionParser
{
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

            // WHY: Pair corresponding tokens while preserving each source's independent quoted-text offsets.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < leftColumns.Count; index++)
            {
                var first = leftColumns[index];
                var second = rightColumns[index];
                var firstStart = first.Token.Start
                    - (first.Token.Delimiter == '\0'
                        ? 0
                        : 1);

                var secondStart = second.Token.Start
                    - (second.Token.Delimiter == '\0'
                        ? 0
                        : 1);

                if (!left
                        .AsSpan(leftOffset, firstStart - leftOffset)
                        .SequenceEqual(right.AsSpan(rightOffset, secondStart - rightOffset))
                    || !(columnComparer ?? StringComparer.Ordinal).Equals(first.Name, second.Name)
                    || !IsNativeColumnQuoteVariation(first, identifierDelimiter, nativeAsciiCaseIdentity)
                    || !IsNativeColumnQuoteVariation(second, identifierDelimiter, nativeAsciiCaseIdentity))
                {
                    return false;
                }

                leftOffset = first.Token.Start
                    + first.Token.Length
                    + (first.Token.Delimiter == '\0'
                        ? 0
                        : 1);

                rightOffset = second.Token.Start
                    + second.Token.Length
                    + (second.Token.Delimiter == '\0'
                        ? 0
                        : 1);
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

            // WHY: Consume the completed parser-owned token list in its original source order.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < columns.Count; index++)
            {
                var column = columns[index];

                if (column.RootToken.Delimiter == '\0')
                {
                    (unquoted ??= new HashSet<string>(StringComparer.Ordinal)).Add(column.RootName);
                }
            }

            // WHY: Return an owned snapshot without changing the established set materialization.
            // ReSharper disable once UseCollectionExpression
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

            // WHY: Rewrite each parser-owned reference once in its verified source-span order.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < columns.Count; index++)
            {
                var column = columns[index];

                if (!columnComparer.Equals(column.Name, source)
                    || StringComparer.Ordinal.Equals(column.Name, target))
                {
                    continue;
                }

                var delimiter = column.Token.Delimiter == '\0'
                    ? unquotedIdentifierDelimiter
                    : column.Token.Delimiter;

                if (delimiter is not ('"' or '`' or '['))
                {
                    // WHY: Core cannot guess whether ANSI quotes denote
                    // identifiers on the active provider/session. Unknown
                    // native quoting loses proof instead of emitting SQL.
                    return false;
                }

                var closing = delimiter == '['
                    ? ']'
                    : delimiter.Value;

                var replacementLength = target.Length + 2;

                // WHY: Traverse the string directly without replacing its character enumerator with a query pipeline.
                // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
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
                var start = column.Token.Start
                    - (column.Token.Delimiter == '\0'
                        ? 0
                        : 1);

                var end = column.Token.Start
                    + column.Token.Length
                    + (column.Token.Delimiter == '\0'
                        ? 0
                        : 1);

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
}
