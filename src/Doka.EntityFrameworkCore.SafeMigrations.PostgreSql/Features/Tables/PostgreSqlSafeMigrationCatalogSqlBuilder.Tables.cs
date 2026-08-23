namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureTable(
        EnsureTableIntent intent
    )
    {
        var definition = intent.Definition;
        var exists = RelationExists(definition.Table, definition.Schema);
        var table = TableExists(definition.Table, definition.Schema);
        var matching = intent.Mode == SafeMigrationTableMode.ConvergenceContainer ? table : TableMatches(definition);

        return Plan(
            $"CASE WHEN NOT {exists} THEN 'missing' WHEN NOT {table} THEN 'unsupported' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching);
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildDropTable(
        DropTableIntent intent
    )
    {
        var exists = RelationExists(intent.Table, intent.Schema);
        var table = TableExists(intent.Table, intent.Schema);

        return Plan(
            $"CASE WHEN NOT {exists} THEN 'missing' WHEN {table} THEN 'matching' " + "ELSE 'different' END",
            $"NOT {exists}");
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildRenameTable(
        RenameTableIntent intent
    )
    {
        var targetName = intent.NewName ?? intent.Name;
        var targetSchema = intent.NewSchema ?? intent.Schema;
        var sourceObject = RelationExists(intent.Name, intent.Schema);
        var source = TableExists(intent.Name, intent.Schema);
        var target = RelationExists(targetName, targetSchema);

        return Plan(
            $"CASE WHEN NOT {sourceObject} THEN 'missing' WHEN NOT {source} THEN 'different' "
            + $"WHEN {target} THEN 'different' "
            + "ELSE 'matching' END",
            $"NOT {RelationExists(intent.Name, intent.Schema)}");
    }

    private string TableMatches(
        ExpectedTableDefinition definition
    )
    {
        var schema = SchemaExpression(definition.Schema);
        var conditions = new List<string>
        {
            TableExists(definition.Table, definition.Schema),
            $"(SELECT COUNT(*) FROM pg_catalog.pg_attribute a "
            + "JOIN pg_catalog.pg_class c ON c.oid = a.attrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + $"WHERE n.nspname = {schema} AND c.relname = {Literal(definition.Table)} "
            + "AND a.attnum > 0 AND NOT a.attisdropped) "
            + $"= {definition.Columns.Count.ToString(CultureInfo.InvariantCulture)}",
            ConstraintCount(definition.Table, definition.Schema, 'u', definition.UniqueConstraints.Count),
            ConstraintCount(definition.Table, definition.Schema, 'c', definition.CheckConstraints.Count),
            ConstraintCount(definition.Table, definition.Schema, 'f', definition.ForeignKeys.Count),
            TableCommentMatches(definition),
        };

        for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
        {
            conditions.Add(
                ColumnMatches(definition.Table, definition.Schema, definition.Columns[ordinal], ordinal + 1));
        }

        conditions.Add(
            definition.PrimaryKey is null
                ? $"NOT {AnyConstraint(definition.Table, definition.Schema, 'p')}"
                : ConstraintColumnsMatch(
                    definition.Table,
                    definition.Schema,
                    definition.PrimaryKey.Name,
                    'p',
                    definition.PrimaryKey.Columns));

        conditions.AddRange(
            definition.UniqueConstraints.Select(value => ConstraintColumnsMatch(
                value.Table,
                value.Schema,
                value.Name,
                'u',
                value.Columns)));

        conditions.AddRange(definition.CheckConstraints.Select(CheckMatches));
        conditions.AddRange(definition.ForeignKeys.Select(ForeignKeyMatches));

        return $"({string.Join(" AND ", conditions)})";
    }

    private string TableCommentMatches(
        ExpectedTableDefinition definition
    ) => "(SELECT pg_catalog.obj_description(c.oid, 'pg_class') FROM pg_catalog.pg_class c "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(definition.Schema)} "
        + $"AND c.relname = {Literal(definition.Table)}) IS NOT DISTINCT FROM "
        + (definition.Comment is null ? "NULL" : Literal(definition.Comment));

    private string RelationExists(
        string table,
        string? schema
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_class c "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)})";

    private string TableExists(
        string table,
        string? schema
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_class c "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
        + "AND c.relkind IN ('r', 'p'))";
}
