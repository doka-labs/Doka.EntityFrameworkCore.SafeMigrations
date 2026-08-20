namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal static class MySqlPreparedStatementText
{
    public static PreparedStatementText NormalizeProviderCommand(
        string commandText,
        bool hasBackslashDdlComment
    )
    {
        if (!hasBackslashDdlComment)
        {
            var sql = NormalizeSingleStatement(commandText);
            return new PreparedStatementText(sql, sql, IsSqlModeSensitive: false);
        }

        var statement = ExtractDdlCommentProtectedStatement(commandText);

        return new PreparedStatementText(
            statement,
            EscapeBackslashesInQuotedLiterals(statement),
            IsSqlModeSensitive: true);
    }

    public static string NormalizeSingleStatement(
        string commandText
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        var end = commandText.Length - 1;
        while (end >= 0
               && char.IsWhiteSpace(commandText[end]))
        {
            end--;
        }

        if (end >= 0
            && commandText[end] == ';')
        {
            end--;
        }

        while (end >= 0
               && char.IsWhiteSpace(commandText[end]))
        {
            end--;
        }

        if (end < 0)
        {
            throw new InvalidOperationException("The provider baseline renderer returned an empty SQL statement.");
        }

        var normalized = commandText[..(end + 1)];
        EnsureNoStatementSeparator(normalized);
        return normalized;
    }

    private static void EnsureNoStatementSeparator(
        string sql
    )
    {
        var state = LexerState.Normal;

        // A semicolon is a statement separator only in normal lexer state.
        // Rejecting it here keeps PREPARE confined to one provider command.
        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';

            switch (state)
            {
                case LexerState.Normal:
                    switch (current)
                    {
                        case '\'':
                            state = LexerState.SingleQuoted;
                            break;
                        case '"':
                            state = LexerState.DoubleQuoted;
                            break;
                        case '`':
                            state = LexerState.Identifier;
                            break;
                        case '/'
                            when next == '*':
                            state = LexerState.BlockComment;
                            index++;
                            break;
                        case '-'
                            when next == '-'
                            && IsCommentBoundary(sql, index + 2):
                            state = LexerState.LineComment;
                            index++;
                            break;
                        case '#':
                            state = LexerState.LineComment;
                            break;
                        case ';':
                            throw new InvalidOperationException(
                                "A provider baseline command contains multiple SQL statements and cannot be prepared safely.");
                    }

                    break;
                case LexerState.SingleQuoted:
                    switch (current)
                    {
                        case '\\':
                        case '\''
                            when next == '\'':
                            index++;
                            break;
                        case '\'':
                            state = LexerState.Normal;
                            break;
                    }

                    break;
                case LexerState.DoubleQuoted:
                    switch (current)
                    {
                        case '\\':
                        case '"'
                            when next == '"':
                            index++;
                            break;
                        case '"':
                            state = LexerState.Normal;
                            break;
                    }

                    break;
                case LexerState.Identifier:
                    switch (current)
                    {
                        case '`'
                            when next == '`':
                            index++;
                            break;
                        case '`':
                            state = LexerState.Normal;
                            break;
                    }

                    break;
                case LexerState.LineComment:
                    if (current is '\r' or '\n')
                    {
                        state = LexerState.Normal;
                    }

                    break;
                case LexerState.BlockComment:
                    if (current == '*'
                        && next == '/')
                    {
                        state = LexerState.Normal;
                        index++;
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }

        if (state is LexerState.SingleQuoted
            or LexerState.DoubleQuoted
            or LexerState.Identifier
            or LexerState.BlockComment)
        {
            throw new InvalidOperationException("The provider baseline command contains an unterminated SQL token.");
        }
    }

    private static string ExtractDdlCommentProtectedStatement(
        string commandText
    )
    {
        // The provider wraps DDL comments in a four-statement sql_mode scope.
        // Only its inner DDL statement belongs in the SafeMigrations guard.
        var statements = SplitStatements(commandText);
        if (statements.Length != 4
            || !IsExecutableSqlModeSet(statements[0], requireSession: false)
            || !IsExecutableSqlModeSet(statements[1], requireSession: true)
            || !IsExecutableSqlModeSet(statements[3], requireSession: true))
        {
            throw new InvalidOperationException(
                "The provider DDL-comment SQL-mode scope has an unknown command shape.");
        }

        return statements[2];
    }

    private static string[] SplitStatements(
        string sql
    )
    {
        var statements = new List<string>();
        var start = 0;
        var state = LexerState.Normal;

        // Track quoted tokens and comments so embedded semicolons remain data.
        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            switch (state)
            {
                case LexerState.Normal:
                    switch (current)
                    {
                        case '\'':
                            state = LexerState.SingleQuoted;
                            break;
                        case '"':
                            state = LexerState.DoubleQuoted;
                            break;
                        case '`':
                            state = LexerState.Identifier;
                            break;
                        case '/'
                            when next == '*':
                            state = LexerState.BlockComment;
                            index++;
                            break;
                        case '-'
                            when next == '-'
                            && IsCommentBoundary(sql, index + 2):
                            state = LexerState.LineComment;
                            index++;
                            break;
                        case '#':
                            state = LexerState.LineComment;
                            break;
                        case ';':
                            AddStatement(statements, sql[start..index]);
                            start = index + 1;
                            break;
                    }

                    break;
                case LexerState.SingleQuoted:
                    switch (current)
                    {
                        case '\''
                            when next == '\'':
                            index++;
                            break;
                        case '\'':
                            state = LexerState.Normal;
                            break;
                    }

                    break;
                case LexerState.DoubleQuoted:
                    switch (current)
                    {
                        case '\\':
                        case '"'
                            when next == '"':
                            index++;
                            break;
                        case '"':
                            state = LexerState.Normal;
                            break;
                    }

                    break;
                case LexerState.Identifier:
                    switch (current)
                    {
                        case '`'
                            when next == '`':
                            index++;
                            break;
                        case '`':
                            state = LexerState.Normal;
                            break;
                    }

                    break;
                case LexerState.LineComment:
                    if (current is '\r' or '\n')
                    {
                        state = LexerState.Normal;
                    }

                    break;
                case LexerState.BlockComment:
                    if (current == '*'
                        && next == '/')
                    {
                        state = LexerState.Normal;
                        index++;
                    }

                    break;
                default:
                    throw new UnreachableException();
            }
        }

        if (state is LexerState.SingleQuoted
            or LexerState.DoubleQuoted
            or LexerState.Identifier
            or LexerState.BlockComment)
        {
            throw new InvalidOperationException("The provider baseline command contains an unterminated SQL token.");
        }

        AddStatement(statements, sql[start..]);
        return statements.ToArray();
    }

    private static void AddStatement(
        List<string> statements,
        string sql
    )
    {
        var value = sql.Trim();
        if (value.Length > 0)
        {
            statements.Add(value);
        }
    }

    private static bool IsExecutableSqlModeSet(
        string statement,
        bool requireSession
    )
    {
        var normalized = statement.Trim();

        return normalized.StartsWith("/*! SET ", StringComparison.OrdinalIgnoreCase)
            && normalized.EndsWith(" */", StringComparison.Ordinal)
            && normalized.Contains("sql_mode", StringComparison.OrdinalIgnoreCase)
            && (!requireSession || normalized.Contains("SET SESSION", StringComparison.OrdinalIgnoreCase));
    }

    private static string EscapeBackslashesInQuotedLiterals(
        string sql
    )
    {
        // The DDL is embedded in a second SQL literal for PREPARE. Default
        // sql_mode therefore needs one additional backslash-escaping layer.
        var builder = new StringBuilder(sql.Length + 16);
        var state = LexerState.Normal;
        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            builder.Append(current);
            switch (state)
            {
                case LexerState.Normal:
                    state = current switch
                    {
                        '\'' => LexerState.SingleQuoted,
                        '"' => LexerState.DoubleQuoted,
                        '`' => LexerState.Identifier,
                        _ => state
                    };

                    break;
                case LexerState.SingleQuoted:
                    switch (current)
                    {
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '\''
                            when next == '\'':
                            builder.Append(sql[++index]);
                            break;
                        case '\'':
                            state = LexerState.Normal;
                            break;
                    }

                    break;
                case LexerState.DoubleQuoted:
                    switch (current)
                    {
                        case '"'
                            when next == '"':
                            builder.Append(sql[++index]);
                            break;
                        case '"':
                            state = LexerState.Normal;
                            break;
                    }

                    break;
                case LexerState.Identifier:
                    switch (current)
                    {
                        case '`'
                            when next == '`':
                            builder.Append(sql[++index]);
                            break;
                        case '`':
                            state = LexerState.Normal;
                            break;
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        "The provider DDL statement contains an unsupported SQL token.");
            }
        }

        return state == LexerState.Normal
            ? builder.ToString()
            : throw new InvalidOperationException("The provider DDL statement contains an unterminated quoted token.");
    }

    private static bool IsCommentBoundary(
        string sql,
        int index
    ) => index >= sql.Length || char.IsWhiteSpace(sql[index]);

    private enum LexerState
    {
        Normal,
        SingleQuoted,
        DoubleQuoted,
        Identifier,
        LineComment,
        BlockComment,
    }
}

internal readonly record struct PreparedStatementText(
    string NoBackslashEscapesSql,
    string DefaultSqlModeSql,
    bool IsSqlModeSensitive
);
