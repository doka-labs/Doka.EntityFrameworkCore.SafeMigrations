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
        bool isMariaDb
    )
    {
        var definition = intent.Definition;
        var exists = ConstraintExists(definition.Table, definition.Name, "CHECK");
        var matching = CheckConstraintMatches(definition, isMariaDb, requireExpectedName: true);
        var identityConflict = CheckConstraintMatches(definition, isMariaDb, requireExpectedName: false);
        var dataBlocked = CheckConstraintDataBlocked(definition);

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(definition.Table)} THEN 'prerequisite_missing' "
            + $"WHEN NOT {exists} AND {identityConflict} THEN 'unsupported' "
            + $"WHEN NOT {exists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {exists} THEN 'missing' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching) with
        {
            UnsupportedCode = "check_constraint_semantic_identity_conflict",
        };
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
            + $"AND tc.CONSTRAINT_NAME {(requireExpectedName ? "=" : "<>")} {Literal(definition.Name)} "
            + "AND tc.CONSTRAINT_TYPE = 'CHECK' "
            // A disabled MySQL check has the same catalog expression but does
            // not enforce the contract. MariaDB does not expose this facet.
            + (isMariaDb ? string.Empty : "AND tc.ENFORCED = 'YES' ")
            + $"AND cc.CHECK_CLAUSE IN ({string.Join(", ", candidates)}))";
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
