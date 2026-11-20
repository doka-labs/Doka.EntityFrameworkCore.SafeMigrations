namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal static partial class SqliteSafeMigrationCatalog
{
    private static string[] ReadIdentifierList(
        string sql,
        int openParenthesis
    ) => SplitTopLevel(ReadBalancedParentheses(sql, openParenthesis))
        .Select(ReadLeadingIdentifier)
        .Where(static value => value is not null)
        .Select(static value => value!)

        // WHY: Preserve explicit fluent materialization for the ordered snapshot.
        // ReSharper disable once UseCollectionExpression
        .ToArray();

    private static string IdentifierPattern(
        string group
    ) => "(?:"
        + QuotedIdentifierAlternatives(group)
        + "|(?<"
        + group
        + "Bare>[A-Za-z_\u0080-\uffff][A-Za-z0-9_$\u0080-\uffff]*))";

    // WHY: Bare names need a token separator; quoting delimiters already
    // separate the keyword from its identifier without whitespace.
    private static string IdentifierFollowingKeywordPattern(
        string group
    ) => @"(?:\s+" + IdentifierPattern(group) + @"|\s*(?:" + QuotedIdentifierAlternatives(group) + "))";

    private static string QuotedIdentifierAlternatives(
        string group
    ) => "\"(?<"
        + group
        + ">(?:\"\"|[^\"])*)\""
        + "|\\[(?<"
        + group
        + "Bracket>[^]]+)\\]"
        + "|`(?<"
        + group
        + "Backtick>(?:``|[^`])*)`"
        + "|'(?<"
        + group
        + "Single>(?:''|[^'])*)'";

    private static string ReadName(
        Match match,
        string group
    )
    {
        var value = match switch
        {
            _ when match.Groups[group].Success => match
                .Groups[group]
                .Value
                .Replace("\"\"", "\"", StringComparison.Ordinal),
            _ when match.Groups[group + "Bracket"].Success => match.Groups[group + "Bracket"].Value,
            _ when match.Groups[group + "Backtick"].Success => match
                .Groups[group + "Backtick"]
                .Value
                .Replace("``", "`", StringComparison.Ordinal),
            _ when match.Groups[group + "Single"].Success => match
                .Groups[group + "Single"]
                .Value
                .Replace("''", "'", StringComparison.Ordinal),
            _ => match.Groups[group + "Bare"].Value,
        };

        return value;
    }

    private static bool HasName(
        Match match,
        string group
    ) => match.Groups[group].Success
        || match.Groups[group + "Bracket"].Success
        || match.Groups[group + "Backtick"].Success
        || match.Groups[group + "Single"].Success
        || match.Groups[group + "Bare"].Success;

    private static string? ReadLeadingIdentifier(
        string clause
    )
    {
        if (clause.Length == 0)
        {
            return null;
        }

        // WHY: Quoted-name exits precede bare-token scanning; two guards do not need a dispatch statement.
        // ReSharper disable once ConvertIfStatementToSwitchStatement
        if (clause[0] is '\"' or '\'' or '`')
        {
            return ReadQuotedIdentifier(clause, clause[0]);
        }

        if (clause[0] == '[')
        {
            var end = clause.IndexOf(']');

            return end > 0
                ? clause[1..end]
                : null;
        }

        var length = 0;
        while (length < clause.Length
            && !SqliteSqlIdentifierScanner.IsWhitespace(clause[length]))
        {
            length++;
        }

        return length == 0
            ? null
            : clause[..length];
    }

    private static string? ReadQuotedIdentifier(
        string value,
        char quote
    )
    {
        var builder = new StringBuilder(value.Length);

        // WHY: Consume escaped delimiters together while retaining the quoted token's exact source offsets.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] != quote)
            {
                builder.Append(value[index]);
                continue;
            }

            if (index + 1 < value.Length
                && value[index + 1] == quote)
            {
                builder.Append(quote);
                index++;
                continue;
            }

            return builder.ToString();
        }

        return null;
    }

    private static bool IdentifiersEqual(
        string[] left,
        string[] right
    )
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        // WHY: Compare ordered identifier members at matching offsets to preserve native identity semantics.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < left.Length; index++)
        {
            if (!s_identifierComparer.Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }
}
