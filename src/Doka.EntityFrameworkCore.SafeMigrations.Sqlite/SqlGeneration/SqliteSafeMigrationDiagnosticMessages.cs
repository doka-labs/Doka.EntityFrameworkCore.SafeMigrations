namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Formats SQLite-owned rejections without changing native exception ownership.</summary>
internal static class SqliteSafeMigrationDiagnosticMessages
{
    /// <summary>Preserves the guard category and its full source-operation ordinal.</summary>
    internal static string FormatBlocked(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision,
        int operationOrdinal)
    {
        // WHY: Format only a rejection, never an accepted command's row/default
        // values or SQL text. The shared formatter bounds and escapes identity.
        return SafeMigrationDiagnosticMessages.FormatRuntimeFromAnalysis(operation, analysis, decision, operationOrdinal);
    }

    /// <summary>Describes relationship failure using metadata only, never PRAGMA row identities.</summary>
    internal static string FormatForeignKeyValidation(
        string table,
        string? principalTable,
        long? foreignKeyOrdinal,
        SqliteForeignKeySnapshot? foreignKey = null)
    {
        var owner = SafeMigrationDiagnosticMessages.BoundForDisplay(table);
        var principal = principalTable is null
            ? "unknown"
            : SafeMigrationDiagnosticMessages.BoundForDisplay(principalTable);

        var ordinal = foreignKeyOrdinal?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var name = foreignKey?.Name is { } constraintName
            ? SafeMigrationDiagnosticMessages.BoundForDisplay(constraintName)
            : "unknown";

        var columns = FormatColumns(foreignKey?.Columns);
        var principalColumns = FormatColumns(foreignKey?.PrincipalColumns);

        return "doka_sm_foreign_key_validation_failed: SQLite foreign-key validation failed; "
            + $"table='{owner}', columns=[{columns}], principalTable='{principal}', "
            + $"principalColumns=[{principalColumns}], foreignKey='{name}', foreignKeyOrdinal={ordinal}; "
            + "reason=an affected relationship violates its foreign-key contract; the rebuild cannot be committed; "
            + "next=inspect the affected relationship with read-only checks and correct orphan references "
            + "before retrying. No row keys or values are included.";
    }

    /// <summary>Limits relationship endpoints to four escaped identifiers without copying whole lists.</summary>
    private static string FormatColumns(IReadOnlyList<string>? columns)
    {
        if (columns is null)
        {
            return "unknown";
        }

        // WHY: Fixed per-member and member-count bounds keep the entire integrity
        // message below 2048 characters even for hostile metadata.
        var names = string.Join(", ", columns.Take(4).Select(
            static column => SafeMigrationDiagnosticMessages.BoundForDisplay(column)));

        return columns.Count > 4 ? names + ", ..." : names;
    }
}
