namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureConstraint(
        string table,
        string? schema,
        string name,
        char type,
        string matching,
        string dataBlocked,
        string additionalPrerequisite = "TRUE"
    )
    {
        var tableExists = TableExists(table, schema);
        var exists = ConstraintExists(table, schema, name, type);

        return Plan(
            $"CASE WHEN NOT {tableExists} OR NOT ({additionalPrerequisite}) THEN 'prerequisite_missing' "
            + $"WHEN NOT {exists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {exists} THEN 'missing' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching);
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildDropConstraint(
        string table,
        string? schema,
        string name,
        char type
    )
    {
        var exists = ConstraintExists(table, schema, name, type);
        return Plan($"CASE WHEN {exists} THEN 'matching' ELSE 'missing' END", $"NOT {exists}");
    }

    private string ConstraintColumnsMatch(
        string table,
        string? schema,
        string name,
        char type,
        IReadOnlyList<string> columns
    ) => ConstraintBase(table, schema, name, type)
        + $" AND ARRAY(SELECT a.attname FROM unnest(co.conkey) WITH ORDINALITY AS key(attnum, ord) "
        + "JOIN pg_catalog.pg_attribute a ON a.attrelid = co.conrelid AND a.attnum = key.attnum "
        + $"ORDER BY key.ord) = {NameArray(columns)})";

    private string DuplicateDataExists(
        string table,
        string? schema,
        IEnumerable<string> keys,
        string predicate
    )
    {
        var snapshot = keys.ToArray();
        return $"EXISTS (SELECT 1 FROM {Qualified(table, schema)} WHERE {predicate} "
            + $"GROUP BY {string.Join(", ", snapshot)} HAVING COUNT(*) > 1 LIMIT 1)";
    }

    private string Delimited(
        string identifier
    ) => _sqlGenerationHelper.DelimitIdentifier(identifier);

    private string ConstraintBase(
        string table,
        string? schema,
        string name,
        char type
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_constraint co "
        + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
        + $"AND co.conname = {Literal(name)} AND co.contype = {Literal(type.ToString())}::\"char\"";

    private string ConstraintExists(
        string table,
        string? schema,
        string name,
        char type
    ) => ConstraintBase(table, schema, name, type) + ")";

    private string AnyConstraint(
        string table,
        string? schema,
        char type
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_constraint co "
        + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
        + $"AND co.contype = {Literal(type.ToString())}::\"char\")";

    private string ConstraintCount(
        string table,
        string? schema,
        char type,
        int expected
    ) => "(SELECT COUNT(*) FROM pg_catalog.pg_constraint co "
        + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
        + $"AND co.contype = {Literal(type.ToString())}::\"char\") "
        + $"= {expected.ToString(CultureInfo.InvariantCulture)}";

    private string NameArray(
        IReadOnlyList<string> values
    ) => $"ARRAY[{string.Join(", ", values.Select(Literal))}]::name[]";
}
