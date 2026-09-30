namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Renders SQL Server's physical cascading-action prerequisites.</summary>
internal static class SqlServerForeignKeySafety
{
    /// <summary>Gets the local result produced by the catalog topology preamble.</summary>
    public const string GraphSafePredicate = "@doka_fk_graph_safe = 1";

    /// <summary>Determines whether either referential action mutates dependent rows.</summary>
    /// <param name="definition">The foreign-key contract.</param>
    /// <returns>Whether cascade-topology validation is necessary.</returns>
    public static bool HasCascadingAction(
        ExpectedForeignKeyDefinition definition
    )
        => IsCascading(definition.OnDelete) || IsCascading(definition.OnUpdate);

    /// <summary>Renders catalog-only trigger, rowversion, and SET DEFAULT prerequisites.</summary>
    /// <param name="definition">The foreign-key contract.</param>
    /// <param name="literal">The provider string-literal renderer.</param>
    /// <returns>A Boolean predicate evaluated before dependent-row probes.</returns>
    public static string BuildPhysicalPredicate(
        ExpectedForeignKeyDefinition definition,
        Func<string, string> literal
    )
    {
        var dependent = TableId(definition.Table, definition.Schema, literal);
        var principal = TableId(definition.PrincipalTable, definition.PrincipalSchema, literal);
        var predicates = new List<string>(4);
        if (HasCascadingAction(definition))
        {
            var dependentColumns = string.Join(", ", definition.Columns.Select(literal));
            var principalColumns = string.Join(", ", definition.PrincipalColumns.Select(literal));

            predicates.Add("NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.system_type_id = 189 "
                + $"AND ((c.object_id = {dependent} AND c.name IN ({dependentColumns})) "
                + $"OR (c.object_id = {principal} AND c.name IN ({principalColumns}))))");
        }

        if (definition.OnDelete == ReferentialAction.Cascade)
        {
            predicates.Add(TriggerAbsent(dependent, "DELETE"));
        }

        if (IsCascading(definition.OnUpdate)
            || definition.OnDelete is ReferentialAction.SetNull or ReferentialAction.SetDefault)
        {
            // WHY: SET NULL/DEFAULT performs an UPDATE on the dependent table,
            // including when its initiating principal action was a DELETE.
            predicates.Add(TriggerAbsent(dependent, "UPDATE"));
        }

        if (definition.OnDelete == ReferentialAction.SetDefault
            || definition.OnUpdate == ReferentialAction.SetDefault)
        {
            var columns = string.Join(", ", definition.Columns.Select(literal));

            predicates.Add($"NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = {dependent} "
                + $"AND c.name IN ({columns}) AND c.is_nullable = 0 AND c.default_object_id = 0)");
        }

        return predicates.Count == 0 ? "1 = 1" : string.Join(" AND ", predicates);
    }

    /// <summary>Renders an ordered graph check against current live FKs and the candidate additions.</summary>
    /// <param name="definitions">The standalone or inline foreign keys being introduced.</param>
    /// <param name="literal">The provider string-literal renderer.</param>
    /// <param name="onlyWhenTableMissing">Whether existing tables retain their current physical graph.</param>
    /// <returns>Self-contained catalog SQL declaring <c>@doka_fk_graph_safe</c>, or null.</returns>
    public static string? BuildCatalogPreamble(
        IReadOnlyList<ExpectedForeignKeyDefinition> definitions,
        Func<string, string> literal,
        bool onlyWhenTableMissing = false
    )
    {
        var candidates = definitions.Where(HasCascadingAction).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var sql = new StringBuilder(4096);
        var missingCandidates = string.Join(" OR ", candidates.Select(candidate =>
            "NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = "
            + TableId(candidate.Table, candidate.Schema, literal) + " AND name = " + literal(candidate.Name) + ")"));

        sql.AppendLine("DECLARE @doka_fk_graph_safe bit = 1;");
        // WHY: A matching FK already passed SQL Server's topology validation.
        // Do not walk its complete graph again during a no-op/replay analysis.
        sql.Append("IF (").Append(missingCandidates).Append(')');
        if (onlyWhenTableMissing)
        {
            sql.Append(" AND ").Append(TableId(candidates[0].Table, candidates[0].Schema, literal)).Append(" IS NULL");
        }

        sql.AppendLine(" BEGIN");

        sql.AppendLine("DECLARE @doka_fk_edges TABLE (principal int NOT NULL, source_event tinyint NOT NULL, "
            + "dependent int NOT NULL, target_event tinyint NOT NULL);")
            .AppendLine("INSERT INTO @doka_fk_edges SELECT referenced_object_id, 1, parent_object_id, "
                + "CASE WHEN delete_referential_action = 1 THEN 1 ELSE 2 END "
                + "FROM sys.foreign_keys WHERE delete_referential_action <> 0 "
                + "UNION ALL SELECT referenced_object_id, 2, parent_object_id, 2 "
                + "FROM sys.foreign_keys WHERE update_referential_action <> 0;");

        foreach (var candidate in candidates)
        {
            var dependent = TableId(candidate.Table, candidate.Schema, literal);
            var principal = TableId(candidate.PrincipalTable, candidate.PrincipalSchema, literal);
            var selfReference = candidate.Table == candidate.PrincipalTable
                && (candidate.Schema ?? "dbo") == (candidate.PrincipalSchema ?? "dbo");

            var principalId = selfReference ? $"COALESCE({principal}, -1)" : principal;

            sql.Append("IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = ")
                .Append(dependent).Append(" AND name = ").Append(literal(candidate.Name)).Append(") AND ")
                .Append(principalId).AppendLine(" IS NOT NULL BEGIN");
            AppendEdge(sql, principalId, $"COALESCE({dependent}, -1)", candidate.OnDelete, isDelete: true);
            AppendEdge(sql, principalId, $"COALESCE({dependent}, -1)", candidate.OnUpdate, isDelete: false);
            sql.AppendLine("END;");
        }

        // WHY: Count paths rather than merely detecting cycles. DELETE SET
        // NULL/DEFAULT changes the event to UPDATE, which can cascade onward.
        // A capped breadth-first walk stops at the first repeated physical
        // table; it never enumerates exponentially many complete paths.
        sql.AppendLine("DECLARE @doka_fk_roots TABLE (table_id int NOT NULL, event_id tinyint NOT NULL, "
                + "PRIMARY KEY (table_id, event_id));")
            .AppendLine("INSERT INTO @doka_fk_roots SELECT DISTINCT principal, source_event FROM @doka_fk_edges;")
            .AppendLine("DECLARE @doka_fk_seen TABLE (table_id int NOT NULL PRIMARY KEY);")
            .AppendLine("DECLARE @doka_fk_frontier TABLE (table_id int NOT NULL, event_id tinyint NOT NULL);")
            .AppendLine("DECLARE @doka_fk_next TABLE (table_id int NOT NULL, event_id tinyint NOT NULL);")
            .AppendLine("DECLARE @doka_fk_root int, @doka_fk_event tinyint;")
            .AppendLine("WHILE @doka_fk_graph_safe = 1 AND EXISTS (SELECT 1 FROM @doka_fk_roots) BEGIN")
            .AppendLine("SELECT TOP (1) @doka_fk_root = table_id, @doka_fk_event = event_id FROM @doka_fk_roots;")
            .AppendLine("DELETE FROM @doka_fk_roots WHERE table_id = @doka_fk_root AND event_id = @doka_fk_event;")
            .AppendLine("DELETE FROM @doka_fk_seen; DELETE FROM @doka_fk_frontier;")
            .AppendLine("INSERT INTO @doka_fk_seen VALUES (@doka_fk_root);")
            .AppendLine("INSERT INTO @doka_fk_frontier VALUES (@doka_fk_root, @doka_fk_event);")
            .AppendLine("WHILE @doka_fk_graph_safe = 1 AND EXISTS (SELECT 1 FROM @doka_fk_frontier) BEGIN")
            .AppendLine("DELETE FROM @doka_fk_next;")
            .AppendLine("INSERT INTO @doka_fk_next SELECT e.dependent, e.target_event FROM @doka_fk_edges e "
                + "JOIN @doka_fk_frontier f ON f.table_id = e.principal AND f.event_id = e.source_event;")
            .AppendLine("IF EXISTS (SELECT 1 FROM @doka_fk_next GROUP BY table_id HAVING COUNT(*) > 1) "
                + "OR EXISTS (SELECT 1 FROM @doka_fk_next n JOIN @doka_fk_seen s ON s.table_id = n.table_id) "
                + "SET @doka_fk_graph_safe = 0;")
            .AppendLine("ELSE BEGIN INSERT INTO @doka_fk_seen SELECT table_id FROM @doka_fk_next; "
                + "DELETE FROM @doka_fk_frontier; "
                + "INSERT INTO @doka_fk_frontier SELECT table_id, event_id FROM @doka_fk_next; END;")
            .AppendLine("END; END;");

        sql.AppendLine("END;");

        return sql.ToString();
    }

    /// <summary>Identifies actions that introduce another cascading tree edge.</summary>
    internal static bool IsCascading(
        ReferentialAction action
    )
        => action is ReferentialAction.Cascade or ReferentialAction.SetNull or ReferentialAction.SetDefault;

    /// <summary>Validates inline column contracts before their table exists.</summary>
    /// <param name="table">The table whose inline FKs will be created.</param>
    /// <param name="literal">The provider string-literal renderer.</param>
    /// <returns>A catalog-only predicate for inline FK storage prerequisites.</returns>
    public static string BuildInlinePhysicalPredicate(
        ExpectedTableDefinition table,
        Func<string, string> literal
    )
    {
        var predicates = new List<string>();
        foreach (var foreignKey in table.ForeignKeys)
        {
            foreach (var name in foreignKey.Columns)
            {
                var column = table.Columns.First(value => value.Name == name);
                if ((HasCascadingAction(foreignKey) && IsRowVersion(column))
                    || ((foreignKey.OnDelete == ReferentialAction.SetNull
                            || foreignKey.OnUpdate == ReferentialAction.SetNull)
                        && !column.IsNullable)
                    || ((foreignKey.OnDelete == ReferentialAction.SetDefault
                            || foreignKey.OnUpdate == ReferentialAction.SetDefault)
                        && !column.IsNullable && column.DefaultValue.Kind == SafeMigrationDefaultValueKind.None))
                {
                    return "1 = 0";
                }
            }

            if (HasCascadingAction(foreignKey))
            {
                var selfReference = foreignKey.PrincipalTable == table.Table
                    && (foreignKey.PrincipalSchema ?? "dbo") == (table.Schema ?? "dbo");

                if (selfReference)
                {
                    if (foreignKey.PrincipalColumns.Any(name =>
                        IsRowVersion(table.Columns.First(value => value.Name == name))))
                    {
                        return "1 = 0";
                    }
                }
                else
                {
                    var principalColumns = string.Join(", ", foreignKey.PrincipalColumns.Select(literal));

                    predicates.Add(
                        "NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.system_type_id = 189 "
                        + $"AND c.object_id = {TableId(foreignKey.PrincipalTable, foreignKey.PrincipalSchema, literal)}"
                        + " "
                        + $"AND c.name IN ({principalColumns}))");
                }
            }
        }

        return predicates.Count == 0 ? "1 = 1" : string.Join(" AND ", predicates);
    }

    private static bool IsRowVersion(
        ExpectedColumnDefinition column
    )
        => column.IsRowVersion || column.StoreType is { } type
            && (type.Equals("rowversion", StringComparison.OrdinalIgnoreCase)
                || type.Equals("timestamp", StringComparison.OrdinalIgnoreCase));

    private static void AppendEdge(
        StringBuilder sql,
        string principal,
        string dependent,
        ReferentialAction action,
        bool isDelete
    )
    {
        if (!IsCascading(action))
        {
            return;
        }

        sql.Append("INSERT INTO @doka_fk_edges VALUES (").Append(principal).Append(", ")
            .Append(isDelete ? '1' : '2').Append(", ").Append(dependent).Append(", ")
            .Append(isDelete && action == ReferentialAction.Cascade ? '1' : '2').AppendLine(");");
    }

    private static string TriggerAbsent(
        string tableId,
        string eventName
    )
        => "NOT EXISTS (SELECT 1 FROM sys.triggers tr JOIN sys.trigger_events te ON te.object_id = tr.object_id "
            + $"WHERE tr.parent_id = {tableId} AND tr.is_instead_of_trigger = 1 AND te.type_desc = N'{eventName}')";

    private static string TableId(
        string table,
        string? schema,
        Func<string, string> literal
    )
        => "OBJECT_ID(" + literal("[" + (schema ?? "dbo").Replace("]", "]]", StringComparison.Ordinal)
            + "].[" + table.Replace("]", "]]", StringComparison.Ordinal) + "]") + ", N'U')";
}
