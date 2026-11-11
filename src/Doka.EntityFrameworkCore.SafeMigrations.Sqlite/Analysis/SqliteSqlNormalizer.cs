namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Normalizes SQLite expressions for bounded semantic comparison.</summary>
internal static class SqliteSqlNormalizer
{
    /// <summary>Determines whether two SQLite expressions are semantically equivalent.</summary>
    public static bool Equivalent(
        string? left,
        string? right
    ) => left is null || right is null
        ? left is null && right is null
        : StringComparer.Ordinal.Equals(Normalize(left), Normalize(right));

    /// <summary>Compares DEFAULT SQL without folding double-quoted string payloads into identifier case.</summary>
    /// <param name="left">The complete native default expression.</param>
    /// <param name="right">The complete authored default expression.</param>
    /// <returns>Whether the normalized syntax and every quoted payload remain identical.</returns>
    internal static bool EquivalentDefault(
        string? left,
        string? right
    ) => left is null || right is null
        ? left is null && right is null
        : StringComparer.Ordinal.Equals(Normalize(left, preserveDoubleQuotedValues: true),
            Normalize(right, preserveDoubleQuotedValues: true));

    /// <summary>Compares bounded expressions while retaining native numeric and string source tokens.</summary>
    /// <param name="left">The live expression source.</param>
    /// <param name="right">The validated expected expression source.</param>
    /// <returns>Whether only identifier delimiters, trivia, or redundant parentheses differ.</returns>
    public static bool EquivalentCapturedSource(
        string? left,
        string? right
    )
    {
        if (left is null
            || right is null)
        {
            return left is null && right is null;
        }

        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        if (StringComparer.Ordinal.Equals(normalizedLeft, normalizedRight))
        {
            return true;
        }

        // WHY: The shared bounded parser treats Unicode whitespace as trivia,
        // but SQLite permits it in unquoted physical identifiers. Only exact
        // native normalization can certify those sources without changing Core.
        if (SqliteSqlIdentifierScanner.HasNonNativeWhitespace(normalizedLeft)
            || SqliteSqlIdentifierScanner.HasNonNativeWhitespace(normalizedRight))
        {
            return false;
        }

        // WHY: SQLite treats -9223372036854775808 as INTEGER but the same
        // token with .0 as REAL. Rendering a CLR decimal would erase that
        // distinction; captured parsing keeps numeric lexemes and literal bytes.
        return SafeMigrationSqlExpressionParser.TryParseCapturedSource(normalizedLeft, out var parsedLeft)
            && SafeMigrationSqlExpressionParser.TryParseCapturedSource(normalizedRight, out var parsedRight)
            && SafeMigrationSqlExpressionContract.Equivalent(parsedLeft, parsedRight);
    }

    /// <summary>Produces the canonical comparison form of a SQLite expression.</summary>
    public static string Normalize(
        string value
    ) => Normalize(value, preserveDoubleQuotedValues: false);

    /// <summary>Keeps the DEFAULT grammar's quoted-value role separate from ordinary identifier folding.</summary>
    private static string Normalize(
        string value,
        bool preserveDoubleQuotedValues
    )
    {
        var span = value
            .AsSpan()
            .Trim(SqliteSqlIdentifierScanner.WhitespaceCharacters);

        while (HasSingleOuterParentheses(span))
        {
            span = span[1..^1]
                .Trim(SqliteSqlIdentifierScanner.WhitespaceCharacters);
        }

        var builder = new StringBuilder(span.Length);
        var pendingSpace = false;
        var declaredIdentifier = false;
        var depth = 0;
        var castTypeDepth = -1;

        // WHY: Track SQL offsets so quote escapes and token boundaries remain intact.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < span.Length; index++)
        {
            if (preserveDoubleQuotedValues)
            {
                var nextPosition = index;
                SqliteSqlIdentifierScanner.SkipTrivia(span, ref nextPosition);
                if (nextPosition != index)
                {
                    pendingSpace = builder.Length > 0;
                    index = nextPosition - 1;
                    continue;
                }

                if (SqliteSqlIdentifierScanner.TryReadBareWord(span, ref nextPosition))
                {
                    var word = span[index..nextPosition];
                    AppendPendingSpace(builder, ref pendingSpace, span[index]);
                    foreach (var letter in word)
                    {
                        builder.Append(ToUpperAscii(letter));
                    }

                    declaredIdentifier = word.Equals("COLLATE", StringComparison.OrdinalIgnoreCase);
                    if (word.Equals("AS", StringComparison.OrdinalIgnoreCase))
                    {
                        castTypeDepth = depth;
                    }

                    index = nextPosition - 1;
                    continue;
                }
            }

            var character = span[index];
            switch (character)
            {
                case '\'':
                    AppendPendingSpace(builder, ref pendingSpace, character);
                    index = AppendQuoted(span, index, '\'', builder, preserveCase: true);
                    declaredIdentifier = false;
                    continue;
                case '"':
                    AppendPendingSpace(builder, ref pendingSpace, character);
                    var quotedStart = builder.Length;
                    index = AppendQuoted(span, index, character, builder, preserveCase: preserveDoubleQuotedValues);
                    if (preserveDoubleQuotedValues)
                    {
                        var nextPosition = index + 1;
                        SqliteSqlIdentifierScanner.SkipTrivia(span, ref nextPosition);

                        // WHY: DEFAULT has no column references. A double-quoted token
                        // is a value unless function-call, CAST-type or COLLATE grammar
                        // proves an identifier role; only those roles allow case folding.
                        if (declaredIdentifier
                            || castTypeDepth == depth
                            || (nextPosition < span.Length && span[nextPosition] == '('))
                        {
                            // WHY: Fold only the classified identifier token in the owned output buffer.
                            // ReSharper disable once ForCanBeConvertedToForeach
                            // ReSharper disable once LoopCanBeConvertedToQuery
                            for (var output = quotedStart; output < builder.Length; output++)
                            {
                                builder[output] = ToUpperAscii(builder[output]);
                            }
                        }
                    }

                    declaredIdentifier = false;
                    continue;
                case '`':
                    AppendPendingSpace(builder, ref pendingSpace, character);
                    index = AppendQuoted(span, index, character, builder, preserveCase: false);
                    declaredIdentifier = false;
                    continue;
                case '[':
                    AppendPendingSpace(builder, ref pendingSpace, character);
                    index = AppendBracketedIdentifier(span, index, builder);
                    declaredIdentifier = false;
                    continue;
            }

            if (preserveDoubleQuotedValues)
            {
                declaredIdentifier = false;
                if (character == '(')
                {
                    depth++;
                }
                else if (character == ')')
                {
                    if (castTypeDepth == depth)
                    {
                        castTypeDepth = -1;
                    }

                    depth--;
                }
            }

            if (SqliteSqlIdentifierScanner.IsWhitespace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            AppendPendingSpace(builder, ref pendingSpace, character);
            builder.Append(ToUpperAscii(character));
        }

        return builder.ToString();
    }

    private static void AppendPendingSpace(
        StringBuilder builder,
        ref bool pendingSpace,
        char character
    )
    {
        if (pendingSpace
            && character is not ')' and not ','
            && builder[^1] != '(')
        {
            builder.Append(' ');
        }

        pendingSpace = false;
    }

    private static int AppendQuoted(
        ReadOnlySpan<char> value,
        int start,
        char quote,
        StringBuilder builder,
        bool preserveCase
    )
    {
        builder.Append(quote);

        // WHY: Consume escaped delimiters together while retaining the quoted token's exact source offsets.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = start + 1; index < value.Length; index++)
        {
            var character = value[index];
            builder.Append(
                preserveCase
                    ? character
                    : ToUpperAscii(character));

            if (character != quote)
            {
                continue;
            }

            if (index + 1 < value.Length
                && value[index + 1] == quote)
            {
                builder.Append(quote);
                index++;
                continue;
            }

            return index;
        }

        return value.Length - 1;
    }

    private static int AppendBracketedIdentifier(
        ReadOnlySpan<char> value,
        int start,
        StringBuilder builder
    )
    {
        builder.Append('[');

        // WHY: Consume escaped delimiters together while retaining the quoted token's exact source offsets.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = start + 1; index < value.Length; index++)
        {
            var character = value[index];
            builder.Append(ToUpperAscii(character));
            if (character == ']')
            {
                return index;
            }
        }

        return value.Length - 1;
    }

    private static bool HasSingleOuterParentheses(
        ReadOnlySpan<char> value
    )
    {
        if (value.Length < 2
            || value[0] != '('
            || value[^1] != ')')
        {
            return false;
        }

        var depth = 0;
        var closingQuote = '\0';

        // WHY: Track quote escapes and parenthesis depth at exact UTF-16 offsets before slicing SQL.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (closingQuote != '\0')
            {
                if (character == closingQuote)
                {
                    if (index + 1 < value.Length
                        && value[index + 1] == closingQuote)
                    {
                        index++;
                    }
                    else
                    {
                        closingQuote = '\0';
                    }
                }

                continue;
            }

            switch (character)
            {
                case '\'' or '"' or '`':
                    closingQuote = character;
                    continue;
                case '[':
                    closingQuote = ']';
                    continue;
                case '(':
                    depth++;
                    break;
                case ')'
                    when --depth == 0
                    && index != value.Length - 1:
                    return false;
            }
        }

        return depth == 0 && closingQuote == '\0';
    }

    private static char ToUpperAscii(
        char value
    ) => value is >= 'a' and <= 'z'
        ? (char)(value - ('a' - 'A'))
        : value;
}
