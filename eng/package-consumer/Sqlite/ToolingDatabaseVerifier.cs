namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.PackageConsumer;

/// <summary>Reads the exact target contract produced by the SQLite CLI and bundle probes.</summary>
internal static class ToolingDatabaseVerifier
{
    /// <summary>Rejects incomplete tooling databases without changing their application state.</summary>
    /// <param name="connection">The open database owned by one tooling probe.</param>
    /// <param name="initialMigrationId">The scaffolded initial migration identity.</param>
    /// <param name="transitionMigrationId">The scaffolded final transition identity.</param>
    internal static void Verify(
        SqliteConnection connection,
        string initialMigrationId,
        string transitionMigrationId
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialMigrationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionMigrationId);

        if (StringComparer.Ordinal.Equals(initialMigrationId, transitionMigrationId))
        {
            throw new ArgumentException("Initial and transition migration identities must differ.",
                nameof(transitionMigrationId));
        }

        // WHY: Independent predicates prevent an unrelated index or extra row
        // from compensating for missing history or an unapplied transition.
        using (var history = connection.CreateCommand())
        {
            history.CommandText = "SELECT COUNT(*) = 2 "
                + "AND COUNT(CASE WHEN MigrationId = $initial THEN 1 END) = 1 "
                + "AND COUNT(CASE WHEN MigrationId = $transition THEN 1 END) = 1 FROM __EFMigrationsHistory;";
            _ = history.Parameters.AddWithValue("$initial", initialMigrationId);
            _ = history.Parameters.AddWithValue("$transition", transitionMigrationId);
            Require(Convert.ToInt32(history.ExecuteScalar(), CultureInfo.InvariantCulture) == 1, "migration_history");
        }

        Require(Scalar(connection,
            "SELECT COUNT(*) = 4 "
            + "AND COUNT(CASE WHEN name = 'Id' AND type = 'INTEGER' AND \"notnull\" = 1 AND pk = 1 "
            + "AND hidden = 0 THEN 1 END) = 1 "
            + "AND COUNT(CASE WHEN name = 'Code' AND type = 'TEXT' AND \"notnull\" = 1 AND pk = 0 "
            + "AND hidden = 0 THEN 1 END) = 1 "
            + "AND COUNT(CASE WHEN name IN ('AddedCode', 'renamed_code') AND type = 'TEXT' "
            + "AND \"notnull\" = 0 AND pk = 0 AND hidden = 0 THEN 1 END) = 2 "
            + "FROM pragma_table_xinfo('tooling_entities');") == 1, "target_columns");

        Require(Scalar(connection,
            "SELECT EXISTS (SELECT 1 FROM pragma_index_list('tooling_entities') "
            + "WHERE name = 'IX_tooling_entities_Code' AND \"unique\" = 1 AND partial = 0) "
            + "AND (SELECT COUNT(*) = 1 AND COUNT(CASE WHEN seqno = 0 AND name = 'Code' THEN 1 END) = 1 "
            + "FROM pragma_index_info('IX_tooling_entities_Code'));") == 1, "code_index");

        Require(Scalar(connection,
            "SELECT COUNT(*) = 1 AND COUNT(CASE WHEN Id = 1 AND Code = 'baseline' "
            + "AND AddedCode IS NULL AND renamed_code IS NULL THEN 1 END) = 1 FROM tooling_entities;") == 1,
            "model_managed_data");

        Require(Scalar(connection,
            "SELECT COUNT(*) = 4 "
            + "AND COUNT(CASE WHEN name = 'Id' AND type = 'INTEGER' AND \"notnull\" = 1 AND pk = 1 "
            + "AND hidden = 0 THEN 1 END) = 1 "
            + "AND COUNT(CASE WHEN name IN ('LeftBound', 'RightBound') AND type = 'INTEGER' "
            + "AND \"notnull\" = 1 AND pk = 0 AND hidden = 0 THEN 1 END) = 2 "
            + "AND COUNT(CASE WHEN name = 'HasDescendants' AND type = 'INTEGER' "
            + "AND \"notnull\" = 1 AND pk = 0 AND hidden = 2 THEN 1 END) = 1 "
            + "FROM pragma_table_xinfo('scaffolding_generated_nodes');") == 1, "generated_columns");

        Require(Scalar(connection,
            "SELECT EXISTS (SELECT 1 FROM pragma_index_list('scaffolding_generated_nodes') "
            + "WHERE name = 'IX_scaffolding_generated_nodes_HasDescendants_LeftBound_RightBound' "
            + "AND \"unique\" = 0 AND partial = 0) "
            + "AND (SELECT COUNT(*) = 3 "
            + "AND COUNT(CASE WHEN seqno = 0 AND name = 'HasDescendants' THEN 1 END) = 1 "
            + "AND COUNT(CASE WHEN seqno = 1 AND name = 'LeftBound' THEN 1 END) = 1 "
            + "AND COUNT(CASE WHEN seqno = 2 AND name = 'RightBound' THEN 1 END) = 1 "
            + "FROM pragma_index_info('IX_scaffolding_generated_nodes_HasDescendants_LeftBound_RightBound'));") == 1,
            "generated_index");

        Require(Scalar(connection,
            "SELECT COUNT(*) = 1 FROM sqlite_schema WHERE type = 'table' "
            + "AND name = 'scaffolding_generated_nodes' AND instr(sql, 'ck_scaffolding_generated_bounds') > 0;") == 1,
            "generated_check");
        Require(Scalar(connection, "PRAGMA foreign_keys;") == 1, "foreign_key_enforcement");
        Require(Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;") == 0, "foreign_key_integrity");
        Require(Scalar(connection,
            "SELECT COUNT(*) FROM pragma_integrity_check WHERE integrity_check <> 'ok';") == 0, "database_integrity");
        Require(Scalar(connection, "SELECT COUNT(*) FROM scaffolding_generated_nodes;") == 0, "generated_initial_data");

        // WHY: Catalog names alone cannot prove that a generated CASE or CHECK
        // survived execution. Roll back synthetic positive and negative probes
        // so every CLI/bundle target remains suitable for identical replay.
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction,
            "INSERT INTO scaffolding_generated_nodes (Id, LeftBound, RightBound) VALUES "
            + "(910001, 1, 2), (910002, 1, 3), (910003, 9223372036854775806, 9223372036854775807);");
        Require(Scalar(connection,
            "SELECT COUNT(*) = 3 "
            + "AND COUNT(CASE WHEN Id = 910001 AND HasDescendants = 0 THEN 1 END) = 1 "
            + "AND COUNT(CASE WHEN Id = 910002 AND HasDescendants = 1 THEN 1 END) = 1 "
            + "AND COUNT(CASE WHEN Id = 910003 AND HasDescendants = 0 THEN 1 END) = 1 "
            + "FROM scaffolding_generated_nodes;", transaction) == 1, "generated_values");
        RequireConstraintFailure(connection, transaction,
            "INSERT INTO scaffolding_generated_nodes (Id, LeftBound, RightBound) VALUES (910004, 2, 2);",
            275, "generated_check");
        RequireConstraintFailure(connection, transaction,
            "INSERT INTO tooling_entities (Id, Code) VALUES (910005, 'baseline');", 2067, "code_uniqueness");
        transaction.Rollback();
    }

    /// <summary>Reads one scalar through the same open connection and optional owned probe transaction.</summary>
    private static int Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Executes synthetic DML inside the verifier-owned rollback boundary.</summary>
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        _ = command.ExecuteNonQuery();
    }

    /// <summary>Requires the intended native constraint error, never an unrelated SQL failure.</summary>
    private static void RequireConstraintFailure(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        int extendedErrorCode,
        string invariant
    )
    {
        try
        {
            Execute(connection, transaction, sql);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19
            && exception.SqliteExtendedErrorCode == extendedErrorCode)
        {
            return;
        }

        throw new InvalidOperationException("SQLite tooling invariant failed: " + invariant + ".");
    }

    /// <summary>Rejects one failed fact independently so unrelated state cannot compensate.</summary>
    private static void Require(bool condition, string invariant)
    {
        if (!condition)
        {
            throw new InvalidOperationException("SQLite tooling invariant failed: " + invariant + ".");
        }
    }
}
