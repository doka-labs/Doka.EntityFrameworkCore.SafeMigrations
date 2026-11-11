namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>
/// Reads SQLite identifiers and keywords while honoring quoted text and SQL trivia.
/// </summary>
internal static class SqliteSqlIdentifierScanner
{
    /// <summary>Contains SQLite's five SQL whitespace characters, excluding high-byte identifier characters.</summary>
    internal const string WhitespaceCharacters = " \t\n\f\r";

    /// <summary>Determines whether a character is native SQL whitespace rather than part of an identifier.</summary>
    internal static bool IsWhitespace(
        char value
    ) => value is ' ' or '\t' or '\n' or '\f' or '\r';

    /// <summary>
    /// Detects unquoted native identifier characters a generic Unicode-whitespace parser would erase.
    /// </summary>
    /// <param name="sql">The complete native SQLite expression source.</param>
    /// <returns>Whether bounded generic parsing cannot certify the original native identifier boundaries.</returns>
    internal static bool HasNonNativeWhitespace(
        string sql
    )
    {
        var position = 0;
        while (position < sql.Length)
        {
            SkipTrivia(sql, ref position);
            if (position >= sql.Length)
            {
                break;
            }

            var character = sql[position];
            if (character is '\'' or '"' or '`' or '[')
            {
                SkipQuoted(
                    sql,
                    ref position,
                    character,
                    character == '['
                        ? ']'
                        : character);

                continue;
            }

            if (character >= '\u0080'
                && char.IsWhiteSpace(character))
            {
                return true;
            }

            position++;
        }

        return false;
    }

    /// <summary>Determines whether SQL contains an unquoted keyword token.</summary>
    /// <param name="sql">The SQLite SQL text to inspect.</param>
    /// <param name="keyword">The keyword to find.</param>
    /// <returns><see langword="true" /> when the keyword occurs as a token.</returns>
    public static bool ContainsKeyword(
        string sql,
        string keyword
    ) => IndexOfKeyword(sql, keyword) >= 0;

    /// <summary>Determines whether SQL contains two adjacent keyword tokens.</summary>
    /// <param name="sql">The SQLite SQL text to inspect.</param>
    /// <param name="first">The first keyword.</param>
    /// <param name="second">The second keyword.</param>
    /// <returns><see langword="true" /> when the keyword sequence occurs.</returns>
    public static bool ContainsKeywordSequence(
        string sql,
        string first,
        string second
    ) => IndexOfKeywordSequence(sql, first, second) >= 0;

    /// <summary>Finds two adjacent keyword tokens outside literals, identifiers, and comments.</summary>
    /// <param name="sql">The SQLite SQL text to inspect.</param>
    /// <param name="first">The first keyword.</param>
    /// <param name="second">The second keyword.</param>
    /// <returns>The zero-based start of the first keyword, or <c>-1</c>.</returns>
    public static int IndexOfKeywordSequence(
        string sql,
        string first,
        string second
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var position = 0;
        while (TryReadNextBareWord(sql, ref position, out var start, out var length))
        {
            var token = sql.AsSpan(start, length);
            if (!token.Equals(first, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var secondPosition = position;
            SkipTrivia(sql, ref secondPosition);
            var secondStart = secondPosition;
            if (TryReadBareWord(sql, ref secondPosition)
                && sql
                    .AsSpan(secondStart, secondPosition - secondStart)
                    .Equals(second, StringComparison.OrdinalIgnoreCase))
            {
                return start;
            }
        }

        return -1;
    }

    /// <summary>Finds a keyword token outside literals, identifiers, and comments.</summary>
    /// <param name="sql">The SQLite SQL text to inspect.</param>
    /// <param name="keyword">The keyword to find.</param>
    /// <param name="startIndex">The zero-based search start.</param>
    /// <returns>The zero-based keyword start, or <c>-1</c>.</returns>
    public static int IndexOfKeyword(
        string sql,
        string keyword,
        int startIndex = 0
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(keyword);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);

        var position = Math.Min(startIndex, sql.Length);
        while (TryReadNextBareWord(sql, ref position, out var start, out var length))
        {
            if (sql
                .AsSpan(start, length)
                .Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return start;
            }
        }

        return -1;
    }

    /// <summary>Finds a character after a keyword and any intervening SQL trivia.</summary>
    /// <param name="sql">The SQLite SQL text to inspect.</param>
    /// <param name="keyword">The keyword that must precede the character.</param>
    /// <param name="expected">The expected character.</param>
    /// <returns>The zero-based character position, or <c>-1</c>.</returns>
    public static int IndexOfCharacterFollowingKeyword(
        string sql,
        string keyword,
        char expected
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(keyword);

        var position = 0;
        while (TryReadNextBareWord(sql, ref position, out var start, out var length))
        {
            if (!sql
                .AsSpan(start, length)
                .Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expectedPosition = position;
            SkipTrivia(sql, ref expectedPosition);
            if (expectedPosition < sql.Length
                && sql[expectedPosition] == expected)
            {
                return expectedPosition;
            }
        }

        return -1;
    }

    /// <summary>Reads the first identifier that follows a keyword token.</summary>
    /// <param name="sql">The SQLite SQL text to inspect.</param>
    /// <param name="keyword">The keyword that precedes the identifier.</param>
    /// <returns>The decoded identifier, or <see langword="null" />.</returns>
    public static string? ReadIdentifierFollowingKeyword(
        string sql,
        string keyword
    ) => ReadIdentifierFollowingKeyword(sql, keyword, returnLast: false);

    /// <summary>Reads the last identifier that follows a repeated keyword token.</summary>
    /// <param name="sql">The SQLite SQL text to inspect.</param>
    /// <param name="keyword">The repeated keyword that precedes each candidate.</param>
    /// <returns>The last decoded identifier, or <see langword="null" />.</returns>
    public static string? ReadLastIdentifierFollowingKeyword(
        string sql,
        string keyword
    ) => ReadIdentifierFollowingKeyword(sql, keyword, returnLast: true);

    /// <summary>Reads the last declared identifier outside parenthesized expressions.</summary>
    /// <param name="sql">The native declaration, including its expression source.</param>
    /// <param name="keyword">The declaration keyword, such as COLLATE.</param>
    /// <returns>The last outer identifier, or null when the declaration omits it.</returns>
    internal static string? ReadTopLevelIdentifierFollowingKeyword(
        string sql,
        string keyword
    ) => ReadIdentifierFollowingKeyword(sql, keyword, returnLast: true, topLevelOnly: true);

    /// <summary>Finds the real key-list delimiter after an owner identifier, never inside a quoted name.</summary>
    /// <param name="sql">The native CREATE INDEX statement.</param>
    /// <param name="keyword">The owner keyword, ON for an index.</param>
    /// <returns>The owner key-list opening parenthesis, or -1 for an unsupported header.</returns>
    internal static int IndexOfOwnerKeyList(
        string sql,
        string keyword
    )
    {
        var keywordPosition = IndexOfKeyword(sql, keyword);
        if (keywordPosition < 0)
        {
            return -1;
        }

        var position = keywordPosition + keyword.Length;
        SkipTrivia(sql, ref position);
        if (!TryReadIdentifier(sql, ref position, out _, allowSingleQuoted: true))
        {
            return -1;
        }

        SkipTrivia(sql, ref position);

        return position < sql.Length && sql[position] == '('
            ? position
            : -1;
    }

    private static string? ReadIdentifierFollowingKeyword(
        string sql,
        string keyword,
        bool returnLast,
        bool topLevelOnly = false
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(keyword);

        var position = 0;
        var depth = 0;
        string? result = null;
        while (position < sql.Length)
        {
            SkipTrivia(sql, ref position);
            if (position >= sql.Length)
            {
                break;
            }

            if (sql[position] == '\'')
            {
                SkipQuoted(sql, ref position, '\'', '\'');
                continue;
            }

            if (sql[position] is '"' or '`' or '[')
            {
                _ = TryReadIdentifier(sql, ref position, out _);
                continue;
            }

            if (sql[position] == '(')
            {
                depth++;
                position++;
                continue;
            }

            if (sql[position] == ')')
            {
                depth--;
                position++;
                continue;
            }

            var start = position;
            if (!TryReadBareWord(sql, ref position))
            {
                position++;
                continue;
            }

            if ((topLevelOnly && depth != 0)
                || !sql
                    .AsSpan(start, position - start)
                    .Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            SkipTrivia(sql, ref position);
            if (!TryReadIdentifier(sql, ref position, out var identifier))
            {
                continue;
            }

            result = identifier;
            if (!returnLast)
            {
                return result;
            }
        }

        return result;
    }

    /// <summary>Determines whether SQL references an identifier token.</summary>
    /// <param name="sql">The SQLite SQL text to inspect.</param>
    /// <param name="identifier">The decoded identifier to find.</param>
    /// <returns><see langword="true" /> when the identifier is referenced.</returns>
    public static bool ReferencesIdentifier(
        string sql,
        string identifier
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(identifier);

        var position = 0;
        while (position < sql.Length)
        {
            SkipTrivia(sql, ref position);
            if (position >= sql.Length)
            {
                break;
            }

            if (TryReadIdentifier(sql, ref position, out var candidate))
            {
                if (SqliteIdentifierComparer.Instance.Equals(candidate, identifier))
                {
                    return true;
                }

                // WHY: The token reader already advanced to the next byte.
                // Skipping that byte can discard a comment or identifier quote
                // and hide a real dependency in otherwise valid native SQL.
                continue;
            }

            if (position < sql.Length
                && sql[position] == '\'')
            {
                SkipQuoted(sql, ref position, '\'', '\'');
            }
            else
            {
                position++;
            }
        }

        return false;
    }

    private static bool TryReadIdentifier(
        string sql,
        ref int position,
        [NotNullWhen(true)] out string? identifier,
        bool allowSingleQuoted = false
    )
    {
        identifier = null;
        if (position >= sql.Length)
        {
            return false;
        }

        var current = sql[position];
        if (current is '"' or '`'
            || (allowSingleQuoted && current == '\''))
        {
            identifier = ReadDelimitedIdentifier(sql, ref position, current, current);

            return true;
        }

        if (current == '[')
        {
            identifier = ReadDelimitedIdentifier(sql, ref position, '[', ']');

            return true;
        }

        if (!IsIdentifierStart(current))
        {
            return false;
        }

        var start = position++;
        while (position < sql.Length
            && IsIdentifierPart(sql[position]))
        {
            position++;
        }

        identifier = sql[start..position];

        return true;
    }

    /// <summary>Advances across one native unquoted identifier/keyword token without allocating a string.</summary>
    internal static bool TryReadBareWord(
        ReadOnlySpan<char> sql,
        ref int position
    )
    {
        if (position >= sql.Length
            || !IsIdentifierStart(sql[position]))
        {
            return false;
        }

        position++;
        while (position < sql.Length
            && IsIdentifierPart(sql[position]))
        {
            position++;
        }

        return true;
    }

    private static bool TryReadNextBareWord(
        string sql,
        ref int position,
        out int start,
        out int length
    )
    {
        while (position < sql.Length)
        {
            SkipTrivia(sql, ref position);
            if (position >= sql.Length)
            {
                break;
            }

            if (sql[position] == '\'')
            {
                SkipQuoted(sql, ref position, '\'', '\'');
                continue;
            }

            if (sql[position] is '"' or '`' or '[')
            {
                _ = TryReadIdentifier(sql, ref position, out _);
                continue;
            }

            start = position;
            if (TryReadBareWord(sql, ref position))
            {
                length = position - start;

                return true;
            }

            position++;
        }

        start = -1;
        length = 0;

        return false;
    }

    private static string ReadDelimitedIdentifier(
        string sql,
        ref int position,
        char open,
        char close
    )
    {
        var result = new StringBuilder();
        position++;
        while (position < sql.Length)
        {
            var current = sql[position++];
            if (current != close)
            {
                result.Append(current);
                continue;
            }

            if (position < sql.Length
                && sql[position] == close
                && open != '[')
            {
                result.Append(close);
                position++;
                continue;
            }

            break;
        }

        return result.ToString();
    }

    /// <summary>Advances across native SQL whitespace and comments while leaving quoted payloads untouched.</summary>
    internal static void SkipTrivia(
        ReadOnlySpan<char> sql,
        ref int position
    )
    {
        // WHY: SQLite treats both comment forms as whitespace, so grammar
        // tokens may legally be separated by comments.
        while (position < sql.Length)
        {
            // WHY: SQLite classifies every high-byte character as alphabetic.
            // Unicode whitespace such as NBSP may therefore be a real operand.
            // WHY: SQLite also emits a standalone BOM as TK_SPACE. It is
            // trivia only at token boundaries; embedded BOM bytes remain part
            // of an identifier and must not enter general trimming policies.
            if (IsWhitespace(sql[position])
                || sql[position] == '\uFEFF')
            {
                position++;
                continue;
            }

            if (position + 1 < sql.Length
                && sql[position] == '-'
                && sql[position + 1] == '-')
            {
                position += 2;
                while (position < sql.Length
                    && sql[position] != '\n')
                {
                    position++;
                }

                continue;
            }

            if (position + 1 < sql.Length
                && sql[position] == '/'
                && sql[position + 1] == '*')
            {
                position += 2;
                while (position + 1 < sql.Length
                    && !(sql[position] == '*' && sql[position + 1] == '/'))
                {
                    position++;
                }

                position = Math.Min(sql.Length, position + 2);
                continue;
            }

            break;
        }
    }

    private static void SkipQuoted(
        string sql,
        ref int position,
        char open,
        char close
    )
    {
        position++;
        while (position < sql.Length)
        {
            if (sql[position++] != close)
            {
                continue;
            }

            if (position < sql.Length
                && sql[position] == close
                && open == close)
            {
                position++;
                continue;
            }

            return;
        }
    }

    // WHY: SQLite treats every character above U+007F as alphabetic for an
    // unquoted identifier, including symbols that char.IsLetter rejects.
    private static bool IsIdentifierStart(
        char value
    ) => value is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '\u0080';

    private static bool IsIdentifierPart(
        char value
    ) => IsIdentifierStart(value) || value == '$' || value is >= '0' and <= '9';
}
