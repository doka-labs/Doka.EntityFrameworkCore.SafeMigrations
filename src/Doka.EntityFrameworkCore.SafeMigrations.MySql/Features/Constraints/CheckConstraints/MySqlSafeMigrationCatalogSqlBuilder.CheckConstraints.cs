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
        var matching = CheckConstraintMatches(definition, isMariaDb);
        var dataBlocked = CheckConstraintDataBlocked(definition);

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(definition.Table)} THEN 'prerequisite_missing' "
            + $"WHEN NOT {exists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {exists} THEN 'missing' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching);
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
        bool isMariaDb
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
            + $"WHERE tc.CONSTRAINT_SCHEMA = DATABASE() AND tc.TABLE_NAME = {Literal(definition.Table)} "
            + $"AND tc.CONSTRAINT_NAME = {Literal(definition.Name)} AND tc.CONSTRAINT_TYPE = 'CHECK' "
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
