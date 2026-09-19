namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Reads a bounded, immutable snapshot of the active SQLite catalog.</summary>
internal static class SqliteSafeMigrationCatalog
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

    private static readonly Regex s_namedForeignKeyClausePattern = new(
        @"^\s*CONSTRAINT\s+" + IdentifierPattern("name") + @"\s+FOREIGN\s+KEY\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_referencePattern = new(
        @"\bREFERENCES\s+" + IdentifierPattern("table") + @"(?:\s*\()?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex s_uniqueConstraintPattern = new(
        @"^\s*(?:CONSTRAINT\s+" + IdentifierPattern("name") + @"\s+)?UNIQUE\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Reads the catalog through the supplied connection and transaction.</summary>
    public static SqliteCatalogSnapshot Read(
        DbConnection connection,
        DbTransaction? transaction
    )
    {
        ArgumentNullException.ThrowIfNull(connection);

        var version = ReadScalarString(connection, transaction, "SELECT sqlite_version();");
        var foreignKeysEnabled = ReadScalarInt(connection, transaction, "PRAGMA foreign_keys;") != 0;
        var legacyAlterTableEnabled = ReadScalarInt(connection, transaction, "PRAGMA legacy_alter_table;") != 0;
        var schemaObjects = ReadSchemaObjects(connection, transaction);
        var indexSql = schemaObjects
            .Where(static value => value.Type == "index")
            .ToDictionary(static value => value.Name, static value => value.Sql, s_identifierComparer);

        var tables = new Dictionary<string, SqliteTableSnapshot>(s_identifierComparer);

        foreach (var schemaObject in schemaObjects.Where(static value => value.Type == "table"))
        {
            tables.Add(schemaObject.Name, ReadTable(connection, transaction, schemaObject, indexSql));
        }

        var otherObjects = schemaObjects
            .Where(static value => value.Type != "table")
            .ToArray();

        return new SqliteCatalogSnapshot(
            version,
            foreignKeysEnabled,
            legacyAlterTableEnabled,
            tables,
            Array.AsReadOnly(otherObjects));
    }

    private static SqliteTableSnapshot ReadTable(
        DbConnection connection,
        DbTransaction? transaction,
        SqliteSchemaObjectSnapshot schemaObject,
        IReadOnlyDictionary<string, string> indexSql
    )
    {
        var tableSql = RemoveComments(schemaObject.Sql);
        var tableClauses = ReadTableClauses(tableSql);
        var columnClauses = ReadColumnClauses(tableClauses);
        var columns = ReadColumns(connection, transaction, schemaObject.Name, columnClauses);
        var indexes = ReadIndexes(connection, transaction, schemaObject.Name, indexSql);
        var namedForeignKeys = ReadNamedForeignKeys(tableClauses);
        var foreignKeys = ReadForeignKeys(connection, transaction, schemaObject.Name, namedForeignKeys);
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
                uniqueColumns,
                uniqueIndex
                    .Keys
                    .Where(static value => value.IsKey)
                    .OrderBy(static value => value.Ordinal)
                    .ToArray());
        }

        return new SqliteTableSnapshot(
            schemaObject.Name,
            tableSql,
            columns,
            indexes,
            foreignKeys,
            checks,
            Array.AsReadOnly(primaryKeyColumns),
            Array.AsReadOnly(primaryKeyKeys),
            primaryKeyName,
            Array.AsReadOnly(uniqueConstraints),
            HasUnmodeledConstraintOptions(tableClauses),
            HasTableOption(tableSql, "STRICT"),
            HasTableOption(tableSql, "WITHOUT ROWID"));
    }

    private static Dictionary<string, SqliteColumnSnapshot> ReadColumns(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        Dictionary<string, string> columnClauses
    )
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT cid, name, type, \"notnull\", dflt_value, pk, hidden "
            + "FROM pragma_table_xinfo($table, 'main') ORDER BY cid;");

        AddParameter(command, "$table", table);

        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, SqliteColumnSnapshot>(s_identifierComparer);
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var hiddenKind = reader.GetInt32(6);
            _ = columnClauses.TryGetValue(name, out var columnSql);
            var collation = ReadCollation(columnSql);
            var generatedSql = ReadGeneratedExpression(columnSql);

            result.Add(
                name,
                new SqliteColumnSnapshot(
                    reader.GetInt32(0),
                    name,
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.GetInt32(3) == 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5),
                    hiddenKind,
                    collation,
                    generatedSql,
                    hiddenKind == 3,
                    columnSql is not null && SqliteSqlIdentifierScanner.ContainsKeyword(columnSql, "AUTOINCREMENT")));
        }

        return result;
    }

    private static ReadOnlyCollection<SqliteIndexSnapshot> ReadIndexes(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyDictionary<string, string> indexSql
    )
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT name, \"unique\", origin, partial FROM pragma_index_list($table, 'main') ORDER BY seq;");

        AddParameter(command, "$table", table);

        using var reader = command.ExecuteReader();
        var headers = new List<(string Name, bool Unique, string Origin, bool Partial)>();
        while (reader.Read())
        {
            headers.Add((reader.GetString(0), reader.GetInt32(1) != 0, reader.GetString(2), reader.GetInt32(3) != 0));
        }

        var result = new List<SqliteIndexSnapshot>(headers.Count);
        foreach (var header in headers)
        {
            var sql = indexSql.TryGetValue(header.Name, out var capturedSql) ? capturedSql : string.Empty;
            var keys = ReadIndexKeys(connection, transaction, header.Name, sql);
            result.Add(
                new SqliteIndexSnapshot(
                    header.Name,
                    header.Unique,
                    header.Origin,
                    header.Partial,
                    keys,
                    ReadIndexFilter(sql),
                    sql));
        }

        return result.AsReadOnly();
    }

    private static ReadOnlyCollection<SqliteIndexKeySnapshot> ReadIndexKeys(
        DbConnection connection,
        DbTransaction? transaction,
        string index,
        string indexSql
    )
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT seqno, cid, name, \"desc\", coll, \"key\" "
            + "FROM pragma_index_xinfo($index, 'main') ORDER BY seqno;");

        AddParameter(command, "$index", index);

        var keyClauses = ReadIndexKeyClauses(indexSql);
        using var reader = command.ExecuteReader();
        var result = new List<SqliteIndexKeySnapshot>();
        while (reader.Read())
        {
            var ordinal = reader.GetInt32(0);
            var isKey = reader.GetInt32(5) != 0;
            var column = reader.IsDBNull(2) ? null : reader.GetString(2);
            result.Add(
                new SqliteIndexKeySnapshot(
                    ordinal,
                    reader.GetInt32(1),
                    column,
                    isKey && column is null && ordinal < keyClauses.Length
                        ? ReadIndexExpression(keyClauses[ordinal])
                        : null,
                    reader.GetInt32(3) != 0,
                    reader.IsDBNull(4) ? "BINARY" : reader.GetString(4),
                    isKey));
        }

        return result.AsReadOnly();
    }

    private static ReadOnlyCollection<SqliteForeignKeySnapshot> ReadForeignKeys(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<NamedForeignKey> namedForeignKeys
    )
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete, \"match\" "
            + "FROM pragma_foreign_key_list($table, 'main') ORDER BY id, seq;");

        AddParameter(command, "$table", table);

        using var reader = command.ExecuteReader();
        var rows = new List<(int Id, int Seq, string PrincipalTable, string Column, string PrincipalColumn,
            string OnUpdate, string OnDelete, string Match)>();

        while (reader.Read())
        {
            rows.Add(
                (reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7)));
        }

        var groups = rows
            .GroupBy(static value => value.Id)
            .OrderBy(static value => value.Key)
            .ToArray();

        var result = new List<SqliteForeignKeySnapshot>(groups.Length);
        for (var index = 0; index < groups.Length; index++)
        {
            var group = groups[index]
                .OrderBy(static value => value.Seq)
                .ToArray();

            var columns = group
                .Select(static value => value.Column)
                .ToArray();

            var principalColumns = group
                .Select(static value => value.PrincipalColumn)
                .ToArray();

            var name = namedForeignKeys.FirstOrDefault(value =>
                    s_identifierComparer.Equals(value.PrincipalTable, group[0].PrincipalTable)
                    && IdentifiersEqual(value.Columns, columns)
                    && (value.PrincipalColumns.Length == 0
                        || IdentifiersEqual(value.PrincipalColumns, principalColumns)))
                ?.Name;

            result.Add(
                new SqliteForeignKeySnapshot(
                    group[0].Id,
                    name,
                    group[0].PrincipalTable,
                    columns,
                    principalColumns,
                    ParseReferentialAction(group[0].OnUpdate),
                    ParseReferentialAction(group[0].OnDelete),
                    group[0].Match));
        }

        return result.AsReadOnly();
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

    private static ReadOnlyCollection<SqliteSchemaObjectSnapshot> ReadSchemaObjects(
        DbConnection connection,
        DbTransaction? transaction
    )
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT type, name, tbl_name, COALESCE(sql, '') FROM main.sqlite_schema "
            + "WHERE name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY type, name;");

        using var reader = command.ExecuteReader();
        var result = new List<SqliteSchemaObjectSnapshot>();
        while (reader.Read())
        {
            result.Add(
                new SqliteSchemaObjectSnapshot(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
        }

        return result.AsReadOnly();
    }

    private static string ReadScalarString(
        DbConnection connection,
        DbTransaction? transaction,
        string sql
    )
    {
        using var command = CreateCommand(connection, transaction, sql);

        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("SQLite returned no scalar catalog value.");
    }

    private static int ReadScalarInt(
        DbConnection connection,
        DbTransaction? transaction,
        string sql
    )
    {
        using var command = CreateCommand(connection, transaction, sql);

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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

    private static void AddParameter(
        DbCommand command,
        string name,
        object value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
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

    private static ReadOnlyCollection<NamedForeignKey> ReadNamedForeignKeys(
        IReadOnlyList<string> tableClauses
    )
    {
        var result = new List<NamedForeignKey>();
        foreach (var clause in tableClauses)
        {
            var match = s_namedForeignKeyClausePattern.Match(clause);
            if (!match.Success)
            {
                continue;
            }

            var columns = ReadIdentifierList(clause, match.Index + match.Length - 1);
            var columnsEnd = FindBalancedParenthesisEnd(clause, match.Index + match.Length - 1);
            var reference = s_referencePattern.Match(clause[(columnsEnd + 1)..]);
            if (!reference.Success)
            {
                continue;
            }

            var principalTable = ReadName(reference, "table");
            var referenceTextOffset = columnsEnd + 1;
            var principalOpen = reference.Value.LastIndexOf('(');
            var principalColumns = principalOpen < 0
                ? []
                : ReadIdentifierList(clause, referenceTextOffset + reference.Index + principalOpen);

            result.Add(new NamedForeignKey(ReadName(match, "name"), columns, principalTable, principalColumns));
        }

        return result.AsReadOnly();
    }

    private static string[] ReadTableClauses(
        string tableSql
    )
    {
        var open = tableSql.IndexOf('(');

        return open < 0
            ? []
            : SplitTopLevel(tableSql[(open + 1)..])
                .ToArray();
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
    ) => "(?:\"(?<"
        + group
        + ">(?:\"\"|[^\"])*)\""
        + "|\\[(?<"
        + group
        + "Bracket>[^]]+)\\]"
        + "|`(?<"
        + group
        + "Backtick>(?:``|[^`])*)`"
        + "|(?<"
        + group
        + "Bare>[A-Za-z_][A-Za-z0-9_]*))";

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
                    : match.Groups[group + "Bare"].Value;

        return value;
    }

    private static bool HasName(
        Match match,
        string group
    ) => match.Groups[group].Success
        || match.Groups[group + "Bracket"].Success
        || match.Groups[group + "Backtick"].Success
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
                return sql[(openParenthesis + 1)..index]
                    .Trim();
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
                    yield return sql[start..index]
                        .Trim();
                    yield break;
                }

                depth--;
            }
            else if (character == ','
                     && depth == 0)
            {
                yield return sql[start..index]
                    .Trim();
                start = index + 1;
            }
        }

        if (start < sql.Length)
        {
            yield return sql[start..].Trim();
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

        if (clause[0] == '\"')
        {
            return ReadQuotedIdentifier(clause, '\"');
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
            ? sql[(where + "WHERE".Length)..]
                .Trim()
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
    ) => left.Length == right.Length
        && left
            .Zip(right)
            .All(pair => s_identifierComparer.Equals(pair.First, pair.Second));

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

    private sealed record NamedForeignKey(
        string Name,
        string[] Columns,
        string PrincipalTable,
        string[] PrincipalColumns
    );
}
