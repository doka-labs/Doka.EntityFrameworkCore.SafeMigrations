namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private MySqlSafeMigrationRuntimePlan BuildEnsureUniqueConstraint(
        EnsureUniqueConstraintIntent intent
    )
    {
        var definition = intent.Definition;
        var exists = ConstraintExists(definition.Table, definition.Name, "UNIQUE");
        var indexNameExists = IndexExists(definition.Table, definition.Name);
        var matching = ConstraintColumnsMatch(definition.Table, definition.Name, definition.Columns, "UNIQUE");
        var semanticAlias = ConstraintColumnsMatch(
            definition.Table,
            definition.Name,
            definition.Columns,
            "UNIQUE",
            requireExpectedName: false);

        var dataBlocked = UniqueConstraintDataBlocked(definition);
        var physicallyAchievable = BuildUnprefixedKeyPhysicalShapeSupported(
            definition.Table,
            definition.Columns);

        var satisfied = $"({matching}) OR (NOT ({indexNameExists}) AND ({semanticAlias}))";
        var semanticCandidates = ConstraintColumnsMatchQuery(
            definition.Table,
            definition.Columns,
            "UNIQUE",
            $"tc.CONSTRAINT_NAME <> {Literal(definition.Name)}");

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(definition.Table)} THEN 'prerequisite_missing' "
            + $"WHEN {exists} AND {matching} THEN 'matching' "
            + $"WHEN {exists} THEN 'different' "
            + $"WHEN {indexNameExists} THEN 'different' "
            + $"WHEN {semanticAlias} THEN 'matching' "
            + $"WHEN {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT ({physicallyAchievable}) THEN 'unsupported' "
            + "ELSE 'missing' END",
            satisfied) with
        {
            MatchedObjectNameExpression = ResolveMatchingObjectName(
                matching,
                Literal(definition.Name),
                semanticCandidates),
            UnsupportedCode = definition.Columns.Count > MySqlProjectedIndexPhysicalShape.MaximumKeyParts
                ? "unique_constraint_too_many_columns"
                : "unique_constraint_exceeds_physical_limit",
            // WHY: Ordered Drop -> Ensure projection still needs duplicate-row
            // proof hidden by either same-name physical shape conflict.
            ClassificationCodeExpression = $"CASE WHEN {indexNameExists} AND NOT ({matching}) AND {dataBlocked} "
                + "THEN 'unique_constraint_replacement_data_blocked' ELSE NULL END",
        };
    }

    private MySqlSafeMigrationRuntimePlan BuildDropUniqueConstraint(
        DropUniqueConstraintIntent intent
    )
    {
        var exists = ConstraintExists(intent.Table, intent.Name, "UNIQUE");

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(intent.Table)} OR NOT {exists} " + "THEN 'missing' ELSE 'matching' END",
            $"NOT {exists}");
    }

    private string ConstraintMatches(
        ExpectedUniqueConstraintDefinition definition
    ) => ConstraintColumnsMatch(definition.Table, definition.Name, definition.Columns, "UNIQUE");

    private string UniqueConstraintSatisfied(
        ExpectedUniqueConstraintDefinition definition
    )
    {
        var exists = ConstraintExists(definition.Table, definition.Name, "UNIQUE");
        var exact = ConstraintMatches(definition);
        var semanticAlias = ConstraintColumnsMatch(
            definition.Table,
            definition.Name,
            definition.Columns,
            "UNIQUE",
            requireExpectedName: false);

        return $"({exact}) OR (NOT ({exists}) AND ({semanticAlias}))";
    }

    private string UniqueConstraintDataBlocked(
        ExpectedUniqueConstraintDefinition definition
    )
    {
        var keys = definition
            .Columns
            .Select(Delimited)
            .ToArray();

        var nonNull = string.Join(" AND ", keys.Select(static key => $"{key} IS NOT NULL"));
        return DuplicateDataExists(definition.Table, definition.Schema, keys, nonNull);
    }
}
