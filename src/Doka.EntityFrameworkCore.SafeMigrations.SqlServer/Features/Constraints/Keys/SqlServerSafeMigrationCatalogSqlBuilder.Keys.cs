namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private bool InlineKeyWidthIsSupported(
        ExpectedTableDefinition definition,
        IReadOnlyList<string> columns,
        int maximumBytes
    )
    {
        if (columns.Count > 32)
        {
            return false;
        }

        var width = 0;
        foreach (var name in columns)
        {
            var column = definition.Columns.Single(column => column.Name == name);
            if (!TryGetColumnType(column, out var type))
            {
                return false;
            }

            var bytes = type.Length is { } length ? length : type.Name switch
            {
                "bigint" or "datetime" or "datetime2" or "float" or "money" or "timestamp" => 8,
                "datetimeoffset" => 10,
                "date" => 3,
                "bit" or "tinyint" => 1,
                "int" or "real" or "smalldatetime" or "smallmoney" => 4,
                "smallint" => 2,
                "time" => 5,
                "decimal" or "numeric" => type.Precision switch
                {
                    <= 9 => 5,
                    <= 19 => 9,
                    <= 28 => 13,
                    _ => 17,
                },
                "uniqueidentifier" => 16,
                _ => 0,
            };

            if (bytes <= 0 || width + bytes > maximumBytes)
            {
                return false;
            }

            width += bytes;
        }

        return true;
    }

    private SqlServerSafeMigrationRuntimePlan BuildEnsureKey(
        string table,
        string? schema,
        string name,
        IReadOnlyList<string> columns,
        string kind
    )
    {
        var tableExists = TableExists(table, schema);
        var occupied = ConstraintNameOccupiedInSchema(schema, name);
        var existingPrimaryKey = kind == "PK"
            ? $"EXISTS (SELECT 1 FROM sys.key_constraints kc WHERE kc.parent_object_id = {TableId(table, schema)} "
                + "AND kc.type = 'PK')"
            : "1 = 0";

        var matching = KeyMatches(table, schema, name, columns, kind);
        var width = columns.Count <= 32
            ? IndexKeysWithinLimit(table, schema, columns, kind == "PK" ? 900 : 1700)
            : "1 = 0";

        var nullableColumns = kind == "PK"
            ? $"EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = {TableId(table, schema)} "
                + $"AND c.name IN ({string.Join(", ", columns.Select(Literal))}) AND c.is_nullable = 1)"
            : "1 = 0";

        var keys = string.Join(", ", columns.Select(Delimited));
        var duplicates = $"EXISTS (SELECT TOP (1) 1 FROM {QualifiedTable(table, schema)} "
            + $"GROUP BY {keys} HAVING COUNT_BIG(*) > 1)";

        var nulls = kind == "PK"
            ? $"EXISTS (SELECT TOP (1) 1 FROM {QualifiedTable(table, schema)} WHERE "
                + string.Join(" OR ", columns.Select(column => $"{Delimited(column)} IS NULL")) + ")"
            : "1 = 0";

        return Plan(
            $"CASE WHEN NOT {tableExists} THEN N'prerequisite_missing' "
            + $"WHEN {matching} THEN N'matching' WHEN {occupied} OR {existingPrimaryKey} THEN N'different' "
            + $"WHEN {nullableColumns} OR NOT ({width}) THEN N'unsupported' "
            + $"WHEN {duplicates} OR {nulls} THEN N'data_blocked' ELSE N'missing' END",
            Bit(matching)) with
        {
            RequiresDelayedBinding = true,
            // WHY: Empty or presently non-null rows do not change a nullable
            // physical column into a legal SQL Server primary-key column.
            ClassificationCodeExpression = kind == "PK"
                ? $"CASE WHEN {nullableColumns} THEN N'primary_key_nullable_column' "
                    + $"WHEN ({occupied} OR {existingPrimaryKey}) AND NOT ({matching}) THEN "
                    + $"CASE WHEN {duplicates} OR {nulls} THEN N'primary_key_replacement_data_blocked' "
                    + "ELSE N'key_replacement_data_safe' END ELSE NULL END"
                : $"CASE WHEN {occupied} AND NOT ({matching}) THEN "
                    + $"CASE WHEN {duplicates} THEN N'unique_constraint_replacement_data_blocked' "
                    + "ELSE N'key_replacement_data_safe' END ELSE NULL END",
            // WHY: Grouping physically unkeyable columns can fail during SQL
            // binding even when a CASE arm would later reject the target key.
            StateEvaluationGuardExpression = Bit(width),
            StateEvaluationGuardFailureExpression = "N'unsupported'",
            MatchedObjectNameExpression = $"CASE WHEN {matching} THEN {Literal(name)} ELSE NULL END",
        };
    }

    private SqlServerSafeMigrationRuntimePlan BuildDropKey(
        string table,
        string? schema,
        string name,
        string kind
    )
    {
        var occupied = ConstraintNameExists(table, schema, name);
        var matchesKind = KeyExists(table, schema, name, kind);
        var referenced = KeyIsReferenced(table, schema, name);

        return Plan(
            $"CASE WHEN NOT {occupied} THEN N'missing' WHEN {matchesKind} AND NOT {referenced} "
            + "THEN N'matching' ELSE N'different' END",
            Bit($"NOT {occupied}")) with
        {
            // WHY: Only an exact key-kind match can lend its incoming-FK
            // blocker to ordered projection. Occupancy and wrong-kind
            // conflicts must never become approved drops after another FK drops.
            ClassificationCodeExpression = $"CASE WHEN {matchesKind} AND {referenced} "
                + "THEN N'incoming_foreign_key_key_dependency' ELSE NULL END",
        };
    }

    private string KeyMatches(
        string table,
        string? schema,
        string name,
        IReadOnlyList<string> columns,
        string kind
    )
    {
        var id = TableId(table, schema);
        var values = string.Join(", ", columns.Select((column, index) =>
            $"({index + 1}, {Literal(column)})"));

        return "EXISTS (SELECT 1 FROM sys.key_constraints kc "
            + "JOIN sys.indexes i ON i.object_id = kc.parent_object_id AND i.index_id = kc.unique_index_id "
            + $"WHERE kc.parent_object_id = {id} AND kc.name = {Literal(name)} "
            + $"AND kc.type = {Literal(kind)} AND i.is_disabled = 0 AND i.ignore_dup_key = 0 "
            + $"AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = kc.parent_object_id "
            + $"AND ic.index_id = kc.unique_index_id AND ic.key_ordinal > 0) = {columns.Count} "
            + $"AND NOT EXISTS (SELECT 1 FROM (VALUES {values}) expected(ordinal, name) "
            + "WHERE NOT EXISTS (SELECT 1 FROM sys.index_columns ic "
            + "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
            + "WHERE ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id "
            + "AND ic.key_ordinal = expected.ordinal AND c.name = expected.name)))";
    }

    private string KeyIsReferenced(
        string table,
        string? schema,
        string name
    )
        => "EXISTS (SELECT 1 FROM sys.key_constraints kc JOIN sys.foreign_keys fk "
            + "ON fk.referenced_object_id = kc.parent_object_id AND fk.key_index_id = kc.unique_index_id "
            + $"WHERE kc.parent_object_id = {TableId(table, schema)} AND kc.name = {Literal(name)})";

    private string KeyExists(
        string table,
        string? schema,
        string name,
        string kind
    )
        => $"EXISTS (SELECT 1 FROM sys.key_constraints kc WHERE kc.parent_object_id = {TableId(table, schema)} "
            + $"AND kc.name = {Literal(name)} AND kc.type = {Literal(kind)})";
}
