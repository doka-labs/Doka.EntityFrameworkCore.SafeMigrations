namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Reads a bounded, immutable snapshot of the active SQLite catalog.</summary>
internal static partial class SqliteSafeMigrationCatalog
{
    private static readonly StringComparer s_identifierComparer = SqliteIdentifierComparer.Instance;

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

            // WHY: Preserve explicit fluent materialization for the ordered snapshot.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var primaryKeyName = ReadPrimaryKeyName(tableClauses);
        var primaryKeyIndex = indexes.FirstOrDefault(static value => value.Origin == "pk");
        var primaryKeyKeys = primaryKeyIndex
                ?.Keys
                .Where(static value => value.IsKey)
                .OrderBy(static value => value.Ordinal)

                // WHY: Preserve explicit fluent materialization for the ordered snapshot.
                // ReSharper disable once UseCollectionExpression
                .ToArray()
            ?? primaryKeyColumns
                .Select((column,
                    ordinal) => new SqliteIndexKeySnapshot(
                    ordinal,
                    columns[column].Ordinal,
                    column,
                    Expression: null,
                    Descending: false,
                    columns[column].Collation ?? "BINARY",
                    IsKey: true))

                // WHY: Preserve explicit fluent materialization for the ordered snapshot.
                // ReSharper disable once UseCollectionExpression
                .ToArray();

        var parsedUniques = ReadUniqueConstraints(tableClauses);
        var uniqueIndexes = indexes
            .Where(static value => value.Origin == "u")

            // WHY: Preserve explicit fluent materialization for the ordered snapshot.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var uniqueConstraints = new SqliteUniqueSnapshot[uniqueIndexes.Length];

        // WHY: Fill unique-constraint slots from the corresponding ordered catalog index entries.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < uniqueIndexes.Length; index++)
        {
            var uniqueIndex = uniqueIndexes[index];
            var uniqueColumns = uniqueIndex
                .Keys
                .Where(static value => value is { IsKey: true, Column: not null })
                .OrderBy(static value => value.Ordinal)
                .Select(static value => value.Column!)

                // WHY: Preserve explicit fluent materialization for the ordered snapshot.
                // ReSharper disable once UseCollectionExpression
                .ToArray();

            var name = parsedUniques.FirstOrDefault(value => IdentifiersEqual(value.Columns, uniqueColumns))
                ?.Name;

            uniqueConstraints[index] = new SqliteUniqueSnapshot(
                name,
                uniqueIndex.Name,
                Array.AsReadOnly(uniqueColumns),
                Array.AsReadOnly(
                    uniqueIndex
                        .Keys
                        .Where(static value => value.IsKey)
                        .OrderBy(static value => value.Ordinal)

                        // WHY: Preserve explicit fluent materialization for the ordered snapshot.
                        // ReSharper disable once UseCollectionExpression
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
}
