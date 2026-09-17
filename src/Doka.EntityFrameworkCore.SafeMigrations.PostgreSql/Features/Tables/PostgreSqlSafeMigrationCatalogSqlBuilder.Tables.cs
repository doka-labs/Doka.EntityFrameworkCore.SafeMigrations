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

        var executionPostcondition = intent.Mode == SafeMigrationTableMode.ConvergenceContainer
            ? table
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

        for (var ordinal = 0; ordinal < definition.Columns.Count; ordinal++)
        {
            conditions.Add(
                ColumnMatches(definition.Table, definition.Schema, definition.Columns[ordinal], ordinal + 1));
        }

        conditions.Add(BuildPrimaryKeyTransitionMatches(definition, expectedTableConstraints));

        conditions.AddRange(
            requiredUniqueConstraints.Select(value => ConstraintColumnsSatisfied(
                value.Table,
                value.Schema,
                value.Name,
                'u',
                value.Columns)));

        conditions.AddRange(requiredCheckConstraints.Select(CheckSatisfied));
        conditions.AddRange(requiredForeignKeys.Select(ForeignKeySatisfied));

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

    private string AllUniqueConstraintsModeled(
        string table,
        string? schema,
        IReadOnlyList<ExpectedUniqueConstraintDefinition> uniqueConstraints
    ) => AllConstraintsModeled(
        table,
        schema,
        'u',
        uniqueConstraints
            .Select(uniqueConstraint => ConstraintColumnsMatch(
                uniqueConstraint.Table,
                uniqueConstraint.Schema,
                'u',
                uniqueConstraint.Columns,
                "co.conname = candidate_co.conname"))
            .ToArray());

    private string AllCheckConstraintsModeled(
        string table,
        string? schema,
        IReadOnlyList<ExpectedCheckConstraintDefinition> checkConstraints
    ) => AllConstraintsModeled(
        table,
        schema,
        'c',
        checkConstraints
            .Select(checkConstraint => CheckMatches(
                checkConstraint,
                "co.conname = candidate_co.conname"))
            .ToArray());

    private string AllForeignKeysModeled(
        string table,
        string? schema,
        IReadOnlyList<ExpectedForeignKeyDefinition> foreignKeys
    ) => AllConstraintsModeled(
        table,
        schema,
        'f',
        foreignKeys
            .Select(foreignKey => ForeignKeyMatches(
                foreignKey,
                "co.conname = candidate_co.conname"))
            .ToArray());

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
