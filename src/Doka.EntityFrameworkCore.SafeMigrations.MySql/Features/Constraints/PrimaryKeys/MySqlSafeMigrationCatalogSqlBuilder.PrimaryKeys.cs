namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private MySqlSafeMigrationRuntimePlan BuildEnsurePrimaryKey(
        EnsurePrimaryKeyIntent intent
    )
    {
        var definition = intent.Definition;
        var exists = PrimaryKeyExists(definition.Table);
        var matching = ConstraintColumnsMatch(definition.Table, "PRIMARY", definition.Columns, "PRIMARY KEY");
        var dataBlocked = PrimaryKeyDataBlocked(definition);

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(definition.Table)} THEN 'data_blocked' "
            + $"WHEN NOT {exists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {exists} THEN 'missing' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching);
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

        return $"(EXISTS (SELECT 1 FROM {Delimited(definition.Table)} WHERE {nulls} LIMIT 1) "
            + $"OR {DuplicateDataExists(definition.Table, definition.Columns.Select(Delimited), "TRUE")})";
    }

    private string PrimaryKeyExists(
        string table
    ) => ConstraintExists(table, "PRIMARY", "PRIMARY KEY");
}
