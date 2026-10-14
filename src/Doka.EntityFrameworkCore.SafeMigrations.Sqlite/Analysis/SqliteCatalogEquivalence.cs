namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Compares SQLite catalog objects by their physical semantics.</summary>
internal static class SqliteCatalogEquivalence
{
    private static readonly StringComparer s_identifierComparer = SqliteIdentifierComparer.Instance;

    /// <summary>Determines whether a catalog foreign key matches an expected definition.</summary>
    public static bool ForeignKeyMatches(
        SqliteCatalogSnapshot snapshot,
        SqliteForeignKeySnapshot actual,
        string principalTable,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> principalColumns,
        ReferentialAction onUpdate,
        ReferentialAction onDelete
    )
    {
        var actualPrincipalColumns = actual.PrincipalColumns.Any(static column => column.Length > 0)
            ? actual.PrincipalColumns
            : snapshot.Tables.TryGetValue(actual.PrincipalTable, out var principal)
                ? principal.PrimaryKeyColumns
                : actual.PrincipalColumns;

        return s_identifierComparer.Equals(actual.PrincipalTable, principalTable)
            && IdentifiersEqual(actual.Columns, columns)
            && IdentifiersEqual(actualPrincipalColumns, principalColumns)
            && actual.OnUpdate == onUpdate
            && actual.OnDelete == onDelete;
    }

    /// <summary>Compares ordered SQLite identifier sequences.</summary>
    public static bool IdentifiersEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var ordinal = 0; ordinal < left.Count; ordinal++)
        {
            if (!s_identifierComparer.Equals(left[ordinal], right[ordinal]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Proves a complete physical key with its columns' declared equality semantics.</summary>
    /// <param name="table">The native owner containing declared column collations.</param>
    /// <param name="keys">The ordered physical key parts, including any auxiliary non-key parts.</param>
    /// <param name="columns">The complete requested column-key order.</param>
    /// <param name="requireDefaultSortOrder">Whether a plain constraint also requires ascending key parts.</param>
    /// <returns>Whether the captured native key satisfies the complete requested semantics.</returns>
    public static bool KeyMatchesDeclaredColumns(
        SqliteTableSnapshot table,
        IReadOnlyList<SqliteIndexKeySnapshot> keys,
        IReadOnlyList<string> columns,
        bool requireDefaultSortOrder = false)
    {
        var ordinal = 0;
        foreach (var key in keys)
        {
            if (!key.IsKey)
            {
                continue;
            }

            if (ordinal >= columns.Count || key.Ordinal != ordinal
                || key.Column is null || key.Expression is not null
                || (requireDefaultSortOrder && key.Descending)
                || !s_identifierComparer.Equals(key.Column, columns[ordinal])
                || !table.Columns.TryGetValue(key.Column, out var column)
                || !s_identifierComparer.Equals(key.Collation, column.Collation ?? "BINARY"))
            {
                return false;
            }

            ordinal++;
        }

        // WHY: A descending unique key can be a native FK parent, whereas a
        // plain authored PK/unique constraint has default ascending shape.
        // Both require the native column-declared equality and complete key.
        return ordinal == columns.Count;
    }
}
