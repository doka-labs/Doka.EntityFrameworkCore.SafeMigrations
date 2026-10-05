namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureTable(
        EnsureTableIntent intent,
        SafeMigrationExpectedTableConstraints? expectedTableConstraints
    )
    {
        var definition = intent.Definition;
        var exists = RelationExists(definition.Table, definition.Schema);
        var table = TableExists(definition.Table, definition.Schema);
        var matching = intent.Mode == SafeMigrationTableMode.ConvergenceContainer
            ? table
            : TableMatches(definition, expectedTableConstraints);

        // WHY: Without expected table constraints the postcondition asks TableMatches the same
        // question with the same arguments, so composing it a second time produced an identical
        // string. Reusing the first result keeps one composition per build.
        var executionPostcondition = intent.Mode == SafeMigrationTableMode.ConvergenceContainer
            ? table
            : expectedTableConstraints is null
                ? matching
                : TableMatches(definition, expectedTableConstraints: null);

        return Plan(
            $"CASE WHEN NOT {exists} THEN 'missing' WHEN NOT {table} THEN 'unsupported' "
            + $"WHEN {matching} THEN 'matching' ELSE 'different' END",
            matching) with
        {
            ExecutionPostcondition = executionPostcondition,
        };
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
        ExpectedTableDefinition definition,
        SafeMigrationExpectedTableConstraints? expectedTableConstraints
    )
    {
        var allowedUniqueConstraints = expectedTableConstraints?.AllowedUniqueConstraints
            ?? definition.UniqueConstraints;

        var requiredUniqueConstraints = expectedTableConstraints?.RequiredUniqueConstraints
            ?? definition.UniqueConstraints;

        var allowedCheckConstraints = expectedTableConstraints?.AllowedCheckConstraints
            ?? definition.CheckConstraints;

        var requiredCheckConstraints = expectedTableConstraints?.RequiredCheckConstraints
            ?? definition.CheckConstraints;

        var allowedForeignKeys = expectedTableConstraints?.AllowedForeignKeys
            ?? definition.ForeignKeys;

        var requiredForeignKeys = expectedTableConstraints?.RequiredForeignKeys
            ?? definition.ForeignKeys;

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
            AllUniqueConstraintsModeled(definition.Table, definition.Schema, allowedUniqueConstraints),
            AllCheckConstraintsModeled(definition.Table, definition.Schema, allowedCheckConstraints),
            AllForeignKeysModeled(definition.Table, definition.Schema, allowedForeignKeys),
            TableCommentMatches(definition),
        };

        if (definition.Columns.Count > 0)
        {
            conditions.Add(AllColumnsMatch(definition));
        }

        conditions.Add(BuildPrimaryKeyTransitionMatches(definition, expectedTableConstraints));

        // WHY: Each required constraint carries its own table and schema, so the set-oriented
        // check is emitted per owning relation rather than assuming the definition's own table.
        conditions.AddRange(requiredUniqueConstraints
            .GroupBy(static value => (value.Table, value.Schema))
            .Select(group => AllRequiredUniqueConstraintsSatisfied(
                group.Key.Table,
                group.Key.Schema,
                group.ToArray())));

        conditions.AddRange(requiredCheckConstraints
            .GroupBy(static value => (value.Table, value.Schema))
            .Select(group => AllRequiredCheckConstraintsSatisfied(
                group.Key.Table,
                group.Key.Schema,
                group.ToArray())));

        conditions.AddRange(requiredForeignKeys
            .GroupBy(static value => (value.Table, value.Schema))
            .Select(group => AllRequiredForeignKeysSatisfied(
                group.Key.Table,
                group.Key.Schema,
                group.ToArray())));

        return $"({string.Join(" AND ", conditions)})";
    }

    private string BuildPrimaryKeyTransitionMatches(
        ExpectedTableDefinition definition,
        SafeMigrationExpectedTableConstraints? expectedTableConstraints
    )
    {
        if (expectedTableConstraints is null)
        {
            return definition.PrimaryKey is null
                ? $"NOT {AnyConstraint(definition.Table, definition.Schema, 'p')}"
                : ConstraintColumnsSatisfied(
                    definition.Table,
                    definition.Schema,
                    definition.PrimaryKey.Name,
                    'p',
                    definition.PrimaryKey.Columns);
        }

        var allowed = expectedTableConstraints.AllowedPrimaryKeys
            .Select(primaryKey => ConstraintColumnsMatch(
                definition.Table,
                definition.Schema,
                'p',
                primaryKey.Columns,
                "TRUE"))
            .ToArray();

        if (!expectedTableConstraints.PrimaryKeyMayBeAbsent)
        {
            var primaryKey = expectedTableConstraints.AllowedPrimaryKeys.Single();

            return ConstraintColumnsSatisfied(
                definition.Table,
                definition.Schema,
                primaryKey.Name,
                'p',
                primaryKey.Columns);
        }

        return allowed.Length == 0
            ? $"NOT {AnyConstraint(definition.Table, definition.Schema, 'p')}"
            : $"(NOT {AnyConstraint(definition.Table, definition.Schema, 'p')} "
                + $"OR {string.Join(" OR ", allowed)})";
    }

    /// <summary>Checks that every actual unique constraint is modeled by an allowed one.</summary>
    /// <remarks>
    /// WHY: The matcher is identical for every allowed constraint apart from its column list, so
    /// correlating one lookup against a VALUES list of those lists keeps the catalog scans constant
    /// instead of repeating the lookup per allowed constraint. The matcher text itself is unchanged.
    /// </remarks>
    /// <param name="table">The owning table.</param>
    /// <param name="schema">The owning schema, or null for the current schema.</param>
    /// <param name="uniqueConstraints">The allowed unique constraints.</param>
    /// <returns>A predicate that is true when no unmodeled unique constraint exists.</returns>
    private string AllUniqueConstraintsModeled(
        string table,
        string? schema,
        IReadOnlyList<ExpectedUniqueConstraintDefinition> uniqueConstraints
    )
    {
        if (uniqueConstraints.Count == 0)
        {
            return AllConstraintsModeled(table, schema, 'u', []);
        }

        // WHY: Each allowed constraint carries its own owning relation, so one correlated matcher
        // is emitted per owner rather than assuming they all belong to the same table.
        var matchers = uniqueConstraints
            .GroupBy(static value => (value.Table, value.Schema))
            .Select(group =>
            {
                var rows = group.Select(value => $"({NameArray(value.Columns)})").ToArray();
                var matcher = ConstraintColumnsMatchQuery(
                    group.Key.Table,
                    group.Key.Schema,
                    'u',
                    "allowed.columns",
                    "co.conname = candidate_co.conname");

                return $"EXISTS (SELECT 1 FROM (VALUES {string.Join(", ", rows)}) AS allowed(columns) "
                    + $"WHERE EXISTS ({matcher}))";
            })
            .ToArray();

        return AllConstraintsModeled(table, schema, 'u', matchers);
    }

    /// <summary>Checks that every actual check constraint is modeled by an allowed one.</summary>
    /// <remarks>
    /// WHY: Only the expression comparison differs per allowed constraint, so one correlated
    /// matcher with a CASE arm per allowed expression keeps the catalog scans constant instead of
    /// opening one scan per allowed constraint.
    /// </remarks>
    /// <param name="table">The owning table.</param>
    /// <param name="schema">The owning schema, or null for the current schema.</param>
    /// <param name="checkConstraints">The allowed check constraints.</param>
    /// <returns>A predicate that is true when no unmodeled check constraint exists.</returns>
    private string AllCheckConstraintsModeled(
        string table,
        string? schema,
        IReadOnlyList<ExpectedCheckConstraintDefinition> checkConstraints
    )
    {
        if (checkConstraints.Count == 0)
        {
            return AllConstraintsModeled(table, schema, 'c', []);
        }

        var matchers = checkConstraints
            .GroupBy(static value => (value.Table, value.Schema))
            .Select(group =>
            {
                var items = group.ToArray();
                var rows = new List<string>(items.Length);
                var arms = new List<string>(items.Length);
                for (var index = 0; index < items.Length; index++)
                {
                    var ordinal = (index + 1).ToString(CultureInfo.InvariantCulture);
                    rows.Add($"({ordinal})");
                    arms.Add($"WHEN {ordinal} THEN {ExpectedCheckExpressionMatches(items[index])}");
                }

                var matcher = ConstraintRowsWithoutName(group.Key.Table, group.Key.Schema, 'c')
                    + " AND co.conname = candidate_co.conname"
                    + CheckFacets($"CASE allowed.ordinal {string.Join(" ", arms)} END");

                return $"EXISTS (SELECT 1 FROM (VALUES {string.Join(", ", rows)}) AS allowed(ordinal) "
                    + $"WHERE EXISTS ({matcher}))";
            })
            .ToArray();

        return AllConstraintsModeled(table, schema, 'c', matchers);
    }

    /// <summary>Checks that every actual foreign key is modeled by an allowed one.</summary>
    /// <remarks>
    /// WHY: Every expected part of a foreign-key match is a value, so one correlated matcher over
    /// a VALUES list of the allowed shapes keeps the catalog scans constant instead of opening one
    /// scan per allowed key.
    /// </remarks>
    /// <param name="table">The owning table.</param>
    /// <param name="schema">The owning schema, or null for the current schema.</param>
    /// <param name="foreignKeys">The allowed foreign keys.</param>
    /// <returns>A predicate that is true when no unmodeled foreign key exists.</returns>
    private string AllForeignKeysModeled(
        string table,
        string? schema,
        IReadOnlyList<ExpectedForeignKeyDefinition> foreignKeys
    )
    {
        if (foreignKeys.Count == 0)
        {
            return AllConstraintsModeled(table, schema, 'f', []);
        }

        var matchers = foreignKeys
            .GroupBy(static value => (value.Table, value.Schema))
            .Select(group =>
            {
                var rows = group
                    .Select(value => $"({NameArray(value.Columns)}, "
                        + $"{Literal(Qualified(value.PrincipalTable, value.PrincipalSchema))}, "
                        + $"{NameArray(value.PrincipalColumns)}, "
                        + $"CAST({Literal(ReferentialCode(value.OnUpdate))} AS pg_catalog.\"char\"), "
                        + $"CAST({Literal(ReferentialCode(value.OnDelete))} AS pg_catalog.\"char\"))")
                    .ToArray();

                var matcher = ConstraintRowsWithoutName(group.Key.Table, group.Key.Schema, 'f')
                    + " AND co.conname = candidate_co.conname"
                    + ForeignKeyFacets(
                        "allowed.columns",
                        "pg_catalog.to_regclass(allowed.principal)",
                        "allowed.principal_columns",
                        "allowed.confupdtype",
                        "allowed.confdeltype");

                return $"EXISTS (SELECT 1 FROM (VALUES {string.Join(", ", rows)}) AS allowed(columns, "
                    + "principal, principal_columns, confupdtype, confdeltype) "
                    + $"WHERE EXISTS ({matcher}))";
            })
            .ToArray();

        return AllConstraintsModeled(table, schema, 'f', matchers);
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
