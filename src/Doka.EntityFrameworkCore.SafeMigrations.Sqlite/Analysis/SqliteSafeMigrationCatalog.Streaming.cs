namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal static partial class SqliteSafeMigrationCatalog
{
    private const string MainTablesPredicate = "t.type = 'table' AND t.name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ";

    private static (string Version, bool ForeignKeysEnabled, bool LegacyAlterTableEnabled) ReadSettings(
        DbConnection connection,
        DbTransaction? transaction
    )
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT sqlite_version(), f.foreign_keys, l.legacy_alter_table "
            + "FROM pragma_foreign_keys() AS f CROSS JOIN pragma_legacy_alter_table() AS l;");

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw InvalidMetadata();
        }

        var result = (reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt32(2) != 0);
        if (reader.Read())
        {
            throw InvalidMetadata();
        }

        return result;
    }

    private static ReadOnlyCollection<SqliteSchemaObjectSnapshot> ReadSchemaObjects(
        DbConnection connection,
        DbTransaction? transaction,
        Dictionary<string, TableBuilder> tables
    )
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT type, name, tbl_name, COALESCE(sql, '') FROM main.sqlite_schema "
            + "WHERE name NOT LIKE 'sqlite\\_%' ESCAPE '\\' "
            + "OR (type = 'index' AND tbl_name NOT LIKE 'sqlite\\_%' ESCAPE '\\') "
            + "ORDER BY type = 'table' DESC, type, name;");

        using var reader = command.ExecuteReader();
        var result = new List<SqliteSchemaObjectSnapshot>();
        while (reader.Read())
        {
            var type = reader.GetString(0);
            var name = reader.GetString(1);
            var owner = reader.GetString(2);
            var sql = reader.GetString(3);
            if (type == "table")
            {
                if (!s_identifierComparer.Equals(name, owner)
                    || sql.Length == 0
                    || !tables.TryAdd(name, new TableBuilder(name, sql)))
                {
                    throw InvalidMetadata();
                }
            }
            else
            {
                if (type == "index"
                    && (!tables.TryGetValue(owner, out var table) || !table.ExpectedIndexNames.Add(name)))
                {
                    throw InvalidMetadata();
                }

                // WHY: Automatic indexes are ownership evidence, not new
                // rebuild blockers in the existing non-table object contract.
                if (!name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(new SqliteSchemaObjectSnapshot(type, name, owner, sql));
                }
            }
        }

        return result.AsReadOnly();
    }

    private static void ReadColumns(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyDictionary<string, TableBuilder> tables
    )
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT t.name, c.cid, c.name, c.type, c.\"notnull\", c.dflt_value, c.pk, c.hidden "
            + "FROM main.sqlite_schema AS t LEFT JOIN pragma_table_xinfo(t.name, 'main') AS c ON 1 = 1 "
            + "WHERE " + MainTablesPredicate + "ORDER BY t.name, c.cid;");

        using var reader = command.ExecuteReader();
        var seen = new HashSet<string>(s_identifierComparer);
        while (reader.Read())
        {
            var table = ReadOwner(reader, tables, seen);
            if (reader.IsDBNull(1)
                || reader.GetInt32(1) != table.Columns.Count)
            {
                throw InvalidMetadata();
            }

            var name = reader.GetString(2);
            var hiddenKind = reader.GetInt32(7);
            _ = table.ColumnClauses.TryGetValue(name, out var columnSql);
            var column = new SqliteColumnSnapshot(
                reader.GetInt32(1),
                name,
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.GetInt32(4) == 0,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                hiddenKind,
                ReadCollation(columnSql),
                ReadGeneratedExpression(columnSql),
                hiddenKind == 3,
                columnSql is not null && SqliteSqlIdentifierScanner.ContainsKeyword(columnSql, "AUTOINCREMENT"));

            if (!table.Columns.TryAdd(name, column))
            {
                throw InvalidMetadata();
            }
        }

        ValidateOwners(tables, seen);
    }

    private static void ReadIndexes(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyDictionary<string, TableBuilder> tables
    )
    {
        // WHY: LEFT joins retain ownership evidence for tables without indexes
        // and expose an index whose key metadata unexpectedly disappeared.
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT t.name, i.seq, i.name, i.\"unique\", i.origin, i.partial, s.name, s.tbl_name, s.sql, "
            + "k.seqno, k.cid, k.name, k.\"desc\", k.coll, k.\"key\" "
            + "FROM main.sqlite_schema AS t LEFT JOIN pragma_index_list(t.name, 'main') AS i ON 1 = 1 "
            + "LEFT JOIN main.sqlite_schema AS s ON s.type = 'index' AND s.name = i.name "
            + "LEFT JOIN pragma_index_xinfo(i.name, 'main') AS k ON 1 = 1 "
            + "WHERE " + MainTablesPredicate + "ORDER BY t.name, i.seq, k.seqno;");

        using var reader = command.ExecuteReader();
        var seen = new HashSet<string>(s_identifierComparer);
        TableBuilder? currentTable = null;
        IndexBuilder? currentIndex = null;
        while (reader.Read())
        {
            var table = ReadOwner(reader, tables, seen);
            if (reader.IsDBNull(1))
            {
                if (table.Indexes.Count != 0)
                {
                    throw InvalidMetadata();
                }

                continue;
            }

            var name = reader.GetString(2);
            var sequence = reader.GetInt32(1);
            if (currentTable != table
                || currentIndex is null
                || currentIndex.Sequence != sequence)
            {
                PublishIndex(currentTable, currentIndex);
                if (sequence < 0
                    || (currentTable == table && currentIndex is not null && sequence <= currentIndex.Sequence)
                    || (reader.IsDBNull(6)
                        && (reader.GetString(4) is "c" or "u"
                            || (reader.GetString(4) == "pk" && !HasTableOption(table.Sql, "WITHOUT ROWID"))))
                    || (!reader.IsDBNull(6) && !s_identifierComparer.Equals(reader.GetString(7), table.Name)))
                {
                    throw InvalidMetadata();
                }

                currentTable = table;
                currentIndex = new IndexBuilder(
                    sequence,
                    name,
                    reader.GetInt32(3) != 0,
                    reader.GetString(4),
                    reader.GetInt32(5) != 0,
                    reader.IsDBNull(8) ? string.Empty : reader.GetString(8));
            }

            if (!s_identifierComparer.Equals(currentIndex.Name, name)
                || currentIndex.Unique != (reader.GetInt32(3) != 0)
                || currentIndex.Origin != reader.GetString(4)
                || currentIndex.Partial != (reader.GetInt32(5) != 0)
                || (!reader.IsDBNull(6) && !s_identifierComparer.Equals(reader.GetString(7), table.Name))
                || currentIndex.Sql != (reader.IsDBNull(8) ? string.Empty : reader.GetString(8))
                || reader.IsDBNull(9)
                || reader.GetInt32(9) != currentIndex.Keys.Count)
            {
                throw InvalidMetadata();
            }

            var ordinal = reader.GetInt32(9);
            var columnId = reader.GetInt32(10);
            var column = reader.IsDBNull(11) ? null : reader.GetString(11);
            var isKey = reader.GetInt32(14) != 0;
            if (column is not null
                && (!table.Columns.TryGetValue(column, out var ownedColumn) || ownedColumn.Ordinal != columnId))
            {
                throw InvalidMetadata();
            }

            currentIndex.Keys.Add(
                new SqliteIndexKeySnapshot(
                    ordinal,
                    columnId,
                    column,
                    isKey && column is null && ordinal < currentIndex.KeyClauses.Length
                        ? ReadIndexExpression(currentIndex.KeyClauses[ordinal])
                        : null,
                    reader.GetInt32(12) != 0,
                    reader.IsDBNull(13) ? "BINARY" : reader.GetString(13),
                    isKey));
        }

        PublishIndex(currentTable, currentIndex);
        ValidateOwners(tables, seen);
        foreach (var table in tables.Values)
        {
            // WHY: WITHOUT ROWID primary keys have no sqlite_schema index row;
            // their physical primary-key index still must be present.
            if (!table.ExpectedIndexNames.IsSubsetOf(table.IndexNames)
                || (HasTableOption(table.Sql, "WITHOUT ROWID")
                    && !table.Indexes.Any(static index => index.Origin == "pk")))
            {
                throw InvalidMetadata();
            }
        }
    }

    private static void PublishIndex(
        TableBuilder? table,
        IndexBuilder? index
    )
    {
        if (table is null || index is null)
        {
            return;
        }

        if (index.Keys.Count == 0
            || !table.IndexNames.Add(index.Name))
        {
            throw InvalidMetadata();
        }

        table.Indexes.Add(
            new SqliteIndexSnapshot(
                index.Name,
                index.Unique,
                index.Origin,
                index.Partial,
                index.Keys.AsReadOnly(),
                ReadIndexFilter(index.ParsedSql),
                index.Sql));
    }

    private static void ReadForeignKeys(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyDictionary<string, TableBuilder> tables
    )
    {
        using var command = CreateCommand(
            connection,
            transaction,
            "SELECT t.name, f.id, f.seq, f.\"table\", f.\"from\", f.\"to\", "
            + "f.on_update, f.on_delete, f.\"match\" "
            + "FROM main.sqlite_schema AS t LEFT JOIN pragma_foreign_key_list(t.name, 'main') AS f ON 1 = 1 "
            + "WHERE " + MainTablesPredicate + "ORDER BY t.name, f.id, f.seq;");

        using var reader = command.ExecuteReader();
        var seen = new HashSet<string>(s_identifierComparer);
        TableBuilder? currentTable = null;
        ForeignKeyBuilder? currentForeignKey = null;
        while (reader.Read())
        {
            var table = ReadOwner(reader, tables, seen);
            if (reader.IsDBNull(1))
            {
                if (table.ForeignKeys.Count != 0)
                {
                    throw InvalidMetadata();
                }

                continue;
            }

            var id = reader.GetInt32(1);
            if (currentTable != table
                || currentForeignKey is null
                || currentForeignKey.Id != id)
            {
                PublishForeignKey(currentTable, currentForeignKey);
                if (id < 0
                    || (currentTable == table && currentForeignKey is not null && id <= currentForeignKey.Id))
                {
                    throw InvalidMetadata();
                }

                currentTable = table;
                currentForeignKey = new ForeignKeyBuilder(
                    id,
                    reader.GetString(3),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8));
            }

            var column = reader.GetString(4);
            if (reader.GetInt32(2) != currentForeignKey.Columns.Count
                || !s_identifierComparer.Equals(currentForeignKey.PrincipalTable, reader.GetString(3))
                || currentForeignKey.OnUpdate != reader.GetString(6)
                || currentForeignKey.OnDelete != reader.GetString(7)
                || currentForeignKey.Match != reader.GetString(8)
                || !table.Columns.ContainsKey(column))
            {
                throw InvalidMetadata();
            }

            currentForeignKey.Columns.Add(column);
            currentForeignKey.PrincipalColumns.Add(reader.IsDBNull(5) ? string.Empty : reader.GetString(5));
        }

        PublishForeignKey(currentTable, currentForeignKey);
        ValidateOwners(tables, seen);
        foreach (var table in tables.Values)
        {
            if (table.MatchedForeignKeys.Count != table.DeclaredForeignKeys.Count)
            {
                throw InvalidMetadata();
            }
        }
    }

    private static void PublishForeignKey(
        TableBuilder? table,
        ForeignKeyBuilder? foreignKey
    )
    {
        if (table is null || foreignKey is null)
        {
            return;
        }

        var columns = foreignKey.Columns.ToArray();
        var principalColumns = foreignKey.PrincipalColumns.ToArray();
        var onUpdate = ParseReferentialAction(foreignKey.OnUpdate);
        var onDelete = ParseReferentialAction(foreignKey.OnDelete);

        // WHY: SQLite assigns foreign-key IDs in reverse declaration order.
        // Consume each declaration once, including unnamed and repeated keys.
        var declaration = table.DeclaredForeignKeys.LastOrDefault(value =>
                !table.MatchedForeignKeys.Contains(value)
                && s_identifierComparer.Equals(value.PrincipalTable, foreignKey.PrincipalTable)
                && IdentifiersEqual(value.Columns, columns)
                && (value.PrincipalColumns.Length == 0
                    ? principalColumns.All(static column => column.Length == 0)
                    : IdentifiersEqual(value.PrincipalColumns, principalColumns))
                && value.OnUpdate == onUpdate
                && value.OnDelete == onDelete);

        if (declaration is null || !table.MatchedForeignKeys.Add(declaration))
        {
            throw InvalidMetadata();
        }

        table.ForeignKeys.Add(
            new SqliteForeignKeySnapshot(
                foreignKey.Id,
                declaration.Name,
                foreignKey.PrincipalTable,
                Array.AsReadOnly(columns),
                Array.AsReadOnly(principalColumns),
                onUpdate,
                onDelete,
                foreignKey.Match));
    }

    private static TableBuilder ReadOwner(
        DbDataReader reader,
        IReadOnlyDictionary<string, TableBuilder> tables,
        HashSet<string> seen
    )
    {
        var name = reader.GetString(0);
        if (!tables.TryGetValue(name, out var table))
        {
            throw InvalidMetadata();
        }

        _ = seen.Add(name);

        return table;
    }

    private static void ValidateOwners(
        IReadOnlyDictionary<string, TableBuilder> tables,
        HashSet<string> seen
    )
    {
        if (seen.Count != tables.Count)
        {
            throw InvalidMetadata();
        }
    }

    private static InvalidOperationException InvalidMetadata() =>
        new("SQLite returned missing, inconsistent, or unowned catalog metadata.");

    private sealed class TableBuilder
    {
        public TableBuilder(
            string name,
            string sql
        )
        {
            Name = name;
            Sql = RemoveComments(sql);
            Clauses = ReadTableClauses(Sql);
            ColumnClauses = ReadColumnClauses(Clauses);
            // WHY: SQLite passes virtual-table module arguments to the module;
            // keywords in those arguments are not ordinary column constraints.
            DeclaredForeignKeys = s_virtualTableDefinitionPattern.IsMatch(Sql)
                ? Array.AsReadOnly(Array.Empty<ParsedForeignKey>())
                : ReadDeclaredForeignKeys(Clauses);
        }

        public string Name { get; }

        public string Sql { get; }

        public string[] Clauses { get; }

        public Dictionary<string, string> ColumnClauses { get; }

        public ReadOnlyCollection<ParsedForeignKey> DeclaredForeignKeys { get; }

        public HashSet<ParsedForeignKey> MatchedForeignKeys { get; } = [];

        public Dictionary<string, SqliteColumnSnapshot> Columns { get; } = new(s_identifierComparer);

        public List<SqliteIndexSnapshot> Indexes { get; } = [];

        public HashSet<string> IndexNames { get; } = new(s_identifierComparer);

        public HashSet<string> ExpectedIndexNames { get; } = new(s_identifierComparer);

        public List<SqliteForeignKeySnapshot> ForeignKeys { get; } = [];
    }

    private sealed class IndexBuilder
    {
        public IndexBuilder(
            int sequence,
            string name,
            bool unique,
            string origin,
            bool partial,
            string sql
        )
        {
            Sequence = sequence;
            Name = name;
            Unique = unique;
            Origin = origin;
            Partial = partial;
            Sql = sql;
            ParsedSql = RemoveComments(sql);
            KeyClauses = ReadIndexKeyClauses(ParsedSql);
        }

        public int Sequence { get; }

        public string Name { get; }

        public bool Unique { get; }

        public string Origin { get; }

        public bool Partial { get; }

        public string Sql { get; }

        public string ParsedSql { get; }

        public string[] KeyClauses { get; }

        public List<SqliteIndexKeySnapshot> Keys { get; } = [];
    }

    private sealed class ForeignKeyBuilder
    {
        public ForeignKeyBuilder(
            int id,
            string principalTable,
            string onUpdate,
            string onDelete,
            string match
        )
        {
            Id = id;
            PrincipalTable = principalTable;
            OnUpdate = onUpdate;
            OnDelete = onDelete;
            Match = match;
        }

        public int Id { get; }

        public string PrincipalTable { get; }

        public string OnUpdate { get; }

        public string OnDelete { get; }

        public string Match { get; }

        public List<string> Columns { get; } = [];

        public List<string> PrincipalColumns { get; } = [];
    }
}
