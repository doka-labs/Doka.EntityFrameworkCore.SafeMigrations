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
            satisfied);
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
        var localColumns = OrderedColumnsSql(definition.Columns);
        var principalColumns = OrderedColumnsSql(definition.PrincipalColumns);
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
            + $"HAVING GROUP_CONCAT(kcu.COLUMN_NAME ORDER BY kcu.ORDINAL_POSITION SEPARATOR ',') = {Literal(localColumns)} "
            + $"AND GROUP_CONCAT(kcu.REFERENCED_COLUMN_NAME ORDER BY kcu.ORDINAL_POSITION SEPARATOR ',') = {Literal(principalColumns)} "
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
