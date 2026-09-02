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
        string additionalPrerequisite = "TRUE",
        string semanticAlias = "FALSE",
        string nonCanonicalAlias = "FALSE",
        string singletonConflict = "FALSE",
        string namespaceCollision = "FALSE"
    )
    {
        var tableExists = TableExists(table, schema);
        var exists = ConstraintExists(table, schema, name, type);
        var satisfied = $"({matching}) OR (NOT ({exists}) AND ({semanticAlias}))";

        // Local ownership is part of semantic identity. A partition-derived or
        // inherited object with the same visible shape is neither a safe alias
        // nor an absent target, because SafeMigrations cannot own its lifecycle.
        return Plan(
            $"CASE WHEN NOT {tableExists} OR NOT ({additionalPrerequisite}) THEN 'prerequisite_missing' "
            + $"WHEN {exists} AND {matching} THEN 'matching' "
            + $"WHEN {exists} THEN 'different' "
            + $"WHEN ({semanticAlias}) THEN 'matching' "
            + $"WHEN ({nonCanonicalAlias}) THEN 'different' "
            + $"WHEN ({singletonConflict}) THEN 'different' "
            + $"WHEN ({namespaceCollision}) THEN 'different' "
            + $"WHEN {dataBlocked} THEN 'data_blocked' ELSE 'missing' END",
            satisfied);
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildDropConstraint(
        string table,
        string? schema,
        string name,
        char type
    )
    {
        var exists = ConstraintExists(table, schema, name, type);
        var local = ConstraintExists(table, schema, name, type, requireLocalIdentity: true);

        return Plan(
            $"CASE WHEN NOT {exists} THEN 'missing' WHEN {local} THEN 'matching' ELSE 'different' END",
            $"NOT {exists}");
    }

    private string ConstraintColumnsMatch(
        string table,
        string? schema,
        string name,
        char type,
        IReadOnlyList<string> columns,
        bool requireExpectedName = true,
        bool requireLocalIdentity = true
    ) => ConstraintColumnsMatch(
        table,
        schema,
        type,
        columns,
        $"co.conname {(requireExpectedName ? "=" : "<>")} {Literal(name)}",
        requireLocalIdentity);

    private string ConstraintColumnsMatch(
        string table,
        string? schema,
        char type,
        IReadOnlyList<string> columns,
        string namePredicate,
        bool requireLocalIdentity = true
    ) => ConstraintBaseWithoutName(table, schema, type)
        + $" AND {namePredicate}"
        + StandardConstraintSemantics(requireLocalIdentity)
        + (type == 'u' ? UniqueNullSemanticsMatch() : string.Empty)
        + $" AND ARRAY(SELECT a.attname FROM unnest(co.conkey) WITH ORDINALITY AS key(attnum, ord) "
        + "JOIN pg_catalog.pg_attribute a ON a.attrelid = co.conrelid AND a.attnum = key.attnum "
        + $"ORDER BY key.ord) = {NameArray(columns)})";

    private static string StandardConstraintSemantics(
        bool requireLocalIdentity = true
    )
        // JSON projection keeps one catalog query compatible with PostgreSQL
        // 14-18 while still rejecting semantics introduced by newer servers.
        => (requireLocalIdentity ? LocalConstraintIdentity() : string.Empty)
            + " AND NOT co.condeferrable AND NOT co.condeferred AND co.convalidated"
            + " AND COALESCE((to_jsonb(co) ->> 'conenforced')::boolean, TRUE)"
            + " AND NOT COALESCE((to_jsonb(co) ->> 'conperiod')::boolean, FALSE)";

    private static string LocalConstraintIdentity()
        // Inherited and partition-derived constraints can enforce the same
        // expression while remaining owned by another table. Treating them as
        // local would make a later parent change invalidate our postcondition.
        => " AND co.conparentid = 0 AND co.conislocal AND co.coninhcount = 0";

    private static string UniqueNullSemanticsMatch()
        // PostgreSQL 15 added indnullsnotdistinct. An ordinary EF unique
        // constraint retains the older/default NULLS DISTINCT behavior.
        => " AND NOT COALESCE((SELECT (to_jsonb(i) ->> 'indnullsnotdistinct')::boolean "
            + "FROM pg_catalog.pg_index i WHERE i.indexrelid = co.conindid), FALSE)";

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
    ) => ConstraintBaseWithoutName(table, schema, type)
        + $" AND co.conname = {Literal(name)}";

    private string ConstraintBaseWithoutName(
        string table,
        string? schema,
        char type
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_constraint co "
        + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
        + $"AND co.contype = {Literal(type.ToString())}::\"char\"";

    private string ConstraintExists(
        string table,
        string? schema,
        string name,
        char type,
        bool requireLocalIdentity = false
    ) => ConstraintBase(table, schema, name, type)
        + (requireLocalIdentity ? LocalConstraintIdentity() : string.Empty)
        + ")";

    private string AnyConstraint(
        string table,
        string? schema,
        char type
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_constraint co "
        + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} AND c.relname = {Literal(table)} "
        + $"AND co.contype = {Literal(type.ToString())}::\"char\")";

    private string RelationNameExists(
        string name,
        string? schema
    ) => "EXISTS (SELECT 1 FROM pg_catalog.pg_class relation "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = relation.relnamespace "
        + $"WHERE n.nspname = {SchemaExpression(schema)} "
        + $"AND relation.relname = {Literal(name)})";

    private string ConstraintColumnsSatisfied(
        string table,
        string? schema,
        string name,
        char type,
        IReadOnlyList<string> columns
    )
    {
        var exists = ConstraintExists(table, schema, name, type);
        var exact = ConstraintColumnsMatch(table, schema, name, type, columns);
        var semanticAlias = ConstraintColumnsMatch(
            table,
            schema,
            name,
            type,
            columns,
            requireExpectedName: false);

        return $"({exact}) OR (NOT ({exists}) AND ({semanticAlias}))";
    }

    private string AllConstraintsModeled(
        string table,
        string? schema,
        char type,
        string[] expectedMatches
    )
    {
        var modeled = expectedMatches.Length == 0
            ? "FALSE"
            : $"({string.Join(" OR ", expectedMatches)})";

        return "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint candidate_co "
            + "JOIN pg_catalog.pg_class candidate_c ON candidate_c.oid = candidate_co.conrelid "
            + "JOIN pg_catalog.pg_namespace candidate_n ON candidate_n.oid = candidate_c.relnamespace "
            + $"WHERE candidate_n.nspname = {SchemaExpression(schema)} "
            + $"AND candidate_c.relname = {Literal(table)} "
            + $"AND candidate_co.contype = {Literal(type.ToString())}::\"char\" "
            + $"AND NOT ({modeled}))";
    }

    private string NameArray(
        IReadOnlyList<string> values
    ) => $"ARRAY[{string.Join(", ", values.Select(Literal))}]::name[]";
}
