namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private MySqlSafeMigrationRuntimePlan BuildEnsureForeignKey(
        EnsureForeignKeyIntent intent,
        MySqlServerVersion serverVersion
    )
    {
        var definition = intent.Definition;
        var exists = ConstraintExists(definition.Table, definition.Name, "FOREIGN KEY");
        var matching = ForeignKeyMatches(definition, requireExpectedName: true);
        var semanticAlias = ForeignKeyMatches(definition, requireExpectedName: false);
        // MySQL and MariaDB before 12.1 allocate FK symbols in the database
        // namespace. MariaDB 12.1 deliberately changed that boundary to the
        // owning table, so a same-name FK on another table is then harmless.
        var usesDatabaseScopedNames = !serverVersion.IsMariaDb
            || serverVersion.Version < new Version(12, 1);

        var nameCollision = usesDatabaseScopedNames
            ? DatabaseConstraintNameExists(definition.Name, "FOREIGN KEY")
            : "FALSE";

        var dataBlocked = ForeignKeyDataBlocked(definition);
        var satisfied = $"({matching}) OR (NOT ({exists}) AND ({semanticAlias}))";

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(definition.Table)} "
            + $"OR NOT {BaseTableExists(definition.PrincipalTable)} THEN 'prerequisite_missing' "
            // An exact-name collision remains authoritative. A second object
            // must never hide drift on the identity owned by this operation.
            + $"WHEN {exists} AND {matching} THEN 'matching' "
            + $"WHEN {exists} THEN 'different' "
            + $"WHEN {semanticAlias} THEN 'matching' "
            + $"WHEN {nameCollision} THEN 'different' "
            + $"WHEN {dataBlocked} THEN 'data_blocked' ELSE 'missing' END",
            satisfied) with
        {
            DiagnosticEvidenceExpression = BuildForeignKeyDiagnosticEvidence(definition),
        };
    }

    private MySqlSafeMigrationRuntimePlan BuildDropForeignKey(
        DropForeignKeyIntent intent
    )
    {
        var exists = ConstraintExists(intent.Table, intent.Name, "FOREIGN KEY");

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(intent.Table)} OR NOT {exists} " + "THEN 'missing' ELSE 'matching' END",
            $"NOT {exists}");
    }

    private string ForeignKeyMatches(
        ExpectedForeignKeyDefinition definition,
        bool requireExpectedName
    ) => ForeignKeyMatches(
        definition,
        $"rc.CONSTRAINT_NAME {(requireExpectedName ? "=" : "<>")} {Literal(definition.Name)}");

    private string ForeignKeyMatches(
        ExpectedForeignKeyDefinition definition,
        string namePredicate
    )
    {
        var localColumnsMatch = OrderedForeignKeyColumnsMatch(
            definition.Columns,
            "kcu.COLUMN_NAME");

        var principalColumnsMatch = OrderedForeignKeyColumnsMatch(
            definition.PrincipalColumns,
            "kcu.REFERENCED_COLUMN_NAME");

        var updateRules = ReferentialRules(definition.OnUpdate);
        var deleteRules = ReferentialRules(definition.OnDelete);

        return $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc "
            + "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu "
            + "ON kcu.CONSTRAINT_SCHEMA = rc.CONSTRAINT_SCHEMA "
            + "AND kcu.TABLE_NAME = rc.TABLE_NAME AND kcu.CONSTRAINT_NAME = rc.CONSTRAINT_NAME "
            + $"WHERE rc.CONSTRAINT_SCHEMA = DATABASE() AND rc.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND {namePredicate} "
            // Group every physical constraint independently. Without its
            // identity in the group, two equivalent legacy constraints merge
            // their key rows and can evade the semantic-candidate predicate.
            + $"GROUP BY rc.CONSTRAINT_NAME, rc.UPDATE_RULE, rc.DELETE_RULE, "
            + "kcu.REFERENCED_TABLE_SCHEMA, kcu.REFERENCED_TABLE_NAME "
            + $"HAVING {localColumnsMatch} "
            + $"AND {principalColumnsMatch} "
            + $"AND kcu.REFERENCED_TABLE_SCHEMA = DATABASE() "
            + $"AND kcu.REFERENCED_TABLE_NAME = {Literal(definition.PrincipalTable)} "
            + $"AND rc.UPDATE_RULE IN ({string.Join(", ", updateRules.Select(Literal))}) "
            + $"AND rc.DELETE_RULE IN ({string.Join(", ", deleteRules.Select(Literal))}))";
    }

    private string ForeignKeySatisfied(
        ExpectedForeignKeyDefinition definition
    )
    {
        var exists = ConstraintExists(definition.Table, definition.Name, "FOREIGN KEY");
        var exact = ForeignKeyMatches(definition, requireExpectedName: true);
        var semanticAlias = ForeignKeyMatches(definition, requireExpectedName: false);

        return $"({exact}) OR (NOT ({exists}) AND ({semanticAlias}))";
    }

    private string ForeignKeyDataBlocked(
        ExpectedForeignKeyDefinition definition
    )
    {
        var localNotNull = string.Join(
            " AND ",
            definition.Columns.Select(column => $"d.{Delimited(column)} IS NOT NULL"));

        var join = string.Join(
            " AND ",
            definition.Columns.Zip(
                definition.PrincipalColumns,
                (
                        local,
                        principal
                    ) => $"d.{Delimited(local)} = p.{Delimited(principal)}"));

        return $"EXISTS (SELECT 1 FROM {Delimited(definition.Table)} d "
            + $"LEFT JOIN {Delimited(definition.PrincipalTable)} p ON {join} "
            + $"WHERE {localNotNull} AND p.{Delimited(definition.PrincipalColumns[0])} IS NULL LIMIT 1)";
    }

    private string BuildForeignKeyDiagnosticEvidence(
        ExpectedForeignKeyDefinition definition
    )
    {
        var expectedColumns = OrderedColumnsSql(definition.Columns);
        var expectedPrincipalColumns = OrderedColumnsSql(definition.PrincipalColumns);
        var expectedUpdateRules = ReferentialRules(definition.OnUpdate);
        var expectedDeleteRules = ReferentialRules(definition.OnDelete);
        var localColumnsMatch = OrderedForeignKeyColumnsMatch(
            definition.Columns,
            "kcu.COLUMN_NAME");

        var principalColumnsMatch = OrderedForeignKeyColumnsMatch(
            definition.PrincipalColumns,
            "kcu.REFERENCED_COLUMN_NAME");

        var actualColumns = "GROUP_CONCAT(kcu.COLUMN_NAME ORDER BY kcu.ORDINAL_POSITION SEPARATOR ',')";
        var actualPrincipalColumns = "GROUP_CONCAT(kcu.REFERENCED_COLUMN_NAME "
            + "ORDER BY kcu.ORDINAL_POSITION SEPARATOR ',')";

        var columnDiagnostics = BoundedOrderedListDiagnostics(
            expectedColumns,
            actualColumns,
            definition.Columns.Count,
            "kcu.COLUMN_NAME");

        var principalColumnDiagnostics = BoundedOrderedListDiagnostics(
            expectedPrincipalColumns,
            actualPrincipalColumns,
            definition.PrincipalColumns.Count,
            "kcu.REFERENCED_COLUMN_NAME");

        return "(SELECT NULLIF(CONCAT_WS(CHAR(30), "
            + DiagnosticRecord(
                $"NOT ({localColumnsMatch})",
                "foreign_key_column_order",
                columnDiagnostics.Expected,
                columnDiagnostics.Actual)
            + ", "
            + DiagnosticRecord(
                $"NOT ({principalColumnsMatch})",
                "foreign_key_principal_column_order",
                principalColumnDiagnostics.Expected,
                principalColumnDiagnostics.Actual)
            + ", "
            + DiagnosticRecord(
                $"rc.DELETE_RULE NOT IN ({string.Join(", ", expectedDeleteRules.Select(Literal))})",
                "foreign_key_delete_behavior",
                Literal(ReferentialDiagnosticCode(definition.OnDelete)),
                "LOWER(REPLACE(rc.DELETE_RULE, ' ', '_'))")
            + ", "
            + DiagnosticRecord(
                $"rc.UPDATE_RULE NOT IN ({string.Join(", ", expectedUpdateRules.Select(Literal))})",
                "foreign_key_update_behavior",
                Literal(ReferentialDiagnosticCode(definition.OnUpdate)),
                "LOWER(REPLACE(rc.UPDATE_RULE, ' ', '_'))")
            + "), '') FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc "
            + "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu "
            + "ON kcu.CONSTRAINT_SCHEMA = rc.CONSTRAINT_SCHEMA "
            + "AND kcu.TABLE_NAME = rc.TABLE_NAME AND kcu.CONSTRAINT_NAME = rc.CONSTRAINT_NAME "
            + $"WHERE rc.CONSTRAINT_SCHEMA = DATABASE() AND rc.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND rc.CONSTRAINT_NAME = {Literal(definition.Name)} "
            + "GROUP BY rc.CONSTRAINT_NAME, rc.UPDATE_RULE, rc.DELETE_RULE LIMIT 1)";
    }

    private string OrderedForeignKeyColumnsMatch(
        IReadOnlyList<string> columns,
        string columnExpression
    )
    {
        var conditions = new List<string>(columns.Count + 1)
        {
            $"COUNT(*) = {columns.Count.ToString(CultureInfo.InvariantCulture)}",
        };

        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            conditions.Add(
                $"SUM(CASE WHEN kcu.ORDINAL_POSITION = {(ordinal + 1).ToString(CultureInfo.InvariantCulture)} "
                + $"AND {columnExpression} = {Literal(columns[ordinal])} THEN 1 ELSE 0 END) = 1");
        }

        // WHY: GROUP_CONCAT is bounded by the session's group_concat_max_len.
        // Per-ordinal aggregates keep semantic identity correct at maximum key widths.
        return $"({string.Join(" AND ", conditions)})";
    }

    private (string Expected, string Actual) BoundedOrderedListDiagnostics(
        string expected,
        string actualExpression,
        int expectedCount,
        string columnExpression
    )
    {
        if (expected.Length <= SafeMigrationFacetDifference.MaximumValueLength)
        {
            return (
                Literal(expected),
                $"LEFT({actualExpression}, {SafeMigrationFacetDifference.MaximumValueLength})");
        }

        var positions = Enumerable
            .Range(1, expectedCount)
            .Select(position =>
                $"MAX(CASE WHEN kcu.ORDINAL_POSITION = {position.ToString(CultureInfo.InvariantCulture)} "
                + $"THEN {columnExpression} END)");

        var expectedPayload = $"count={expectedCount.ToString(CultureInfo.InvariantCulture)};{expected}";
        var actualPayload = $"CONCAT('count=', COUNT(*), ';', CONCAT_WS(',', {string.Join(", ", positions)}))";

        return (
            $"CONCAT('sha256:', LOWER(SHA2({Literal(expectedPayload)}, 256)))",
            $"CONCAT('sha256:', LOWER(SHA2({actualPayload}, 256)))");
    }

    private static string ReferentialDiagnosticCode(
        ReferentialAction action
    ) => action switch
    {
        ReferentialAction.NoAction => "no_action_or_restrict",
        ReferentialAction.Restrict => "restrict",
        ReferentialAction.Cascade => "cascade",
        ReferentialAction.SetNull => "set_null",
        ReferentialAction.SetDefault => "set_default",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static IReadOnlyList<string> ReferentialRules(
        ReferentialAction action
    ) => action switch
    {
        ReferentialAction.Cascade => ["CASCADE"],
        ReferentialAction.SetNull => ["SET NULL"],
        ReferentialAction.SetDefault => ["SET DEFAULT"],
        ReferentialAction.Restrict => ["RESTRICT"],
        ReferentialAction.NoAction => ["NO ACTION", "RESTRICT",],
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
