namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private string ConstraintColumnsMatch(
        string table,
        string name,
        IReadOnlyList<string> columns,
        string type
    ) => $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc "
        + "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu "
        + "ON kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA "
        + "AND kcu.TABLE_NAME = tc.TABLE_NAME AND kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME "
        + $"WHERE tc.CONSTRAINT_SCHEMA = DATABASE() AND tc.TABLE_NAME = {Literal(table)} "
        + $"AND tc.CONSTRAINT_NAME = {Literal(name)} AND tc.CONSTRAINT_TYPE = {Literal(type)} "
        + $"GROUP BY tc.CONSTRAINT_NAME HAVING COUNT(*) = {columns.Count.ToString(CultureInfo.InvariantCulture)} "
        + $"AND GROUP_CONCAT(kcu.COLUMN_NAME ORDER BY kcu.ORDINAL_POSITION SEPARATOR ',') "
        + $"= {Literal(OrderedColumnsSql(columns))})";

    private string DuplicateDataExists(
        string table,
        IEnumerable<string> keys,
        string predicate
    )
    {
        var snapshot = keys.ToArray();

        return $"EXISTS (SELECT 1 FROM {Delimited(table)} WHERE {predicate} "
            + $"GROUP BY {string.Join(", ", snapshot)} HAVING COUNT(*) > 1 LIMIT 1)";
    }

    private string ConstraintExists(
        string table,
        string name,
        string type
    ) => $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc "
        + $"WHERE tc.CONSTRAINT_SCHEMA = DATABASE() AND tc.TABLE_NAME = {Literal(table)} "
        + $"AND tc.CONSTRAINT_NAME = {Literal(name)} AND tc.CONSTRAINT_TYPE = {Literal(type)})";

    private static string OrderedColumnsSql(
        IReadOnlyList<string> columns,
        string expression = "kcu.COLUMN_NAME"
    )
    {
        _ = expression;
        return string.Join(",", columns);
    }
}
