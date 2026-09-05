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
        diagnosticEvidence: BuildForeignKeyDiagnosticEvidence(intent.Definition));

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
    ) => ConstraintBaseWithoutName(definition.Table, definition.Schema, 'f')
        + $" AND {namePredicate}"
        + StandardConstraintSemantics(requireLocalIdentity)
        + $" AND ARRAY(SELECT a.attname FROM unnest(co.conkey) WITH ORDINALITY AS key(attnum, ord) "
        + "JOIN pg_catalog.pg_attribute a ON a.attrelid = co.conrelid AND a.attnum = key.attnum "
        + $"ORDER BY key.ord) = {NameArray(definition.Columns)} "
        + $"AND co.confrelid = {QualifiedRegclass(definition.PrincipalTable, definition.PrincipalSchema)} "
        + $"AND ARRAY(SELECT a.attname FROM unnest(co.confkey) WITH ORDINALITY AS key(attnum, ord) "
        + "JOIN pg_catalog.pg_attribute a ON a.attrelid = co.confrelid AND a.attnum = key.attnum "
        + $"ORDER BY key.ord) = {NameArray(definition.PrincipalColumns)} "
        + $"AND co.confupdtype = {Literal(ReferentialCode(definition.OnUpdate))}::\"char\" "
        + $"AND co.confdeltype = {Literal(ReferentialCode(definition.OnDelete))}::\"char\" "
        + "AND co.confmatchtype = 's'::\"char\" "
        // A column-list SET NULL/DEFAULT action changes which dependent
        // columns are updated and is not expressible by the EF operation.
        + "AND (to_jsonb(co) ->> 'confdelsetcols') IS NULL)";

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
