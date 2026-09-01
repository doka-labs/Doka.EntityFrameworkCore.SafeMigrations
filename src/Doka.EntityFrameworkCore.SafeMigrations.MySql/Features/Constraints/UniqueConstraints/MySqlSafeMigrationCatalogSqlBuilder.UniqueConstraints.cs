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
        var identityConflict = ConstraintColumnsMatch(
            definition.Table,
            definition.Name,
            definition.Columns,
            "UNIQUE",
            requireExpectedName: false);
        var dataBlocked = UniqueConstraintDataBlocked(definition);

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(definition.Table)} THEN 'prerequisite_missing' "
            + $"WHEN NOT {exists} AND {identityConflict} THEN 'unsupported' "
            + $"WHEN NOT {exists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {exists} THEN 'missing' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching) with
        {
            UnsupportedCode = "unique_constraint_semantic_identity_conflict",
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
