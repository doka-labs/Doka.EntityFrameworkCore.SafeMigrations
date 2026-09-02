namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private MySqlSafeMigrationRuntimePlan BuildEnsureUniqueConstraint(
        EnsureUniqueConstraintIntent intent
    )
    {
        var definition = intent.Definition;
        var exists = ConstraintExists(definition.Table, definition.Name, "UNIQUE");
        var matching = ConstraintColumnsMatch(definition.Table, definition.Name, definition.Columns, "UNIQUE");
        var semanticAlias = ConstraintColumnsMatch(
            definition.Table,
            definition.Name,
            definition.Columns,
            "UNIQUE",
            requireExpectedName: false);

        var dataBlocked = UniqueConstraintDataBlocked(definition);
        var satisfied = $"({matching}) OR (NOT ({exists}) AND ({semanticAlias}))";

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(definition.Table)} THEN 'prerequisite_missing' "
            + $"WHEN {exists} AND {matching} THEN 'matching' "
            + $"WHEN {exists} THEN 'different' "
            + $"WHEN {semanticAlias} THEN 'matching' "
            + $"WHEN {dataBlocked} THEN 'data_blocked' ELSE 'missing' END",
            satisfied);
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
        return DuplicateDataExists(definition.Table, keys, nonNull);
    }
}
