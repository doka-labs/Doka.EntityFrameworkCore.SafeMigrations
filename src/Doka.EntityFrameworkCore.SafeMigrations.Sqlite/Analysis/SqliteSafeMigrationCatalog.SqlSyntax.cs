namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal static partial class SqliteSafeMigrationCatalog
{
    private static string[] ReadTableClauses(
        string tableSql
    )
    {
        var open = FindFirstUnquotedParenthesis(tableSql);

        return open < 0
            ? []
            : SplitTopLevel(tableSql[(open + 1)..])

                // WHY: Preserve explicit fluent materialization for the ordered snapshot.
                // ReSharper disable once UseCollectionExpression
                .ToArray();
    }

    private static int FindFirstUnquotedParenthesis(
        string sql
    )
    {
        var quote = '\0';

        // WHY: Respect escaped quotes before selecting a structural parenthesis offset.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (quote != ']'
                        && index + 1 < sql.Length
                        && sql[index + 1] == quote)
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

            // WHY: Quoting updates state, while the first structural parenthesis exits immediately.
            // ReSharper disable once ConvertIfStatementToSwitchStatement
            if (character is '\'' or '\"' or '`' or '[')
            {
                quote = character == '['
                    ? ']'
                    : character;
            }
            else if (character == '(')
            {
                return index;
            }
        }

        return -1;
    }

    private static string ReadBalancedParentheses(
        string sql,
        int openParenthesis
    )
    {
        var depth = 0;
        var quote = '\0';

        // WHY: Track quote escapes and parenthesis depth at exact UTF-16 offsets before slicing SQL.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = openParenthesis; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (quote != ']'
                        && index + 1 < sql.Length
                        && sql[index + 1] == quote)
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

            switch (character)
            {
                case '\'' or '\"' or '`':
                    quote = character;
                    break;
                case '[':
                    quote = ']';
                    break;
                case '(':
                    depth++;
                    break;
                case ')' when --depth == 0:
                    return sql
                        .AsSpan(openParenthesis + 1, index - openParenthesis - 1)
                        .Trim(SqliteSqlIdentifierScanner.WhitespaceCharacters)
                        .ToString();
            }
        }

        throw new InvalidOperationException("SQLite catalog SQL contains an unbalanced expression.");
    }

    private static int FindBalancedParenthesisEnd(
        string sql,
        int openParenthesis
    )
    {
        var depth = 0;
        var quote = '\0';

        // WHY: Track quote escapes and parenthesis depth at exact UTF-16 offsets before slicing SQL.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = openParenthesis; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (quote != ']'
                        && index + 1 < sql.Length
                        && sql[index + 1] == quote)
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

            switch (character)
            {
                case '\'' or '\"' or '`':
                    quote = character;
                    break;
                case '[':
                    quote = ']';
                    break;
                case '(':
                    depth++;
                    break;
                case ')' when --depth == 0:
                    return index;
            }
        }

        throw new InvalidOperationException("SQLite catalog SQL contains an unbalanced expression.");
    }

    private static Dictionary<string, string> ReadColumnClauses(
        string[] tableClauses
    )
    {
        var result = new Dictionary<string, string>(s_identifierComparer);
        foreach (var clause in tableClauses)
        {
            var identifier = ReadLeadingIdentifier(clause);
            if (identifier is not null)
            {
                result.TryAdd(identifier, clause);
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitTopLevel(
        string sql
    )
    {
        var start = 0;
        var depth = 0;
        var quote = '\0';

        // WHY: Track quote escapes and parenthesis depth at exact UTF-16 offsets before slicing SQL.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quote != '\0')
            {
                if (character == quote
                    && (quote == ']' || index + 1 >= sql.Length || sql[index + 1] != quote))
                {
                    quote = '\0';
                }
                else if (character == quote
                    && quote != ']')
                {
                    index++;
                }

                continue;
            }

            switch (character)
            {
                case '\'' or '\"' or '`':
                    quote = character;
                    break;
                case '[':
                    quote = ']';
                    break;
                case '(':
                    depth++;
                    break;
                case ')' when depth == 0:
                    yield return sql
                        .AsSpan(start, index - start)
                        .Trim(SqliteSqlIdentifierScanner.WhitespaceCharacters)
                        .ToString();

                    yield break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return sql
                        .AsSpan(start, index - start)
                        .Trim(SqliteSqlIdentifierScanner.WhitespaceCharacters)
                        .ToString();

                    start = index + 1;
                    break;
            }
        }

        if (start < sql.Length)
        {
            yield return sql
                .AsSpan(start)
                .Trim(SqliteSqlIdentifierScanner.WhitespaceCharacters)
                .ToString();
        }
    }

    private static string RemoveComments(
        string sql
    )
    {
        // WHY: Most catalog definitions contain no comment markers. Reuse their
        // immutable SQL instead of copying it; possible markers still use the quote-aware scanner.
        if (!sql.Contains("--", StringComparison.Ordinal)
            && !sql.Contains("/*", StringComparison.Ordinal))
        {
            return sql;
        }

        var builder = new StringBuilder(sql.Length);
        var quote = '\0';

        // WHY: Keep quote-aware lookahead aligned while removing comment-delimiter ranges.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quote != '\0')
            {
                builder.Append(character);
                if (character == quote)
                {
                    if (quote != ']'
                        && index + 1 < sql.Length
                        && sql[index + 1] == quote)
                    {
                        builder.Append(sql[++index]);
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            // WHY: Quoted bytes bypass comment recognition entirely; retain that early continuation explicitly.
            // ReSharper disable once ConvertIfStatementToSwitchStatement
            if (character is '\'' or '\"' or '`' or '[')
            {
                quote = character == '['
                    ? ']'
                    : character;

                builder.Append(character);
                continue;
            }

            if (character == '-'
                && index + 1 < sql.Length
                && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length
                    && sql[index] != '\n')
                {
                    index++;
                }

                builder.Append(' ');
                if (index < sql.Length)
                {
                    builder.Append(sql[index]);
                }

                continue;
            }

            if (character == '/'
                && index + 1 < sql.Length
                && sql[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < sql.Length
                    && (sql[index] != '*' || sql[index + 1] != '/'))
                {
                    index++;
                }

                index = Math.Min(index + 1, sql.Length - 1);
                builder.Append(' ');
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
