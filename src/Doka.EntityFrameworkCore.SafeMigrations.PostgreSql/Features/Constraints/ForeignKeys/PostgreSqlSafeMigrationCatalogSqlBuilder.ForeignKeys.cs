namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureForeignKey(
        EnsureForeignKeyIntent intent
    ) => BuildEnsureConstraint(
        intent.Definition.Table,
        intent.Definition.Schema,
        intent.Definition.Name,
        'f',
        ForeignKeyMatches(intent.Definition, requireExpectedName: true),
        ForeignKeyDataBlocked(intent.Definition),
        TableExists(intent.Definition.PrincipalTable, intent.Definition.PrincipalSchema),
        ForeignKeyMatches(intent.Definition, requireExpectedName: false),
        ForeignKeyMatches(
            intent.Definition,
            requireExpectedName: false,
            requireLocalIdentity: false),
        diagnosticEvidence: BuildForeignKeyDiagnosticEvidence(intent.Definition),
        semanticCandidates: ForeignKeyMatchQuery(
            intent.Definition,
            $"co.conname <> {Literal(intent.Definition.Name)}"));

    private PostgreSqlSafeMigrationRuntimePlan BuildDropForeignKey(
        DropForeignKeyIntent intent
    ) => BuildDropConstraint(intent.Table, intent.Schema, intent.Name, 'f');

    private string ForeignKeyDataBlocked(
        ExpectedForeignKeyDefinition definition
    )
    {
        var localNotNull = string.Join(
            " AND ",
            definition.Columns.Select(column => $"d.{Delimited(column)} IS NOT NULL"));

        var join = string.Join(
            " AND ",
            definition.Columns.Zip(
                definition.PrincipalColumns,
                (local, principal) => $"d.{Delimited(local)} = p.{Delimited(principal)}"));

        return $"EXISTS (SELECT 1 FROM {Qualified(definition.Table, definition.Schema)} d "
            + $"LEFT JOIN {Qualified(definition.PrincipalTable, definition.PrincipalSchema)} p ON {join} "
            + $"WHERE {localNotNull} AND p.{Delimited(definition.PrincipalColumns[0])} IS NULL LIMIT 1)";
    }

    private string ForeignKeyMatches(
        ExpectedForeignKeyDefinition definition,
        bool requireExpectedName,
        bool requireLocalIdentity = true
    ) => ForeignKeyMatches(
        definition,
        $"co.conname {(requireExpectedName ? "=" : "<>")} {Literal(definition.Name)}",
        requireLocalIdentity);

    private string ForeignKeyMatches(
        ExpectedForeignKeyDefinition definition,
        string namePredicate,
        bool requireLocalIdentity = true
    ) => $"EXISTS ({ForeignKeyMatchQuery(definition, namePredicate, requireLocalIdentity)})";

    private string ForeignKeyMatchQuery(
        ExpectedForeignKeyDefinition definition,
        string namePredicate,
        bool requireLocalIdentity = true
    ) => ConstraintRowsWithoutName(definition.Table, definition.Schema, 'f')
        + $" AND {namePredicate}"
        + ForeignKeyFacets(
            NameArray(definition.Columns),
            QualifiedRegclass(definition.PrincipalTable, definition.PrincipalSchema),
            NameArray(definition.PrincipalColumns),
            $"{Literal(ReferentialCode(definition.OnUpdate))}::\"char\"",
            $"{Literal(ReferentialCode(definition.OnDelete))}::\"char\"",
            requireLocalIdentity);

    /// <summary>Builds the foreign-key facet predicates against expected-value expressions.</summary>
    /// <remarks>
    /// Every expected part is a value, so the same predicates serve a single key and a whole
    /// correlated set. Keeping one definition here prevents the set forms from drifting away
    /// from the per-key form.
    /// </remarks>
    /// <param name="columns">SQL yielding the expected ordered local column names.</param>
    /// <param name="principal">SQL yielding the expected referenced relation.</param>
    /// <param name="principalColumns">SQL yielding the expected ordered referenced column names.</param>
    /// <param name="onUpdate">SQL yielding the expected update action code.</param>
    /// <param name="onDelete">SQL yielding the expected delete action code.</param>
    /// <param name="requireLocalIdentity">Whether local ownership is part of the identity.</param>
    /// <returns>The facet predicates, each prefixed with AND.</returns>
    private static string ForeignKeyFacets(
        string columns,
        string principal,
        string principalColumns,
        string onUpdate,
        string onDelete,
        bool requireLocalIdentity = true
    ) => StandardConstraintSemantics(requireLocalIdentity)
        + " AND ARRAY(SELECT a.attname FROM unnest(co.conkey) WITH ORDINALITY AS key(attnum, ord) "
        + "JOIN pg_catalog.pg_attribute a ON a.attrelid = co.conrelid AND a.attnum = key.attnum "
        + $"ORDER BY key.ord) = {columns} "
        + $"AND co.confrelid = {principal} "
        + "AND ARRAY(SELECT a.attname FROM unnest(co.confkey) WITH ORDINALITY AS key(attnum, ord) "
        + "JOIN pg_catalog.pg_attribute a ON a.attrelid = co.confrelid AND a.attnum = key.attnum "
        + $"ORDER BY key.ord) = {principalColumns} "
        + $"AND co.confupdtype = {onUpdate} "
        + $"AND co.confdeltype = {onDelete} "
        + "AND co.confmatchtype = 's'::\"char\" "
        // A column-list SET NULL/DEFAULT action changes which dependent
        // columns are updated and is not expressible by the EF operation.
        + "AND (to_jsonb(co) ->> 'confdelsetcols') IS NULL";

    private string BuildForeignKeyDiagnosticEvidence(
        ExpectedForeignKeyDefinition definition
    )
    {
        var expectedColumns = string.Join(",", definition.Columns);
        var expectedPrincipalColumns = string.Join(",", definition.PrincipalColumns);
        var actualColumns = "array_to_string(ARRAY(SELECT a.attname "
            + "FROM unnest(co.conkey) WITH ORDINALITY AS key(attnum, ord) "
            + "JOIN pg_catalog.pg_attribute a "
            + "ON a.attrelid = co.conrelid AND a.attnum = key.attnum ORDER BY key.ord), ',')";

        var actualPrincipalColumns = "array_to_string(ARRAY(SELECT a.attname "
            + "FROM unnest(co.confkey) WITH ORDINALITY AS key(attnum, ord) "
            + "JOIN pg_catalog.pg_attribute a "
            + "ON a.attrelid = co.confrelid AND a.attnum = key.attnum ORDER BY key.ord), ',')";

        var columnDiagnostics = BoundedOrderedListDiagnostics(expectedColumns, actualColumns);
        var principalColumnDiagnostics = BoundedOrderedListDiagnostics(
            expectedPrincipalColumns,
            actualPrincipalColumns);

        var records = new[]
        {
            DiagnosticRecord(
                $"ARRAY(SELECT a.attname FROM unnest(co.conkey) WITH ORDINALITY AS key(attnum, ord) "
                    + "JOIN pg_catalog.pg_attribute a "
                    + "ON a.attrelid = co.conrelid AND a.attnum = key.attnum "
                    + $"ORDER BY key.ord) <> {NameArray(definition.Columns)}",
                "foreign_key_column_order",
                columnDiagnostics.Expected,
                columnDiagnostics.Actual),
            DiagnosticRecord(
                $"ARRAY(SELECT a.attname FROM unnest(co.confkey) WITH ORDINALITY AS key(attnum, ord) "
                    + "JOIN pg_catalog.pg_attribute a "
                    + "ON a.attrelid = co.confrelid AND a.attnum = key.attnum "
                    + $"ORDER BY key.ord) <> {NameArray(definition.PrincipalColumns)}",
                "foreign_key_principal_column_order",
                principalColumnDiagnostics.Expected,
                principalColumnDiagnostics.Actual),
            DiagnosticRecord(
                $"co.confdeltype <> {Literal(ReferentialCode(definition.OnDelete))}::\"char\"",
                "foreign_key_delete_behavior",
                Literal(ReferentialDiagnosticCode(definition.OnDelete)),
                ReferentialDiagnosticSql("co.confdeltype")),
            DiagnosticRecord(
                $"co.confupdtype <> {Literal(ReferentialCode(definition.OnUpdate))}::\"char\"",
                "foreign_key_update_behavior",
                Literal(ReferentialDiagnosticCode(definition.OnUpdate)),
                ReferentialDiagnosticSql("co.confupdtype")),
        };

        return "(SELECT NULLIF(concat_ws(chr(30), "
            + string.Join(", ", records)
            + "), '') FROM pg_catalog.pg_constraint co "
            + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
            + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
            + $"WHERE n.nspname = {SchemaExpression(definition.Schema)} "
            + $"AND c.relname = {Literal(definition.Table)} "
            + $"AND co.conname = {Literal(definition.Name)} AND co.contype = 'f'::\"char\" LIMIT 1)";
    }

    private (string Expected, string Actual) BoundedOrderedListDiagnostics(
        string expected,
        string actualExpression
    ) => expected.Length <= SafeMigrationFacetDifference.MaximumValueLength
        ? (Literal(expected), $"LEFT({actualExpression}, {SafeMigrationFacetDifference.MaximumValueLength})")
        : (
            $"'md5:' || pg_catalog.md5({Literal(expected)})",
            $"'md5:' || pg_catalog.md5({actualExpression})");

    private static string ReferentialDiagnosticCode(
        ReferentialAction action
    ) => action switch
    {
        ReferentialAction.NoAction => "no_action",
        ReferentialAction.Restrict => "restrict",
        ReferentialAction.Cascade => "cascade",
        ReferentialAction.SetNull => "set_null",
        ReferentialAction.SetDefault => "set_default",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static string ReferentialDiagnosticSql(
        string expression
    ) => $"CASE {expression} WHEN 'a'::\"char\" THEN 'no_action' WHEN 'r'::\"char\" THEN 'restrict' "
        + "WHEN 'c'::\"char\" THEN 'cascade' WHEN 'n'::\"char\" THEN 'set_null' "
        + "WHEN 'd'::\"char\" THEN 'set_default' ELSE 'unknown' END";

    /// <summary>Verifies every required foreign key of one table through three catalog scans.</summary>
    /// <remarks>
    /// WHY: Every expected part of a foreign-key match is a value, so the whole set can be driven
    /// from a VALUES list and the three correlated catalog scans are emitted once instead of once
    /// per required key. A predicate can evaluate to NULL, so the absent test coalesces to FALSE
    /// to keep the mismatch verdict the per-key form produced.
    /// </remarks>
    /// <param name="table">The table owning the required foreign keys.</param>
    /// <param name="schema">The owning schema, or null for the current schema.</param>
    /// <param name="required">The required foreign keys, all owned by that table.</param>
    /// <returns>A predicate that is true when every required foreign key is satisfied.</returns>
    private string AllRequiredForeignKeysSatisfied(
        string table,
        string? schema,
        IReadOnlyList<ExpectedForeignKeyDefinition> required
    )
    {
        var rows = required
            .Select(value => $"(CAST({Literal(value.Name)} AS pg_catalog.name), {NameArray(value.Columns)}, "
                + $"{Literal(Qualified(value.PrincipalTable, value.PrincipalSchema))}, "
                + $"{NameArray(value.PrincipalColumns)}, "
                + $"CAST({Literal(ReferentialCode(value.OnUpdate))} AS pg_catalog.\"char\"), "
                + $"CAST({Literal(ReferentialCode(value.OnDelete))} AS pg_catalog.\"char\"))")
            .ToArray();

        var facets = ForeignKeyFacets(
            "required.columns",
            "pg_catalog.to_regclass(required.principal)",
            "required.principal_columns",
            "required.confupdtype",
            "required.confdeltype");

        var rowSource = ConstraintRowsWithoutName(table, schema, 'f');
        var exact = $"EXISTS ({rowSource} AND co.conname = required.conname{facets})";
        var exists = $"EXISTS ({rowSource} AND co.conname = required.conname)";
        var alias = $"EXISTS ({rowSource} AND co.conname <> required.conname{facets})";

        return $"NOT EXISTS (SELECT 1 FROM (VALUES {string.Join(", ", rows)}) AS required(conname, columns, "
            + "principal, principal_columns, confupdtype, confdeltype) WHERE NOT COALESCE("
            + $"({exact}) OR (NOT ({exists}) AND ({alias})), FALSE))";
    }

    private string ForeignKeySatisfied(
        ExpectedForeignKeyDefinition definition
    )
    {
        var exists = ConstraintExists(definition.Table, definition.Schema, definition.Name, 'f');
        var exact = ForeignKeyMatches(definition, requireExpectedName: true);
        var semanticAlias = ForeignKeyMatches(definition, requireExpectedName: false);

        return $"({exact}) OR (NOT ({exists}) AND ({semanticAlias}))";
    }

    private static string ReferentialCode(
        ReferentialAction action
    ) => action switch
    {
        ReferentialAction.NoAction => "a",
        ReferentialAction.Restrict => "r",
        ReferentialAction.Cascade => "c",
        ReferentialAction.SetNull => "n",
        ReferentialAction.SetDefault => "d",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
