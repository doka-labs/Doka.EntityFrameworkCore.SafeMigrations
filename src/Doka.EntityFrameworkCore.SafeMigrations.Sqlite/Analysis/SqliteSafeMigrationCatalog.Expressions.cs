namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal static partial class SqliteSafeMigrationCatalog
{
    private static readonly Regex s_indexCollationPattern = new(
        "\\s+COLLATE\\s+(?:\"(?:\"\"|[^\"])*\"|\\[[^]]+\\]|`(?:``|[^`])*`|[A-Za-z_][A-Za-z0-9_]*)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_indexSortOrderPattern = new(
        @"\s+(?:ASC|DESC)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string? ReadCollation(
        string? columnSql
    )
    {
        // WHY: COLLATE inside AS(...) or DEFAULT(...) belongs to that expression,
        // not to the column's declared comparison identity.
        return columnSql is null
            ? null
            : SqliteSqlIdentifierScanner.ReadTopLevelIdentifierFollowingKeyword(columnSql, "COLLATE");
    }

    private static string? ReadGeneratedExpression(
        string? columnSql
    )
    {
        if (columnSql is null)
        {
            return null;
        }

        var openParenthesis = SqliteSqlIdentifierScanner.IndexOfCharacterFollowingKeyword(columnSql, "AS", '(');

        return openParenthesis >= 0
            ? ReadBalancedParentheses(columnSql, openParenthesis)
            : null;
    }

    private static string? ReadIndexFilter(
        string sql
    )
    {
        var where = SqliteSqlIdentifierScanner.IndexOfKeyword(sql, "WHERE");

        return where >= 0
            ? sql
                .AsSpan(where + "WHERE".Length)
                .Trim(SqliteSqlIdentifierScanner.WhitespaceCharacters)
                .ToString()
            : null;
    }

    private static bool HasTableOption(
        string sql,
        string option
    )
    {
        var bodyEnd = sql.LastIndexOf(')');
        if (bodyEnd < 0)
        {
            return false;
        }

        var options = sql[(bodyEnd + 1)..];

        return StringComparer.Ordinal.Equals(option, "WITHOUT ROWID")
            ? SqliteSqlIdentifierScanner.ContainsKeywordSequence(options, "WITHOUT", "ROWID")
            : SqliteSqlIdentifierScanner.ContainsKeyword(options, option);
    }

    private static string[] ReadIndexKeyClauses(
        string indexSql
    )
    {
        if (indexSql.Length == 0)
        {
            return [];
        }

        // WHY: A quoted index name may itself contain ON and parentheses.
        // The shared lexical scanner must identify the real owner before slicing keys.
        var openParenthesis = SqliteSqlIdentifierScanner.IndexOfOwnerKeyList(indexSql, "ON");

        return openParenthesis >= 0
            ? SplitTopLevel(ReadBalancedParentheses(indexSql, openParenthesis))

                // WHY: Preserve explicit fluent materialization for the ordered snapshot.
                // ReSharper disable once UseCollectionExpression
                .ToArray()
            : [];
    }

    private static string ReadIndexExpression(
        string clause
    )
    {
        var value = s_indexSortOrderPattern.Replace(
            clause
                .AsSpan()
                .Trim(SqliteSqlIdentifierScanner.WhitespaceCharacters)
                .ToString(),
            string.Empty);

        return s_indexCollationPattern.Replace(value, string.Empty);
    }
}
