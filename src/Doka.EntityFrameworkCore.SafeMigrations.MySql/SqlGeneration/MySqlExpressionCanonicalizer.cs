namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal static class MySqlExpressionCanonicalizer
{
    private readonly record struct CatalogToken(
        string Text,
        bool IsTopLevelBooleanOperator = false
    );

    public static string QuoteIdentifiers(
        string expression,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        var builder = new StringBuilder(expression.Length + 16);
        for (var index = 0; index < expression.Length;)
        {
            var current = expression[index];
            if (current is '\'' or '"' or '`')
            {
                index = CopyQuoted(expression, index, builder, current);
                continue;
            }

            if (current == '_'
                || char.IsLetter(current))
            {
                var start = index++;
                while (index < expression.Length
                       && (expression[index] == '_'
                           || expression[index] == '$'
                           || char.IsLetterOrDigit(expression[index])))
                {
                    index++;
                }

                var token = expression[start..index];
                var next = index;
                while (next < expression.Length
                       && char.IsWhiteSpace(expression[next]))
                {
                    next++;
                }

                if (IsKeyword(token)
                    || next < expression.Length && expression[next] == '(')
                {
                    builder.Append(token);
                }
                else
                {
                    builder.Append(sqlGenerationHelper.DelimitIdentifier(token));
                }

                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    public static IReadOnlyList<string> BuildCatalogDisplayCandidates(
        string quotedExpression
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quotedExpression);

        var tokens = TokenizeCatalogDisplay(quotedExpression);
        var flat = string
            .Concat(tokens.Select(static token => token.Text))
            .Trim();

        var candidates = new List<string>
        {
            flat,
            $"({flat})",
        };

        if (tokens.Any(static token => token.IsTopLevelBooleanOperator))
        {
            var builder = new StringBuilder("((");
            var term = new StringBuilder();
            foreach (var token in tokens)
            {
                if (!token.IsTopLevelBooleanOperator)
                {
                    term.Append(token.Text);
                    continue;
                }

                builder
                    .Append(
                        term
                            .ToString()
                            .Trim())
                    .Append(") ")
                    .Append(token.Text.Trim())
                    .Append(" (");

                term.Clear();
            }

            builder
                .Append(
                    term
                        .ToString()
                        .Trim())
                .Append("))");

            candidates.Add(builder.ToString());
        }

        return candidates
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static List<CatalogToken> TokenizeCatalogDisplay(
        string expression
    )
    {
        var tokens = new List<CatalogToken>();
        var depth = 0;
        for (var index = 0; index < expression.Length;)
        {
            var current = expression[index];
            if (char.IsWhiteSpace(current))
            {
                while (index < expression.Length
                       && char.IsWhiteSpace(expression[index]))
                {
                    index++;
                }

                tokens.Add(new CatalogToken(" "));
                continue;
            }

            if (current == '`')
            {
                var end = FindQuotedEnd(expression, index, current);
                tokens.Add(new CatalogToken(RenderCatalogIdentifier(expression[index..end])));
                index = end;
                continue;
            }

            if (current is '\'' or '"')
            {
                var end = FindQuotedEnd(expression, index, current);
                tokens.Add(new CatalogToken(RenderCatalogString(expression[index..end], current)));
                index = end;
                continue;
            }

            if (current == '(')
            {
                depth++;
                tokens.Add(new CatalogToken("("));
                index++;
                continue;
            }

            if (current == ')')
            {
                depth--;
                tokens.Add(new CatalogToken(")"));
                index++;
                continue;
            }

            if (current == '_'
                || char.IsLetter(current))
            {
                var start = index++;
                while (index < expression.Length
                       && (expression[index] == '_'
                           || expression[index] == '$'
                           || char.IsLetterOrDigit(expression[index])))
                {
                    index++;
                }

                var word = expression[start..index]
                    .ToLowerInvariant();
                tokens.Add(new CatalogToken(word, depth == 0 && word is "and" or "or"));
                continue;
            }

            tokens.Add(new CatalogToken(current.ToString()));
            index++;
        }

        return tokens;
    }

    private static int FindQuotedEnd(
        string expression,
        int index,
        char quote
    )
    {
        index++;
        while (index < expression.Length)
        {
            var current = expression[index++];
            if (current == '\\'
                && quote != '`'
                && index < expression.Length)
            {
                index++;
                continue;
            }

            if (current != quote)
            {
                continue;
            }

            if (index < expression.Length
                && expression[index] == quote)
            {
                index++;
                continue;
            }

            return index;
        }

        throw new ArgumentException("The SQL expression contains an unterminated quoted token.", nameof(expression));
    }

    private static string RenderCatalogIdentifier(
        string token
    )
    {
        var escaped = token[1..^1]
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

        var rendered = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(escaped));

        return $"`{rendered}`";
    }

    private static string RenderCatalogString(
        string token,
        char quote
    )
    {
        var inner = token[1..^1]
            .Replace(new string(quote, 2), quote.ToString(), StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

        var rendered = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(inner));

        return $@"_utf8mb4\'{rendered}\'";
    }

    private static int CopyQuoted(
        string expression,
        int index,
        StringBuilder builder,
        char quote
    )
    {
        builder.Append(quote);
        index++;
        while (index < expression.Length)
        {
            var current = expression[index];
            builder.Append(current);
            index++;
            if (current == '\\'
                && quote != '`'
                && index < expression.Length)
            {
                builder.Append(expression[index]);
                index++;
            }
            else if (current == quote)
            {
                if (index < expression.Length
                    && expression[index] == quote)
                {
                    builder.Append(expression[index]);
                    index++;
                }
                else
                {
                    return index;
                }
            }
        }

        throw new ArgumentException("The SQL expression contains an unterminated quoted token.", nameof(expression));
    }

    private static bool IsKeyword(
        string token
    ) => token.ToUpperInvariant() is "ALL"
        or "AND"
        or "ANY"
        or "AS"
        or "ASC"
        or "BETWEEN"
        or "BINARY"
        or "BY"
        or "CASE"
        or "CAST"
        or "COLLATE"
        or "DESC"
        or "DISTINCT"
        or "ELSE"
        or "END"
        or "ESCAPE"
        or "EXISTS"
        or "FALSE"
        or "FROM"
        or "IN"
        or "INTERVAL"
        or "IS"
        or "LIKE"
        or "MOD"
        or "NOT"
        or "NULL"
        or "OR"
        or "REGEXP"
        or "RLIKE"
        or "THEN"
        or "TRUE"
        or "UNKNOWN"
        or "WHEN"
        or "WHERE"
        or "XOR";
}
