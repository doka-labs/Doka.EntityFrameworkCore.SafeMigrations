namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Proves that a SQLite rebuild retains the declared value and generation domain.</summary>
internal static class SqliteColumnRepairProof
{
    /// <summary>Allows only nullability and default changes after the live source contract is verified.</summary>
    /// <param name="source">The exact source definition captured by the migration.</param>
    /// <param name="target">The requested target definition.</param>
    /// <returns>Whether copying existing values cannot change their declared storage or CLR domain.</returns>
    internal static bool HasLosslessShape(ExpectedColumnDefinition source, ExpectedColumnDefinition target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        // WHY: SQLite applies the target affinity while copying rows. An exact
        // source match does not prove losslessness: INTEGER -> REAL can round
        // Int64 values, and TEXT -> NUMERIC can discard their representation.
        // Generation and collation changes likewise need a separate proof;
        // rebuilding successfully and matching the target schema is not one.
        return SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(source, target)
            && Autoincrement(source) == Autoincrement(target);
    }

    /// <summary>Reads the only supported SQLite-owned generation annotation without allocating.</summary>
    private static bool Autoincrement(ExpectedColumnDefinition definition)
    {
        for (var index = 0; index < definition.ProviderAnnotations.Count; index++)
        {
            var annotation = definition.ProviderAnnotations[index];
            if (annotation.Name == "Sqlite:Autoincrement")
            {
                return annotation.Value is true;
            }
        }

        return false;
    }
}
