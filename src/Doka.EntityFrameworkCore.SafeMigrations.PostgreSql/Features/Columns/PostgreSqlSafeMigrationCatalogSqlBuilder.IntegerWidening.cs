namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private const string IntegerWideningCatalogsReadable =
        "pg_catalog.has_table_privilege('pg_catalog.pg_depend', 'SELECT') "
        + "AND pg_catalog.has_table_privilege('pg_catalog.pg_inherits', 'SELECT') "
        + "AND pg_catalog.has_table_privilege('pg_catalog.pg_am', 'SELECT') "
        + "AND pg_catalog.has_table_privilege('pg_catalog.pg_opclass', 'SELECT') "
        + "AND pg_catalog.has_table_privilege('pg_catalog.pg_constraint', 'SELECT') "
        + "AND pg_catalog.has_table_privilege('pg_catalog.pg_index', 'SELECT')";

    /// <summary>Recognizes only the lossless built-in signed-integer lattice with unchanged ancillary facets.</summary>
    internal static bool IsIntegerWidening(
        AlterColumnIntent intent
    )
    {
        if (intent.OldDefinition is not { } source)
        {
            return false;
        }

        var target = intent.Definition;
        var sourceRank = IntegerWidth(source);
        var targetRank = IntegerWidth(target);

        // WHY: Range inclusion proves every existing value fits without scanning
        // rows. Domains, user casts, generated values and default rewrites have
        // separate execution semantics and cannot borrow this built-in proof.
        return sourceRank != 0
            && sourceRank < targetRank
            && StringComparer.Ordinal.Equals(source.Name, target.Name)
            && source.IsNullable == target.IsNullable
            && source.IsUnicode == target.IsUnicode
            && source.MaxLength == target.MaxLength
            && source.IsFixedLength == target.IsFixedLength
            && source.IsRowVersion == target.IsRowVersion
            && source.Precision == target.Precision
            && source.Scale == target.Scale
            && Equals(source.Collation, target.Collation)
            && StringComparer.Ordinal.Equals(source.Comment, target.Comment)
            && source.DefaultValue.Kind == SafeMigrationDefaultValueKind.None
            && target.DefaultValue.Kind == SafeMigrationDefaultValueKind.None
            && source.CapturedComputedSql is null
            && target.CapturedComputedSql is null
            && PostgreSqlSafeMigrationColumnMetadata.CanSafelyConverge(source)
            && PostgreSqlSafeMigrationColumnMetadata.CanSafelyConverge(target)
            && PostgreSqlSafeMigrationColumnMetadata.GetValueGenerationStrategy(source) is null
                or NpgsqlValueGenerationStrategy.None
            && PostgreSqlSafeMigrationColumnMetadata.GetValueGenerationStrategy(target) is null
                or NpgsqlValueGenerationStrategy.None;
    }

    /// <summary>Binds one built-in store width to its exact CLR representation without type inference.</summary>
    private static int IntegerWidth(
        ExpectedColumnDefinition definition
    ) => definition.StoreType switch
    {
        "smallint" when definition.ClrType == typeof(short) => 1,
        "integer" when definition.ClrType == typeof(int) => 2,
        "bigint" when definition.ClrType == typeof(long) => 3,
        _ => 0,
    };

    /// <summary>Rejects dependencies needing a separate execution or relational-equality proof.</summary>
    private string IntegerWideningDependenciesAllow(
        AlterColumnIntent intent
    ) => "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_attribute widening_column "
        + "JOIN pg_catalog.pg_class widening_table ON widening_table.oid = widening_column.attrelid "
        + "JOIN pg_catalog.pg_namespace widening_schema ON widening_schema.oid = widening_table.relnamespace "
        + $"WHERE widening_schema.nspname = {SchemaExpression(intent.Schema)} "
        + $"AND widening_table.relname = {Literal(intent.Table)} "
        + $"AND widening_column.attname = {Literal(intent.Definition.Name)} AND ("
        + "widening_table.relkind <> 'r' "
        + "OR widening_column.atttypid <> "
        + IntegerTypeOid(intent.OldDefinition!)
        + " OR EXISTS (SELECT 1 FROM pg_catalog.pg_inherits widening_inheritance "
        + "WHERE widening_inheritance.inhrelid = widening_table.oid "
        + "OR widening_inheritance.inhparent = widening_table.oid) "
        + "OR EXISTS (SELECT 1 FROM pg_catalog.pg_constraint widening_fk WHERE widening_fk.contype = 'f' "
        + "AND ((widening_fk.conrelid = widening_table.oid AND widening_column.attnum = ANY(widening_fk.conkey)) "
        + "OR (widening_fk.confrelid = widening_table.oid "
        + "AND widening_column.attnum = ANY(widening_fk.confkey)))) "
        + "OR EXISTS (SELECT 1 FROM pg_catalog.pg_depend widening_dependency "
        + "WHERE widening_dependency.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass "
        + "AND widening_dependency.refobjid = widening_table.oid "
        + "AND widening_dependency.refobjsubid = widening_column.attnum "
        // WHY: Native ALTER TYPE rebuilds only indexes, constraints and
        // extended statistics. Exact-column procedure/trigger/policy/
        // publication and unknown dependency classes cannot be re-bound.
        // Whole-owner references such as COUNT(*) are not column proofs.
        + "AND (widening_dependency.classid NOT IN ('pg_catalog.pg_class'::pg_catalog.regclass, "
        + "'pg_catalog.pg_constraint'::pg_catalog.regclass, 'pg_catalog.pg_statistic_ext'::pg_catalog.regclass) "
        + "OR widening_dependency.classid = 'pg_catalog.pg_constraint'::pg_catalog.regclass "
        + "AND EXISTS (SELECT 1 FROM pg_catalog.pg_constraint whole_row_check "
        + "WHERE whole_row_check.oid = widening_dependency.objid AND whole_row_check.contype = 'c') "
        + "OR widening_dependency.classid = 'pg_catalog.pg_class'::pg_catalog.regclass "
        + "AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class dependent_relation "
        + "WHERE dependent_relation.oid = widening_dependency.objid "
        + "AND dependent_relation.relkind IN ('i', 'I', 'S')))) "
        // WHY: A true whole-row CHECK can change truth without a native
        // ALTER failure (row size or an opaque function can inspect types).
        // PostgreSQL represents constants and whole-row checks with the
        // same owner edge. Native canonical constants and direct null tests
        // retain their truth; unrelated-column checks have no such edge.
        + "OR EXISTS (SELECT 1 FROM pg_catalog.pg_depend whole_owner_dependency "
        + "JOIN pg_catalog.pg_constraint whole_owner_check "
        + "ON whole_owner_check.oid = whole_owner_dependency.objid "
        + "WHERE whole_owner_dependency.classid = 'pg_catalog.pg_constraint'::pg_catalog.regclass "
        + "AND whole_owner_dependency.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass "
        + "AND whole_owner_dependency.refobjid = widening_table.oid "
        + "AND whole_owner_dependency.refobjsubid = 0 "
        + "AND whole_owner_check.conrelid = widening_table.oid AND whole_owner_check.contype = 'c' "
        + "AND NOT COALESCE(pg_catalog.pg_get_expr(whole_owner_check.conbin, whole_owner_check.conrelid) IN ("
        + "'true', 'false', 'NULL::boolean', "
        + "pg_catalog.format('(%I.* IS NOT NULL)', widening_table.relname), "
        + "pg_catalog.format('(%I.* IS NULL)', widening_table.relname)), FALSE)) "
        + "OR EXISTS (SELECT 1 FROM pg_catalog.pg_constraint widening_check "
        + "WHERE widening_check.conrelid = widening_table.oid AND widening_check.contype = 'c' "
        + "AND widening_column.attnum = ANY(widening_check.conkey)) "
        + "OR EXISTS (SELECT 1 FROM pg_catalog.pg_index widening_index "
        + "JOIN pg_catalog.pg_class widening_index_owner ON widening_index_owner.oid = widening_index.indexrelid "
        + "JOIN pg_catalog.pg_am widening_am ON widening_am.oid = widening_index_owner.relam "
        + "WHERE widening_index.indrelid = widening_table.oid "
        + "AND (widening_column.attnum = ANY(widening_index.indkey) "
        + "OR EXISTS (SELECT 1 FROM pg_catalog.pg_depend index_dependency "
        + "WHERE index_dependency.classid = 'pg_catalog.pg_class'::pg_catalog.regclass "
        + "AND index_dependency.objid = widening_index.indexrelid "
        + "AND index_dependency.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass "
        + "AND index_dependency.refobjid = widening_table.oid "
        + "AND index_dependency.refobjsubid = widening_column.attnum)) "
        + "AND (widening_am.amname <> 'btree' OR widening_index.indexprs IS NOT NULL "
        + "OR widening_index.indpred IS NOT NULL "
        + "OR NOT widening_index.indisvalid OR NOT widening_index.indisready "
        + "OR widening_index.indisexclusion "
        + "OR EXISTS (SELECT 1 FROM pg_catalog.unnest(widening_index.indclass) role(opclass) "
        + "JOIN pg_catalog.pg_opclass op ON op.oid = role.opclass "
        + "JOIN pg_catalog.pg_namespace op_schema ON op_schema.oid = op.opcnamespace "
        + "WHERE NOT op.opcdefault OR op_schema.nspname <> 'pg_catalog')))))";

    /// <summary>Uses fixed native OIDs so a user domain or search-path alias cannot impersonate a built-in.</summary>
    private static string IntegerTypeOid(
        ExpectedColumnDefinition definition
    ) => IntegerWidth(definition) switch
    {
        1 => "21",
        2 => "23",
        3 => "20",
        _ => throw new UnreachableException(),
    };
}
