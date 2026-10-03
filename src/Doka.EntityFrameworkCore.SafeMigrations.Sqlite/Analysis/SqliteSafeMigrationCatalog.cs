namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Reads a bounded, immutable snapshot of the active SQLite catalog.</summary>
internal static partial class SqliteSafeMigrationCatalog
{
    private static readonly StringComparer s_identifierComparer = SqliteIdentifierComparer.Instance;

    private static readonly Regex s_indexCollationPattern = new(
        "\\s+COLLATE\\s+(?:\"(?:\"\"|[^\"])*\"|\\[[^]]+\\]|`(?:``|[^`])*`|[A-Za-z_][A-Za-z0-9_]*)\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_indexKeyListPattern = new(
        @"\bON\s+" + IdentifierPattern("table") + @"\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_indexSortOrderPattern = new(
        @"\s+(?:ASC|DESC)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_foreignKeyClausePattern = new(
        @"^\s*(?:CONSTRAINT" + IdentifierFollowingKeywordPattern("name") + @"\s*)?FOREIGN\s+KEY\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_constraintNamePattern = new(
        @"\bCONSTRAINT" + IdentifierFollowingKeywordPattern("name"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_referencePattern = new(
        @"\bREFERENCES" + IdentifierFollowingKeywordPattern("table") + @"(?:\s*(?<open>\())?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_uniqueConstraintPattern = new(
        @"^\s*(?:CONSTRAINT\s+" + IdentifierPattern("name") + @"\s+)?UNIQUE\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_virtualTableDefinitionPattern = new(
        @"^\s*CREATE\s+VIRTUAL\s+TABLE\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Reads the catalog through the supplied connection and transaction.</summary>
    public static SqliteCatalogSnapshot Read(
        DbConnection connection,
        DbTransaction? transaction
    )
    {
        ArgumentNullException.ThrowIfNull(connection);

        var settings = ReadSettings(connection, transaction);
        var builders = new Dictionary<string, TableBuilder>(s_identifierComparer);
        var otherObjects = ReadSchemaObjects(connection, transaction, builders);
        ReadColumns(connection, transaction, builders);
        ReadIndexes(connection, transaction, builders);
        ReadForeignKeys(connection, transaction, builders);
        var tables = new Dictionary<string, SqliteTableSnapshot>(s_identifierComparer);
        foreach (var builder in builders.Values)
        {
            tables.Add(builder.Name, BuildTable(builder));
        }

        return new SqliteCatalogSnapshot(
            settings.Version,
            settings.ForeignKeysEnabled,
            settings.LegacyAlterTableEnabled,
            new ReadOnlyDictionary<string, SqliteTableSnapshot>(tables),
            otherObjects);
    }

    private static SqliteTableSnapshot BuildTable(
        TableBuilder builder
    )
    {
        var tableSql = builder.Sql;
        var tableClauses = builder.Clauses;
        var columns = builder.Columns;
        var indexes = builder.Indexes;
        var checks = ReadChecks(tableClauses);
        var primaryKeyColumns = columns
            .Values
            .Where(static value => value.PrimaryKeyOrdinal > 0)
            .OrderBy(static value => value.PrimaryKeyOrdinal)
            .Select(static value => value.Name)
            .ToArray();

        var primaryKeyName = ReadPrimaryKeyName(tableClauses);
        var primaryKeyIndex = indexes.FirstOrDefault(static value => value.Origin == "pk");
        var primaryKeyKeys = primaryKeyIndex
                ?.Keys
                .Where(static value => value.IsKey)
                .OrderBy(static value => value.Ordinal)
                .ToArray()
            ?? primaryKeyColumns
                .Select((
                    column,
                    ordinal
                ) => new SqliteIndexKeySnapshot(
                    ordinal,
                    columns[column].Ordinal,
                    column,
                    Expression: null,
                    Descending: false,
                    columns[column].Collation ?? "BINARY",
                    IsKey: true))
                .ToArray();

        var parsedUniques = ReadUniqueConstraints(tableClauses);
        var uniqueIndexes = indexes
            .Where(static value => value.Origin == "u")
            .ToArray();

        var uniqueConstraints = new SqliteUniqueSnapshot[uniqueIndexes.Length];

        for (var index = 0; index < uniqueIndexes.Length; index++)
        {
            var uniqueIndex = uniqueIndexes[index];
            var uniqueColumns = uniqueIndex
                .Keys
                .Where(static value => value.IsKey && value.Column is not null)
                .OrderBy(static value => value.Ordinal)
                .Select(static value => value.Column!)
                .ToArray();

            var name = parsedUniques.FirstOrDefault(value => IdentifiersEqual(value.Columns, uniqueColumns))?.Name;
            uniqueConstraints[index] = new SqliteUniqueSnapshot(
                name,
                uniqueIndex.Name,
                Array.AsReadOnly(uniqueColumns),
                Array.AsReadOnly(uniqueIndex
                    .Keys
                    .Where(static value => value.IsKey)
                    .OrderBy(static value => value.Ordinal)
                    .ToArray()));
        }

        return new SqliteTableSnapshot(
            builder.Name,
            tableSql,
            new ReadOnlyDictionary<string, SqliteColumnSnapshot>(columns),
            indexes.AsReadOnly(),
            builder.ForeignKeys.AsReadOnly(),
            checks,
            Array.AsReadOnly(primaryKeyColumns),
            Array.AsReadOnly(primaryKeyKeys),
            primaryKeyName,
            Array.AsReadOnly(uniqueConstraints),
            HasUnmodeledConstraintOptions(tableClauses),
            HasTableOption(tableSql, "STRICT"),
            HasTableOption(tableSql, "WITHOUT ROWID"));
    }

    private static ReadOnlyCollection<SqliteCheckSnapshot> ReadChecks(
        IReadOnlyList<string> tableClauses
    )
    {
        var result = new List<SqliteCheckSnapshot>();
        foreach (var clause in tableClauses)
        {
            var searchStart = 0;
            while (true)
            {
                var checkStart = SqliteSqlIdentifierScanner.IndexOfKeyword(clause, "CHECK", searchStart);
                if (checkStart < 0)
                {
                    break;
                }

                var openParenthesis = clause.IndexOf('(', checkStart + "CHECK".Length);
                if (openParenthesis < 0)
                {
                    break;
                }

                var expression = ReadBalancedParentheses(clause, openParenthesis);
                var closeParenthesis = FindBalancedParenthesisEnd(clause, openParenthesis);
                var constraintPrefix = clause[..checkStart];

                // WHY: SQLite assigns the latest column CONSTRAINT name to a
                // following CHECK until the column clause ends.
                var name = SqliteSqlIdentifierScanner.ReadLastIdentifierFollowingKeyword(
                    constraintPrefix,
                    "CONSTRAINT");

                result.Add(new SqliteCheckSnapshot(name, expression));
                searchStart = closeParenthesis + 1;
            }
        }

        return result.AsReadOnly();
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string sql
    )
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        return command;
    }

    private static ReadOnlyCollection<ParsedUniqueConstraint> ReadUniqueConstraints(
        IReadOnlyList<string> tableClauses
    )
    {
        var result = new List<ParsedUniqueConstraint>();
        foreach (var clause in tableClauses)
        {
            var match = s_uniqueConstraintPattern.Match(clause);
            if (!match.Success)
            {
                continue;
            }

            result.Add(
                new ParsedUniqueConstraint(
                    HasName(match, "name") ? ReadName(match, "name") : null,
                    ReadIdentifierList(clause, match.Index + match.Length - 1)));
        }

        return result.AsReadOnly();
    }

    private static bool HasUnmodeledConstraintOptions(
        IReadOnlyList<string> tableClauses
    )
    {
        foreach (var clause in tableClauses)
        {
            if (SqliteSqlIdentifierScanner.ContainsKeywordSequence(clause, "ON", "CONFLICT"))
            {
                return true;
            }

            var isForeignKeyClause = SqliteSqlIdentifierScanner.ContainsKeyword(clause, "REFERENCES")
                || SqliteSqlIdentifierScanner.ContainsKeywordSequence(clause, "FOREIGN", "KEY");

            if (isForeignKeyClause
                && (SqliteSqlIdentifierScanner.ContainsKeyword(clause, "MATCH")
                    || SqliteSqlIdentifierScanner.ContainsKeyword(clause, "DEFERRABLE")
                    || SqliteSqlIdentifierScanner.ContainsKeyword(clause, "INITIALLY")))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlyCollection<ParsedForeignKey> ReadDeclaredForeignKeys(
        IReadOnlyList<string> tableClauses
    )
    {
        var result = new List<ParsedForeignKey>();
        foreach (var clause in tableClauses)
        {
            var tableConstraint = s_foreignKeyClausePattern.Match(clause);
            var column = ReadLeadingIdentifier(clause);
            var searchStart = 0;
            while (true)
            {
                var referenceStart = SqliteSqlIdentifierScanner.IndexOfKeyword(clause, "REFERENCES", searchStart);
                if (referenceStart < 0)
                {
                    break;
                }

                var reference = s_referencePattern.Match(clause, referenceStart);
                if (!reference.Success || reference.Index != referenceStart || column is null)
                {
                    throw InvalidMetadata();
                }

                var columns = tableConstraint.Success
                    ? ReadIdentifierList(clause, tableConstraint.Index + tableConstraint.Length - 1)
                    : [column];

                var principalOpen = reference.Groups["open"].Success ? reference.Groups["open"].Index : -1;
                var principalColumns = principalOpen < 0
                    ? []
                    : ReadIdentifierList(clause, principalOpen);

                searchStart = principalOpen < 0
                    ? reference.Index + reference.Length
                    : FindBalancedParenthesisEnd(clause, principalOpen) + 1;

                var nextReference = SqliteSqlIdentifierScanner.IndexOfKeyword(clause, "REFERENCES", searchStart);
                var options = nextReference < 0 ? clause[searchStart..] : clause[searchStart..nextReference];
                var name = tableConstraint.Success
                    ? HasName(tableConstraint, "name") ? ReadName(tableConstraint, "name") : null
                    : ReadLastDeclaredConstraintName(clause[..referenceStart]);

                result.Add(
                    new ParsedForeignKey(
                        name,
                        columns,
                        ReadName(reference, "table"),
                        principalColumns,
                        ReadDeclaredReferentialAction(options, "UPDATE"),
                        ReadDeclaredReferentialAction(options, "DELETE")));
            }
        }

        return result.AsReadOnly();
    }

    private static string? ReadLastDeclaredConstraintName(
        string sql
    )
    {
        string? result = null;
        var searchStart = 0;
        while (true)
        {
            var keyword = SqliteSqlIdentifierScanner.IndexOfKeyword(sql, "CONSTRAINT", searchStart);
            if (keyword < 0)
            {
                return result;
            }

            // WHY: Single quotes are identifiers at this grammar position;
            // the shared scanner must continue treating them as literals elsewhere.
            var match = s_constraintNamePattern.Match(sql, keyword);
            if (!match.Success || match.Index != keyword)
            {
                throw InvalidMetadata();
            }

            result = ReadName(match, "name");
            searchStart = match.Index + match.Length;
        }
    }

    private static ReferentialAction ReadDeclaredReferentialAction(
        string sql,
        string direction
    )
    {
        var action = sql;
        var found = false;
        while (true)
        {
            var start = SqliteSqlIdentifierScanner.IndexOfKeywordSequence(action, "ON", direction);
            if (start < 0)
            {
                break;
            }

            found = true;
            var directionAndAction = action[(start + "ON".Length)..].TrimStart();
            action = directionAndAction[direction.Length..].TrimStart();
        }

        if (!found)
        {
            return ReferentialAction.NoAction;
        }

        if (SqliteSqlIdentifierScanner.IndexOfKeywordSequence(action, "NO", "ACTION") == 0)
        {
            return ReferentialAction.NoAction;
        }

        if (SqliteSqlIdentifierScanner.IndexOfKeywordSequence(action, "SET", "NULL") == 0)
        {
            return ReferentialAction.SetNull;
        }

        if (SqliteSqlIdentifierScanner.IndexOfKeywordSequence(action, "SET", "DEFAULT") == 0)
        {
            return ReferentialAction.SetDefault;
        }

        if (SqliteSqlIdentifierScanner.IndexOfKeyword(action, "CASCADE") == 0)
        {
            return ReferentialAction.Cascade;
        }

        if (SqliteSqlIdentifierScanner.IndexOfKeyword(action, "RESTRICT") == 0)
        {
            return ReferentialAction.Restrict;
        }

        throw InvalidMetadata();
    }

    private static string[] ReadTableClauses(
        string tableSql
    )
    {
        var open = FindFirstUnquotedParenthesis(tableSql);

        return open < 0
            ? []
            : SplitTopLevel(tableSql[(open + 1)..])
                .ToArray();
    }

    private static int FindFirstUnquotedParenthesis(
        string sql
    )
    {
        var quote = '\0';
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    if (quote != ']' && index + 1 < sql.Length && sql[index + 1] == quote)
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

            if (character is '\'' or '\"' or '`' or '[')
            {
                quote = character == '[' ? ']' : character;
            }
            else if (character == '(')
            {
                return index;
            }
        }

        return -1;
    }

    private static string[] ReadIdentifierList(
        string sql,
        int openParenthesis
    ) => SplitTopLevel(ReadBalancedParentheses(sql, openParenthesis))
        .Select(ReadLeadingIdentifier)
        .Where(static value => value is not null)
        .Select(static value => value!)
        .ToArray();

    private static string IdentifierPattern(
        string group
    ) => "(?:" + QuotedIdentifierAlternatives(group)
        + "|(?<" + group + "Bare>[A-Za-z_\u0080-\uffff][A-Za-z0-9_$\u0080-\uffff]*))";

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
        var value = match.Groups[group].Success
            ? match
                .Groups[group]
                .Value
                .Replace("\"\"", "\"", StringComparison.Ordinal)
            : match.Groups[group + "Bracket"].Success
                ? match.Groups[group + "Bracket"].Value
                : match.Groups[group + "Backtick"].Success
                    ? match
                        .Groups[group + "Backtick"]
                        .Value
                        .Replace("``", "`", StringComparison.Ordinal)
                    : match.Groups[group + "Single"].Success
                        ? match.Groups[group + "Single"].Value.Replace("''", "'", StringComparison.Ordinal)
                        : match.Groups[group + "Bare"].Value;

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

    private static string ReadName(
        Match match
    )
    {
        var value = match.Groups["name"].Success
            ? match
                .Groups["name"]
                .Value
                .Replace("\"\"", "\"", StringComparison.Ordinal)
            : match.Groups["bracket"].Success
                ? match.Groups["bracket"].Value
                : match.Groups["backtick"].Success
                    ? match
                        .Groups["backtick"]
                        .Value
                        .Replace("``", "`", StringComparison.Ordinal)
                    : match.Groups["bare"].Value;

        return value;
    }

    private static string ReadBalancedParentheses(
        string sql,
        int openParenthesis
    )
    {
        var depth = 0;
        var quote = '\0';
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

            if (character is '\'' or '\"' or '`')
            {
                quote = character;
                continue;
            }

            if (character == '[')
            {
                quote = ']';
                continue;
            }

            if (character == '(')
            {
                depth++;
            }
            else if (character == ')'
                     && --depth == 0)
            {
                return sql.AsSpan(openParenthesis + 1, index - openParenthesis - 1).Trim().ToString();
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

            if (character is '\'' or '\"' or '`')
            {
                quote = character;
            }
            else if (character == '[')
            {
                quote = ']';
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')'
                     && --depth == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException("SQLite catalog SQL contains an unbalanced expression.");
    }

    private static Dictionary<string, string> ReadColumnClauses(
        IReadOnlyList<string> tableClauses
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

            if (character is '\'' or '\"' or '`')
            {
                quote = character;
            }
            else if (character == '[')
            {
                quote = ']';
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                if (depth == 0)
                {
                    yield return sql.AsSpan(start, index - start).Trim().ToString();
                    yield break;
                }

                depth--;
            }
            else if (character == ','
                     && depth == 0)
            {
                yield return sql.AsSpan(start, index - start).Trim().ToString();
                start = index + 1;
            }
        }

        if (start < sql.Length)
        {
            yield return sql.AsSpan(start).Trim().ToString();
        }
    }

    private static string? ReadLeadingIdentifier(
        string clause
    )
    {
        if (clause.Length == 0)
        {
            return null;
        }

        if (clause[0] is '\"' or '\'')
        {
            return ReadQuotedIdentifier(clause, clause[0]);
        }

        if (clause[0] == '[')
        {
            var end = clause.IndexOf(']');
            return end > 0 ? clause[1..end] : null;
        }

        if (clause[0] == '`')
        {
            return ReadQuotedIdentifier(clause, '`');
        }

        var length = 0;
        while (length < clause.Length
               && !char.IsWhiteSpace(clause[length]))
        {
            length++;
        }

        return length == 0 ? null : clause[..length];
    }

    private static string? ReadQuotedIdentifier(
        string value,
        char quote
    )
    {
        var builder = new StringBuilder(value.Length);
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

    private static string? ReadCollation(
        string? columnSql
    )
    {
        if (columnSql is null)
        {
            return null;
        }

        return SqliteSqlIdentifierScanner.ReadIdentifierFollowingKeyword(columnSql, "COLLATE");
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

        return openParenthesis >= 0 ? ReadBalancedParentheses(columnSql, openParenthesis) : null;
    }

    private static string? ReadIndexFilter(
        string sql
    )
    {
        var where = SqliteSqlIdentifierScanner.IndexOfKeyword(sql, "WHERE");

        return where >= 0
            ? sql.AsSpan(where + "WHERE".Length).Trim().ToString()
            : null;
    }

    private static string? ReadPrimaryKeyName(
        IReadOnlyList<string> tableClauses
    )
    {
        foreach (var clause in tableClauses)
        {
            var primaryKey = SqliteSqlIdentifierScanner.IndexOfKeywordSequence(clause, "PRIMARY", "KEY");
            if (primaryKey < 0)
            {
                continue;
            }

            // WHY: A column PRIMARY KEY can follow other named constraints;
            // SQLite binds the latest preceding CONSTRAINT name to the key.

            return SqliteSqlIdentifierScanner.ReadLastIdentifierFollowingKeyword(clause[..primaryKey], "CONSTRAINT");
        }

        return null;
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

            if (character is '\'' or '\"' or '`')
            {
                quote = character;
                builder.Append(character);
                continue;
            }

            if (character == '[')
            {
                quote = ']';
                builder.Append(character);
                continue;
            }

            if (character == '-'
                && index + 1 < sql.Length
                && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length
                       && sql[index] is not '\r' and not '\n')
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

        var match = s_indexKeyListPattern.Match(indexSql);

        return match.Success
            ? SplitTopLevel(ReadBalancedParentheses(indexSql, match.Index + match.Length - 1))
                .ToArray()
            : [];
    }

    private static string ReadIndexExpression(
        string clause
    )
    {
        var value = s_indexSortOrderPattern.Replace(clause.Trim(), string.Empty);

        return s_indexCollationPattern.Replace(value, string.Empty);
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

        for (var index = 0; index < left.Length; index++)
        {
            if (!s_identifierComparer.Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static ReferentialAction ParseReferentialAction(
        string value
    ) => value.ToUpperInvariant() switch
    {
        "NO ACTION" => ReferentialAction.NoAction,
        "RESTRICT" => ReferentialAction.Restrict,
        "CASCADE" => ReferentialAction.Cascade,
        "SET NULL" => ReferentialAction.SetNull,
        "SET DEFAULT" => ReferentialAction.SetDefault,
        _ => throw new InvalidOperationException($"SQLite returned unknown referential action '{value}'."),
    };

    private sealed record ParsedUniqueConstraint(
        string? Name,
        string[] Columns
    );

    private sealed record ParsedForeignKey(
        string? Name,
        string[] Columns,
        string PrincipalTable,
        string[] PrincipalColumns,
        ReferentialAction OnUpdate,
        ReferentialAction OnDelete
    );
}
