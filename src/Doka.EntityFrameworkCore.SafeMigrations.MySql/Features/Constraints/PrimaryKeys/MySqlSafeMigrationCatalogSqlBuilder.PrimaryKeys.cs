namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private MySqlSafeMigrationRuntimePlan BuildEnsurePrimaryKey(
        EnsurePrimaryKeyIntent intent,
        bool requiresReplacementRows
    )
    {
        var definition = intent.Definition;
        var exists = PrimaryKeyExists(definition.Table);
        var matching = ConstraintColumnsMatch(definition.Table, "PRIMARY", definition.Columns, "PRIMARY KEY");
        var dataBlocked = PrimaryKeyDataBlocked(definition);
        var physicallyAchievable = BuildUnprefixedKeyPhysicalShapeSupported(
            definition.Table,
            definition.Columns);

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(definition.Table)} THEN 'prerequisite_missing' "
            + $"WHEN NOT {exists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {exists} AND NOT ({physicallyAchievable}) THEN 'unsupported' "
            + $"WHEN NOT {exists} THEN 'missing' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching) with
        {
            UnsupportedCode = definition.Columns.Count > MySqlProjectedIndexPhysicalShape.MaximumKeyParts
                ? "primary_key_too_many_columns"
                : "primary_key_exceeds_physical_limit",

            // WHY: The old PRIMARY can conceal invalid rows for a different
            // replacement. Capture that independent evidence before any DROP;
            // matching keys and absent keys do not scan their rows again here.
            ClassificationCodeExpression = requiresReplacementRows
                ? $"CASE WHEN {exists} AND NOT ({matching}) THEN "
                    + $"CASE WHEN {dataBlocked} THEN 'primary_key_replacement_data_blocked' "
                    + "ELSE 'key_replacement_data_safe' END ELSE NULL END"
                : null,
        };
    }

    private MySqlSafeMigrationRuntimePlan BuildDropPrimaryKey(
        DropPrimaryKeyIntent intent
    )
    {
        var exists = PrimaryKeyExists(intent.Table);

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(intent.Table)} OR NOT {exists} " + "THEN 'missing' ELSE 'matching' END",
            $"NOT {exists}");
    }

    private string PrimaryKeyDataBlocked(
        ExpectedPrimaryKeyDefinition definition
    )
    {
        var nulls = string.Join(" OR ", definition.Columns.Select(column => $"{Delimited(column)} IS NULL"));

        return $"(EXISTS (SELECT 1 FROM {Delimited(definition.Table, definition.Schema)} "
            + $"WHERE {nulls} LIMIT 1) OR {DuplicateDataExists(
                definition.Table,
                definition.Schema,
                definition.Columns.Select(Delimited),
                "TRUE")})";
    }

    private string PrimaryKeyExists(
        string table
    ) => ConstraintExists(table, "PRIMARY", "PRIMARY KEY");
}
