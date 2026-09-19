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

    /// <summary>Produces the canonical comparison form of a SQLite expression.</summary>
    public static string Normalize(
        string value
    )
    {
        var span = value.AsSpan().Trim();
        while (HasSingleOuterParentheses(span))
        {
            span = span[1..^1].Trim();
        }

        var builder = new StringBuilder(span.Length);
        var pendingSpace = false;
        for (var index = 0; index < span.Length; index++)
        {
            var character = span[index];
            switch (character)
            {
                case '\'':
                    AppendPendingSpace(builder, ref pendingSpace, character);
                    index = AppendQuoted(span, index, '\'', builder, preserveCase: true);
                    continue;
                case '"' or '`':
                    AppendPendingSpace(builder, ref pendingSpace, character);
                    index = AppendQuoted(span, index, character, builder, preserveCase: false);
                    continue;
                case '[':
                    AppendPendingSpace(builder, ref pendingSpace, character);
                    index = AppendBracketedIdentifier(span, index, builder);
                    continue;
            }

            if (char.IsWhiteSpace(character))
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
        for (var index = start + 1; index < value.Length; index++)
        {
            var character = value[index];
            builder.Append(preserveCase ? character : ToUpperAscii(character));
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
    ) => value is >= 'a' and <= 'z' ? (char)(value - ('a' - 'A')) : value;
}
