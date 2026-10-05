namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Proves all captured builtin store types through one metadata join for a physical table.</summary>
    private string ModelManagedTableStoreTypesMatch(
        string table,
        string schema,
        List<(string Column, string StoreType)> columns
    )
    {
        // WHY: A derived VALUES relation avoids compiling one correlated
        // metadata EXISTS per column in both delayed guard scopes. Duplicate
        // requirements remain rows, so every original conjunction is preserved.
        var sql = new StringBuilder(768 + columns.Count * 96)
            .Append("NOT EXISTS (SELECT 1 FROM (VALUES ");

        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            if (!TryParseStoreType(column.StoreType, out var type))
            {
                return "1 = 0";
            }

            if (index != 0)
            {
                sql.Append(", ");
            }

            sql.Append('(')
                .Append(Literal(column.Column))
                .Append(", ")
                .Append(Literal(type.Name))
                .Append(", ");

            AppendTableColumnOptionalFacet(sql, type.Length);
            sql.Append(", ");
            AppendTableColumnOptionalFacet(sql, type.Precision);
            sql.Append(", ");
            AppendTableColumnOptionalFacet(sql, type.Scale);
            sql.Append(')');
        }

        sql.Append(") expected(name, type_name, max_length, precision_value, scale_value) ")
            .Append("LEFT JOIN sys.columns c ON c.object_id = ")
            .Append(TableId(table, schema))
            .Append(" AND c.name = expected.name ")
            .Append("LEFT JOIN sys.types ty ON ty.user_type_id = c.user_type_id ")
            // WHY: NOT(predicate) loses UNKNOWN rows from absent columns or
            // unavailable metadata. A positive CASE accepts only the complete
            // original EXISTS proof, including builtin type provenance.
            .Append("WHERE CASE WHEN c.column_id IS NOT NULL ")
            .Append("AND ty.name = expected.type_name AND ty.is_user_defined = 0 AND ty.is_assembly_type = 0 ")
            .Append("AND (expected.max_length IS NULL OR c.max_length = expected.max_length) ")
            .Append("AND (expected.precision_value IS NULL OR c.precision = expected.precision_value) ")
            .Append("AND (expected.scale_value IS NULL OR c.scale = expected.scale_value) ")
            .Append("THEN 0 ELSE 1 END = 1)");

        return sql.ToString();
    }
}
