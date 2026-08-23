namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal static class MySqlExpressionCanonicalizer
{
    private readonly record struct CatalogToken(
        string Text,
        bool IsTopLevelBooleanOperator = false
    );

    public static IReadOnlyList<string> BuildCatalogDisplayCandidates(
        string quotedExpression,
        bool includeMySqlEncodedDisplay
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quotedExpression);

        var normalizedExpression = RemoveBalancedOuterParentheses(quotedExpression.Trim());
        var candidates = BuildCandidates(TokenizeCatalogDisplay(normalizedExpression, encodeForMySqlCatalog: false));
        if (includeMySqlEncodedDisplay)
        {
            candidates.AddRange(
                BuildCandidates(TokenizeCatalogDisplay(normalizedExpression, encodeForMySqlCatalog: true)));
        }

        return candidates
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static List<string> BuildCandidates(
        IReadOnlyList<CatalogToken> tokens
    )
    {
        var flat = string
            .Concat(tokens.Select(static token => token.Text))
            .Trim();

        // MySQL and MariaDB may add outer or per-term parentheses when they
        // expose expressions through INFORMATION_SCHEMA.
        var candidates = new List<string>
        {
            flat,
            $"({flat})",
        };

        if (tokens.Any(static token => token.IsTopLevelBooleanOperator))
        {
            var unwrapped = new StringBuilder();
            var builder = new StringBuilder("((");
            var term = new StringBuilder();
            foreach (var token in tokens)
            {
                if (!token.IsTopLevelBooleanOperator)
                {
                    term.Append(token.Text);
                    continue;
                }

                if (unwrapped.Length > 0)
                {
                    unwrapped.Append(' ');
                }

                unwrapped
                    .Append(
                        RemoveBalancedOuterParentheses(
                            term
                                .ToString()
                                .Trim()))
                    .Append(' ')
                    .Append(token.Text.Trim());

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

            unwrapped
                .Append(' ')
                .Append(
                    RemoveBalancedOuterParentheses(
                        term
                            .ToString()
                            .Trim()));

            builder
                .Append(
                    term
                        .ToString()
                        .Trim())
                .Append("))");

            candidates.Add(unwrapped.ToString());
            candidates.Add(builder.ToString());
        }

        return candidates;
    }

    private static List<CatalogToken> TokenizeCatalogDisplay(
        string expression,
        bool encodeForMySqlCatalog
    )
    {
        var tokens = new List<CatalogToken>();
        var depth = 0;

        // Boolean terms are regrouped only at depth zero; nested predicates
        // must retain their original parenthesis structure.
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
                var token = expression[index..end];
                tokens.Add(new CatalogToken(encodeForMySqlCatalog ? RenderMySqlCatalogIdentifier(token) : token));
                index = end;
                continue;
            }

            if (current is '\'' or '"')
            {
                var end = FindQuotedEnd(expression, index, current);
                var token = expression[index..end];
                tokens.Add(new CatalogToken(encodeForMySqlCatalog ? RenderMySqlCatalogString(token, current) : token));
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

    private static string RenderMySqlCatalogIdentifier(
        string token
    )
    {
        // MySQL 8 can expose UTF-8 expression tokens through a single-byte
        // INFORMATION_SCHEMA display. This encoded form is only an additional
        // MySQL catalog candidate; the canonical token remains authoritative,
        // and MariaDB does not opt into this candidate path.
        var escaped = token[1..^1]
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

        return $"`{Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(escaped))}`";
    }

    private static string RenderMySqlCatalogString(
        string token,
        char quote
    )
    {
        // Keep literals on the same catalog-display path as quoted identifiers
        // so one server-rendered expression is compared under one encoding rule.
        var inner = token[1..^1]
            .Replace(new string(quote, 2), quote.ToString(), StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

        var rendered = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(inner));

        return $@"_utf8mb4\'{rendered}\'";
    }

    private static string RemoveBalancedOuterParentheses(
        string expression
    )
    {
        while (expression.Length >= 2
               && expression[0] == '('
               && expression[^1] == ')'
               && OuterParenthesesEncloseExpression(expression))
        {
            expression = expression[1..^1]
                .Trim();
        }

        return expression;
    }

    private static bool OuterParenthesesEncloseExpression(
        string expression
    )
    {
        var depth = 0;
        var quote = '\0';
        for (var index = 0; index < expression.Length; index++)
        {
            var current = expression[index];
            if (quote != '\0')
            {
                if (current == '\\'
                    && quote != '`'
                    && index + 1 < expression.Length)
                {
                    index++;
                    continue;
                }

                if (current == quote)
                {
                    if (index + 1 < expression.Length
                        && expression[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                quote = current;
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;
                if (depth == 0
                    && index != expression.Length - 1)
                {
                    return false;
                }
            }

            if (depth < 0)
            {
                return false;
            }
        }

        return depth == 0 && quote == '\0';
    }
}
