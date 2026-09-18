namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private string ConstraintColumnsMatch(
        string table,
        string name,
        IReadOnlyList<string> columns,
        string type,
        bool requireExpectedName = true
    ) => ConstraintColumnsMatch(
        table,
        columns,
        type,
        $"tc.CONSTRAINT_NAME {(requireExpectedName ? "=" : "<>")} {Literal(name)}");

    private string ConstraintColumnsMatch(
        string table,
        IReadOnlyList<string> columns,
        string type,
        string namePredicate
    ) => $"EXISTS ({ConstraintColumnsMatchQuery(table, columns, type, namePredicate)})";

    private string ConstraintColumnsMatchQuery(
        string table,
        IReadOnlyList<string> columns,
        string type,
        string namePredicate
    ) => "SELECT tc.CONSTRAINT_NAME AS candidate_name FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc "
        + "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu "
        + "ON kcu.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA "
        + "AND kcu.TABLE_NAME = tc.TABLE_NAME AND kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME "
        + $"WHERE tc.CONSTRAINT_SCHEMA = DATABASE() AND tc.TABLE_NAME = {Literal(table)} "
        + $"AND {namePredicate} "
        + $"AND tc.CONSTRAINT_TYPE = {Literal(type)} "
        + $"GROUP BY tc.CONSTRAINT_NAME HAVING {OrderedConstraintColumnsMatch(columns, "kcu.COLUMN_NAME")}";

    private string DuplicateDataExists(
        string table,
        string? schema,
        IEnumerable<string> keys,
        string predicate
    )
    {
        var snapshot = keys.ToArray();

        return $"EXISTS (SELECT 1 FROM {Delimited(table, schema)} WHERE {predicate} "
            + $"GROUP BY {string.Join(", ", snapshot)} HAVING COUNT(*) > 1 LIMIT 1)";
    }

    private string ConstraintExists(
        string table,
        string name,
        string type
    ) => $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc "
        + $"WHERE tc.CONSTRAINT_SCHEMA = DATABASE() AND tc.TABLE_NAME = {Literal(table)} "
        + $"AND tc.CONSTRAINT_NAME = {Literal(name)} AND tc.CONSTRAINT_TYPE = {Literal(type)})";

    private string DatabaseConstraintNameExists(
        string name,
        string type
    ) => "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc "
        + "WHERE tc.CONSTRAINT_SCHEMA = DATABASE() "
        + $"AND tc.CONSTRAINT_NAME = {Literal(name)} "
        + $"AND tc.CONSTRAINT_TYPE = {Literal(type)})";

    private static string OrderedColumnsSql(
        IReadOnlyList<string> columns
    ) => string.Join(",", columns);
}
