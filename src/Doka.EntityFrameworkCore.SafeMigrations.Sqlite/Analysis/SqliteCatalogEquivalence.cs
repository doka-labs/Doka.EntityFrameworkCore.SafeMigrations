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
}
