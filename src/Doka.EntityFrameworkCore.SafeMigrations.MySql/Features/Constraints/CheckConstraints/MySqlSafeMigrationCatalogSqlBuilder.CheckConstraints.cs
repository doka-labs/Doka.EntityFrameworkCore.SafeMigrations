namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private static string? GetUnsupportedCheckConstraintFeature(
        SafeMigrationIntent intent,
        MySqlMigrationFeatureSet features
    ) => intent is EnsureCheckConstraintIntent && !Supported(features, MySqlMigrationFeature.CheckConstraints)
        ? "check_constraint"
        : null;

    private MySqlSafeMigrationRuntimePlan BuildEnsureCheckConstraint(
        EnsureCheckConstraintIntent intent,
        MySqlServerVersion serverVersion
    )
    {
        var definition = intent.Definition;
        var isMariaDb = serverVersion.IsMariaDb;
        var exists = ConstraintExists(definition.Table, definition.Name, "CHECK");
        var matching = CheckConstraintMatches(definition, isMariaDb, requireExpectedName: true);
        var semanticAlias = CheckConstraintMatches(definition, isMariaDb, requireExpectedName: false);
        var nameCollision = isMariaDb ? "FALSE" : DatabaseConstraintNameExists(definition.Name, "CHECK");
        var dataBlocked = CheckConstraintDataBlocked(definition);
        var satisfied = $"({matching}) OR (NOT ({exists}) AND ({semanticAlias}))";

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(definition.Table)} THEN 'prerequisite_missing' "
            + $"WHEN {exists} AND {matching} THEN 'matching' "
            + $"WHEN {exists} THEN 'different' "
            + $"WHEN {semanticAlias} THEN 'matching' "
            + $"WHEN {nameCollision} THEN 'different' "
            + $"WHEN {dataBlocked} THEN 'data_blocked' ELSE 'missing' END",
            satisfied);
    }

    private MySqlSafeMigrationRuntimePlan BuildDropCheckConstraint(
        DropCheckConstraintIntent intent
    )
    {
        var exists = ConstraintExists(intent.Table, intent.Name, "CHECK");
        return Plan(
            $"CASE WHEN NOT {BaseTableExists(intent.Table)} OR NOT {exists} " + "THEN 'missing' ELSE 'matching' END",
            $"NOT {exists}");
    }

    private string CheckConstraintMatches(
        ExpectedCheckConstraintDefinition definition,
        bool isMariaDb,
        bool requireExpectedName = true
    ) => CheckConstraintMatches(
        definition,
        isMariaDb,
        $"tc.CONSTRAINT_NAME {(requireExpectedName ? "=" : "<>")} {Literal(definition.Name)}");

    private string CheckConstraintMatches(
        ExpectedCheckConstraintDefinition definition,
        bool isMariaDb,
        string namePredicate
    )
    {
        var expression = definition.Sql ?? _expressionRenderer.Render(definition.Expression!);
        var candidates = new[] { expression, $"({expression})", }
            .Concat(
                MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(
                    expression,
                    includeMySqlEncodedDisplay: !isMariaDb))
            .Distinct(StringComparer.Ordinal)
            .Select(Literal);

        return $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc "
            + "JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc "
            + "ON cc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA "
            + "AND cc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME "
            // MariaDB 12.1 made user-visible constraint names table-scoped.
            // MySQL's catalog omits TABLE_NAME because names remain schema-wide.
            + (isMariaDb ? "AND cc.TABLE_NAME = tc.TABLE_NAME " : string.Empty)
            + $"WHERE tc.CONSTRAINT_SCHEMA = DATABASE() AND tc.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND {namePredicate} "
            + "AND tc.CONSTRAINT_TYPE = 'CHECK' "
            // A disabled MySQL check has the same catalog expression but does
            // not enforce the contract. MariaDB does not expose this facet.
            + (isMariaDb ? string.Empty : "AND tc.ENFORCED = 'YES' ")
            + $"AND cc.CHECK_CLAUSE IN ({string.Join(", ", candidates)}))";
    }

    private string CheckConstraintSatisfied(
        ExpectedCheckConstraintDefinition definition,
        bool isMariaDb
    )
    {
        var exists = ConstraintExists(definition.Table, definition.Name, "CHECK");
        var exact = CheckConstraintMatches(definition, isMariaDb, requireExpectedName: true);
        var semanticAlias = CheckConstraintMatches(definition, isMariaDb, requireExpectedName: false);

        return $"({exact}) OR (NOT ({exists}) AND ({semanticAlias}))";
    }

    private string CheckConstraintDataBlocked(
        ExpectedCheckConstraintDefinition definition
    )
    {
        var expression = definition.Sql ?? _expressionRenderer.Render(definition.Expression!);

        return $"EXISTS (SELECT 1 FROM {Delimited(definition.Table)} "
            + $"WHERE NOT COALESCE(({expression}), TRUE) LIMIT 1)";
    }
}
