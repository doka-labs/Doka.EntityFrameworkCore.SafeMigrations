namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Matches every authored column through one bounded catalog join.</summary>
    private string TableColumnsMatch(
        ExpectedTableDefinition definition
    )
    {
        // WHY: Derived VALUES has no INSERT row-count limit. One catalog
        // join preserves the 1,024-column contract without compiling a
        // separate correlated column/default/identity probe for every row.
        var sql = new StringBuilder(2048 + definition.Columns.Count * 192)
            .Append("NOT EXISTS (SELECT 1 FROM (VALUES ");

        for (var index = 0; index < definition.Columns.Count; index++)
        {
            var column = definition.Columns[index];
            if (!TryGetColumnType(column, out var type)
                || !TryGetIdentity(column, out var identity, out var seed, out var increment))
            {
                return "1 = 0";
            }

            if (index != 0)
            {
                sql.Append(", ");
            }

            sql.Append('(')
                .Append(Literal(column.Name))
                .Append(", ")
                .Append(Literal(type.Name))
                .Append(column.IsNullable ? ", 1" : ", 0")
                .Append(identity ? ", 1, " : ", 0, ");

            AppendTableColumnOptionalFacet(sql, type.Length);
            sql.Append(", ");
            AppendTableColumnOptionalFacet(sql, type.Precision);
            sql.Append(", ");
            AppendTableColumnOptionalFacet(sql, type.Scale);
            sql.Append(type.IsCharacter ? ", 1, " : ", 0, ");
            sql.Append(!type.IsCharacter
                ? "CAST(NULL AS nvarchar(128))"
                : column.Collation is null
                    ? "CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))"
                    : Literal(column.Collation.Name));

            sql.Append(", ");
            AppendTableColumnIdentityFacet(sql, identity ? seed : null);
            sql.Append(", ");
            AppendTableColumnIdentityFacet(sql, identity ? increment : null);
            sql.Append(", ");
            if (column.DefaultValue.Kind == SafeMigrationDefaultValueKind.None)
            {
                sql.Append("CAST(NULL AS nvarchar(64))");
            }
            else
            {
                sql.Append(Literal(ContractFingerprint("default", definition.Schema, definition.Table, column.Name,
                    DefaultExpression(column))));
            }

            sql.Append(')');
        }

        sql.Append(") expected(name, type_name, is_nullable, is_identity, max_length, precision_value, scale_value, ")
            .Append("is_character, collation_name, identity_seed, identity_increment, default_fingerprint) ")
            .Append("LEFT JOIN sys.columns c ON c.object_id = ")
            .Append(TableId(definition.Table, definition.Schema))
            .Append(" AND c.name = expected.name ")
            .Append("LEFT JOIN sys.types ty ON ty.user_type_id = c.user_type_id ")
            .Append("LEFT JOIN sys.identity_columns identity_column ON identity_column.object_id = c.object_id ")
            .Append("AND identity_column.column_id = c.column_id ")
            .Append("LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id ")
            .Append("AND dc.parent_column_id = c.column_id ")
            .Append("LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = dc.object_id ")
            .Append("AND ep.minor_id = 0 AND ep.name = N'Doka:SafeMigrations:Contract' ")
            // WHY: NOT(predicate) would discard UNKNOWN. CASE treats absent
            // columns, missing identity metadata, and missing stamps as a
            // mismatch, preserving the original positive EXISTS semantics.
            .Append("WHERE CASE WHEN c.column_id IS NOT NULL ")
            .Append("AND ty.name = expected.type_name AND ty.is_user_defined = 0 AND ty.is_assembly_type = 0 ")
            .Append("AND c.is_nullable = expected.is_nullable AND c.is_identity = expected.is_identity ")
            .Append("AND c.is_computed = 0 AND c.is_sparse = 0 AND c.is_column_set = 0 AND c.is_rowguidcol = 0 ")
            .Append("AND c.is_filestream = 0 AND c.is_hidden = 0 AND c.generated_always_type = 0 ")
            .Append("AND (expected.max_length IS NULL OR c.max_length = expected.max_length) ")
            .Append("AND (expected.precision_value IS NULL OR c.precision = expected.precision_value) ")
            .Append("AND (expected.scale_value IS NULL OR c.scale = expected.scale_value) ")
            .Append("AND (expected.is_character = 0 OR c.collation_name = expected.collation_name) ")
            .Append("AND (expected.is_identity = 0 OR (identity_column.is_not_for_replication = 0 ")
            .Append("AND TRY_CONVERT(decimal(38,0), identity_column.seed_value) = expected.identity_seed ")
            .Append("AND TRY_CONVERT(decimal(38,0), identity_column.increment_value) = expected.identity_increment)) ")
            .Append("AND ((expected.default_fingerprint IS NULL AND c.default_object_id = 0) ")
            .Append("OR (expected.default_fingerprint IS NOT NULL AND dc.object_id IS NOT NULL ")
            .Append("AND CONVERT(nvarchar(64), ep.value) = expected.default_fingerprint ")
            .Append("COLLATE Latin1_General_100_BIN2)) THEN 0 ELSE 1 END = 1)");

        return sql.ToString();
    }

    /// <summary>Types optional numeric facets so absent VALUES entries cannot change inference.</summary>
    private static void AppendTableColumnOptionalFacet(
        StringBuilder sql,
        int? value
    )
    {
        sql.Append(value is null ? "CAST(NULL AS int)" : value.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Preserves full signed identity values without narrowing the catalog conversion.</summary>
    private static void AppendTableColumnIdentityFacet(
        StringBuilder sql,
        long? value
    )
    {
        sql.Append("CAST(")
            .Append(value is null ? "NULL" : value.Value.ToString(CultureInfo.InvariantCulture))
            .Append(" AS decimal(38,0))");
    }
}
