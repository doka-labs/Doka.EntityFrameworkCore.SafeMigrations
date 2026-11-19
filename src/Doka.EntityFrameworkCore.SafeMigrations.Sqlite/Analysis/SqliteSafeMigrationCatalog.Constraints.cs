namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal static partial class SqliteSafeMigrationCatalog
{
    private static readonly Regex s_foreignKeyClausePattern = new(
        @"^\s*(?:CONSTRAINT" + IdentifierFollowingKeywordPattern("name") + @"\s*)?FOREIGN\s+KEY\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_constraintNamePattern = new(
        @"\bCONSTRAINT" + IdentifierFollowingKeywordPattern("name"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_referencePattern = new(
        @"\bREFERENCES" + IdentifierFollowingKeywordPattern("table") + @"(?:\s*(?<open>\())?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static ReadOnlyCollection<SqliteCheckSnapshot> ReadChecks(
        string[] tableClauses
    )
    {
        List<SqliteCheckSnapshot> result = [];
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
                var name = ReadLastDeclaredConstraintName(constraintPrefix);

                result.Add(new SqliteCheckSnapshot(name, expression));
                searchStart = closeParenthesis + 1;
            }
        }

        return result.AsReadOnly();
    }

    private static ReadOnlyCollection<ParsedUniqueConstraint> ReadUniqueConstraints(
        string[] tableClauses
    )
    {
        List<ParsedUniqueConstraint> result = [];
        foreach (var clause in tableClauses)
        {
            var uniqueStart = SqliteSqlIdentifierScanner.IndexOfKeyword(clause, "UNIQUE");
            if (uniqueStart < 0)
            {
                continue;
            }

            var keyList = SqliteSqlIdentifierScanner.IndexOfCharacterFollowingKeyword(clause, "UNIQUE", '(');
            string[] columns;
            if (keyList >= 0)
            {
                columns = ReadIdentifierList(clause, keyList);
            }
            else if (ReadLeadingIdentifier(clause) is { } leadingColumn)
            {
                columns = [leadingColumn];
            }
            else
            {
                continue;
            }

            // WHY: Native UNIQUE declarations may be table-level or column-level.
            // Both positions use the same quoted-name grammar, including adjacent tokens.
            result.Add(
                new ParsedUniqueConstraint(
                    ReadLastDeclaredConstraintName(clause[..uniqueStart]),
                    columns));
        }

        return result.AsReadOnly();
    }

    private static bool HasUnmodeledConstraintOptions(
        string[] tableClauses
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
        string[] tableClauses
    )
    {
        List<ParsedForeignKey> result = [];
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
                if (!reference.Success
                    || reference.Index != referenceStart
                    || column is null)
                {
                    throw InvalidMetadata();
                }

                var columns = tableConstraint.Success
                    ? ReadIdentifierList(clause, tableConstraint.Index + tableConstraint.Length - 1)
                    : [column];

                var principalOpen = reference.Groups["open"].Success
                    ? reference.Groups["open"].Index
                    : -1;

                var principalColumns = principalOpen < 0
                    ? []
                    : ReadIdentifierList(clause, principalOpen);

                searchStart = principalOpen < 0
                    ? reference.Index + reference.Length
                    : FindBalancedParenthesisEnd(clause, principalOpen) + 1;

                var nextReference = SqliteSqlIdentifierScanner.IndexOfKeyword(clause, "REFERENCES", searchStart);
                var options = nextReference < 0
                    ? clause[searchStart..]
                    : clause[searchStart..nextReference];

                var name = tableConstraint.Success switch
                {
                    false => ReadLastDeclaredConstraintName(clause[..referenceStart]),
                    true when HasName(tableConstraint, "name") => ReadName(tableConstraint, "name"),
                    _ => null,
                };

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
            if (!match.Success
                || match.Index != keyword)
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
            var directionAndAction = action
                .AsSpan(start + "ON".Length)
                .TrimStart(SqliteSqlIdentifierScanner.WhitespaceCharacters);

            action = directionAndAction[direction.Length..]
                .TrimStart(SqliteSqlIdentifierScanner.WhitespaceCharacters)
                .ToString();
        }

        if (!found
            || SqliteSqlIdentifierScanner.IndexOfKeywordSequence(action, "NO", "ACTION") == 0)
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

        return SqliteSqlIdentifierScanner.IndexOfKeyword(action, "RESTRICT") == 0
            ? ReferentialAction.Restrict
            : throw InvalidMetadata();
    }

    private static string? ReadPrimaryKeyName(
        string[] tableClauses
    )
    {
        // WHY: Preserve array traversal and stop at the first declared key without iterator materialization.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var clause in tableClauses)
        {
            var primaryKey = SqliteSqlIdentifierScanner.IndexOfKeywordSequence(clause, "PRIMARY", "KEY");
            if (primaryKey < 0)
            {
                continue;
            }

            // WHY: A column PRIMARY KEY can follow other named constraints;
            // SQLite binds the latest preceding CONSTRAINT name to the key.

            return ReadLastDeclaredConstraintName(clause[..primaryKey]);
        }

        return null;
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
