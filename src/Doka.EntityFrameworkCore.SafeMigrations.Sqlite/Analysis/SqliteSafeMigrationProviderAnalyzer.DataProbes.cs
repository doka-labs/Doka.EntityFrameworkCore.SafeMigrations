namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

internal sealed partial class SqliteSafeMigrationProviderAnalyzer
{
    /// <summary>Checks whether required-column creation would affect any existing row.</summary>
    private static bool TableHasRows(
        DbConnection connection,
        DbTransaction? transaction,
        string table
    ) => ExecuteExists(connection, transaction, $"SELECT 1 FROM {Delimit(table)} LIMIT 1;");

    /// <summary>Checks for a NULL value before a required-column transition.</summary>
    private static bool ColumnHasNulls(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        string column
    ) => ExecuteExists(
        connection,
        transaction,
        $"SELECT 1 FROM {Delimit(table)} WHERE {Delimit(column)} IS NULL LIMIT 1;");

    /// <summary>Checks whether a proposed key contains any NULL component.</summary>
    private static bool HasNullKey(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns
    ) => ExecuteExists(
        connection,
        transaction,
        $"SELECT 1 FROM {Delimit(table)} WHERE "
        + string.Join(" OR ", columns.Select(column => $"{Delimit(column)} IS NULL"))
        + " LIMIT 1;");

    /// <summary>Checks ordered key uniqueness with the requested SQLite NULL semantics.</summary>
    private static bool HasDuplicateKey(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns,
        bool excludeNulls
    )
    {
        var identifiers = string.Join(", ", columns.Select(Delimit));
        var predicate = excludeNulls
            ? " WHERE " + string.Join(" AND ", columns.Select(column => $"{Delimit(column)} IS NOT NULL"))
            : string.Empty;

        return ExecuteExists(
            connection,
            transaction,
            $"SELECT 1 FROM {Delimit(table)}{predicate} GROUP BY {identifiers} HAVING COUNT(*) > 1 LIMIT 1;");
    }

    /// <summary>Checks unique index rows using the same expressions, collations and filter.</summary>
    private bool HasDuplicateIndexKey(
        DbConnection connection,
        DbTransaction? transaction,
        ExpectedIndexDefinition definition
    )
    {
        var expressions = definition
            .Keys
            .Select(key => key.Column is not null
                ? Delimit(key.Column)
                + (key.Collation is null ? string.Empty : " COLLATE " + Delimit(key.Collation.Name))
                : key.StructuredExpression is not null
                    ? _expressionRenderer.Render(key.StructuredExpression)
                    : key.Expression
                    ?? throw new InvalidOperationException("A SQLite index key requires a column or expression."))
            .ToArray();

        var predicates = expressions
            .Select(expression => $"({expression}) IS NOT NULL")
            .ToList();
        var filter = definition.StructuredFilter is not null
            ? _expressionRenderer.Render(definition.StructuredFilter)
            : definition.Filter;

        if (filter is not null)
        {
            predicates.Add($"({filter})");
        }

        return ExecuteExists(
            connection,
            transaction,
            $"SELECT 1 FROM {Delimit(definition.Table)} WHERE {string.Join(" AND ", predicates)} "
            + $"GROUP BY {string.Join(", ", expressions)} HAVING COUNT(*) > 1 LIMIT 1;");
    }

    /// <summary>Checks whether any retained row violates the declared check expression.</summary>
    private static bool HasCheckViolations(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        string expression
    ) => ExecuteExists(
        connection,
        transaction,
        $"SELECT 1 FROM {Delimit(table)} WHERE NOT ({expression}) AND ({expression}) IS NOT NULL LIMIT 1;");

    /// <summary>Checks whether retained dependent values have a matching principal key.</summary>
    private static bool HasForeignKeyViolations(
        DbConnection connection,
        DbTransaction? transaction,
        ExpectedForeignKeyDefinition definition
    ) => HasForeignKeyViolations(
        connection,
        transaction,
        definition.Table,
        definition.Columns,
        definition.PrincipalTable,
        definition.PrincipalColumns);

    /// <summary>Checks incoming and outgoing retained rows before an owned atomic table rebuild.</summary>
    private static bool HasRetainedRebuildForeignKeyViolation(
        SqliteCatalogSnapshot snapshot,
        DbConnection connection,
        DbTransaction? transaction,
        string rebuiltTable,
        SafeMigrationOperation currentOperation,
        SqliteRebuildArtifactContract rebuildArtifacts
    )
    {
        var liveRebuiltTable = rebuildArtifacts.ResolveLiveTableName(rebuiltTable, currentOperation);
        foreach (var dependent in snapshot.Tables.Values)
        {
            foreach (var foreignKey in dependent.ForeignKeys)
            {
                if (!rebuildArtifacts.RetainsForeignKey(dependent.Name, foreignKey)
                    || (!s_identifierComparer.Equals(dependent.Name, liveRebuiltTable)
                        && !s_identifierComparer.Equals(foreignKey.PrincipalTable, liveRebuiltTable)))
                {
                    continue;
                }

                if (!snapshot.Tables.TryGetValue(foreignKey.PrincipalTable, out var principal))
                {
                    if (HasNonNullForeignKeyValues(connection, transaction, dependent.Name, foreignKey.Columns))
                    {
                        return true;
                    }

                    continue;
                }

                var principalColumns = foreignKey.PrincipalColumns.Any(static column => column.Length > 0)
                    ? foreignKey.PrincipalColumns
                    : principal.PrimaryKeyColumns;

                if (principalColumns.Count != foreignKey.Columns.Count
                    || HasForeignKeyViolations(
                        connection,
                        transaction,
                        dependent.Name,
                        foreignKey.Columns,
                        foreignKey.PrincipalTable,
                        principalColumns))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Checks whether retained dependent values have a matching principal key.</summary>
    private static bool HasForeignKeyViolations(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns,
        string principalTable,
        IReadOnlyList<string> principalColumns
    )
    {
        var join = new string[columns.Count];
        // WHY: Keep dependent-column null tests aligned with the declared FK key positions.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < columns.Count; index++)
        {
            join[index] = $"p.{Delimit(principalColumns[index])} IS d.{Delimit(columns[index])}";
        }

        var nonNull = string.Join(" AND ", columns.Select(column => $"d.{Delimit(column)} IS NOT NULL"));

        return ExecuteExists(
            connection,
            transaction,
            $"SELECT 1 FROM {Delimit(table)} AS d "
            + $"LEFT JOIN {Delimit(principalTable)} AS p ON {string.Join(" AND ", join)} "
            + $"WHERE {nonNull} AND p.{Delimit(principalColumns[0])} IS NULL LIMIT 1;");
    }

    /// <summary>Checks whether a missing principal would orphan any fully populated dependent key.</summary>
    private static bool HasNonNullForeignKeyValues(
        DbConnection connection,
        DbTransaction? transaction,
        string table,
        IReadOnlyList<string> columns
    )
    {
        var nonNull = string.Join(" AND ", columns.Select(column => $"{Delimit(column)} IS NOT NULL"));

        return ExecuteExists(connection, transaction, $"SELECT 1 FROM {Delimit(table)} WHERE {nonNull} LIMIT 1;");
    }

    /// <summary>Runs a bounded existence probe on the caller's connection and transaction.</summary>
    private static bool ExecuteExists(
        DbConnection connection,
        DbTransaction? transaction,
        string sql
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        return command.ExecuteScalar() is not null;
    }
}
