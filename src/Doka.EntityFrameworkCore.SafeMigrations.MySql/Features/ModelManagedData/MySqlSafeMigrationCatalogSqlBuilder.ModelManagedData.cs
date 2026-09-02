namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private MySqlSafeMigrationRuntimePlan BuildEnsureModelManagedData(
        EnsureModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(intent, ("t", intent.Columns, intent.ColumnTypes, intent.Values));
        var table = Delimited(intent.Table);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var targetMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "t");
        var found = $"doka_actual.{Delimited(intent.KeyColumns[0])} IS NOT NULL";
        var uniqueCollision = UniqueCollision(intent, intent.UniqueKeys, relation, "t");
        var engineUnsupported = EngineUnsupported(intent.Table);
        var state = "(SELECT CASE "
            + $"WHEN {engineUnsupported} THEN 'unsupported' "
            + $"WHEN COALESCE(SUM(({found}) AND NOT ({targetMatch})), 0) > 0 THEN 'different' "
            + $"WHEN {uniqueCollision} THEN 'data_blocked' "
            + $"WHEN COALESCE(SUM(NOT ({found})), 0) > 0 THEN 'missing' "
            + "ELSE 'matching' END "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var postcondition = "NOT EXISTS (SELECT 1 "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch} "
            + $"WHERE NOT ({found}) OR NOT ({targetMatch}))";

        var insertColumns = string.Join(", ", intent.Columns.Select(Delimited));
        var insertValues = string.Join(", ", ColumnAliases(intent.Columns.Count, "doka_expected", "t"));
        var mutation = $"INSERT INTO {table} ({insertColumns}) "
            + $"SELECT {insertValues} FROM {relation} "
            + $"WHERE NOT EXISTS (SELECT 1 FROM {table} AS doka_actual WHERE {keyMatch})";

        var rowEvidence = $"(SELECT GROUP_CONCAT(CASE WHEN NOT ({found}) THEN '0' "
            + $"WHEN ({targetMatch}) THEN '2' ELSE '3' END "
            + $"ORDER BY doka_expected.{Delimited("r")} SEPARATOR '') "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        return Plan(state, postcondition) with
        {
            MutationSql = mutation,
            ModelManagedRowEvidenceExpression = rowEvidence,
            ModelManagedRowCount = intent.RowCount,
        };
    }

    private MySqlSafeMigrationRuntimePlan BuildUpdateModelManagedData(
        UpdateModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(
            intent,
            ("o", intent.Columns, intent.ColumnTypes, intent.OldValues),
            ("n", intent.Columns, intent.ColumnTypes, intent.NewValues));

        var table = Delimited(intent.Table);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var sourceMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "o");
        var targetMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "n");
        var found = $"doka_actual.{Delimited(intent.KeyColumns[0])} IS NOT NULL";
        var uniqueCollision = UniqueCollision(intent, intent.UniqueKeys, relation, "n");
        var engineUnsupported = EngineUnsupported(intent.Table);
        var state = "(SELECT CASE "
            + $"WHEN {engineUnsupported} THEN 'unsupported' "
            + $"WHEN COALESCE(SUM(NOT ({found})), 0) > 0 THEN 'prerequisite_missing' "
            + $"WHEN COALESCE(SUM(NOT ({sourceMatch}) AND NOT ({targetMatch})), 0) > 0 THEN 'different' "
            + $"WHEN {uniqueCollision} THEN 'data_blocked' "
            + $"WHEN COALESCE(SUM(NOT ({targetMatch})), 0) = 0 THEN 'matching' "
            + "ELSE 'transition_ready' END "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var postcondition = "NOT EXISTS (SELECT 1 "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch} "
            + $"WHERE NOT ({found}) OR NOT ({targetMatch}))";

        var assignments = string.Join(", ", intent.Columns.Select((column, ordinal) =>
            $"doka_actual.{Delimited(column)} = doka_expected.{Delimited($"n{ordinal}")}"));

        var mutation = $"UPDATE {table} AS doka_actual JOIN {relation} ON {keyMatch} "
            + $"SET {assignments} WHERE ({sourceMatch}) AND NOT ({targetMatch})";

        var rowEvidence = $"(SELECT GROUP_CONCAT(CASE WHEN NOT ({found}) THEN '0' "
            + $"WHEN ({targetMatch}) THEN '2' WHEN ({sourceMatch}) THEN '1' ELSE '3' END "
            + $"ORDER BY doka_expected.{Delimited("r")} SEPARATOR '') "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        return Plan(state, postcondition) with
        {
            MutationSql = mutation,
            ModelManagedRowEvidenceExpression = rowEvidence,
            ModelManagedRowCount = intent.RowCount,
        };
    }

    private MySqlSafeMigrationRuntimePlan BuildDeleteModelManagedData(
        DeleteModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(intent, ("o", intent.Columns, intent.ColumnTypes, intent.OldValues));
        var table = Delimited(intent.Table);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var sourceMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "o");
        var found = $"doka_actual.{Delimited(intent.KeyColumns[0])} IS NOT NULL";
        var dependencyExists = DependencyExists(intent, relation);
        var unmodeledDependency = UnmodeledIncomingForeignKey(intent);
        var engineUnsupported = EngineUnsupported(intent.Table);
        var state = "(SELECT CASE "
            + $"WHEN {engineUnsupported} THEN 'unsupported' "
            + $"WHEN {unmodeledDependency} THEN 'unsupported' "
            + $"WHEN COALESCE(SUM(({found}) AND NOT ({sourceMatch})), 0) > 0 THEN 'different' "
            + $"WHEN {dependencyExists} THEN 'data_blocked' "
            + $"WHEN COALESCE(SUM({found}), 0) = 0 THEN 'missing' "
            + "ELSE 'transition_ready' END "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var postcondition = "NOT EXISTS (SELECT 1 "
            + $"FROM {relation} JOIN {table} AS doka_actual ON {keyMatch})";

        var mutation = $"DELETE doka_actual FROM {table} AS doka_actual "
            + $"JOIN {relation} ON {keyMatch} WHERE ({sourceMatch}) AND NOT ({dependencyExists})";

        var rowEvidence = $"(SELECT GROUP_CONCAT(CASE WHEN NOT ({found}) THEN '0' "
            + $"WHEN ({sourceMatch}) THEN '1' ELSE '3' END "
            + $"ORDER BY doka_expected.{Delimited("r")} SEPARATOR '') "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        return Plan(state, postcondition) with
        {
            MutationSql = mutation,
            ModelManagedRowEvidenceExpression = rowEvidence,
            ModelManagedDependencyCountsExpression = DependencyCounts(intent, relation),
            ModelManagedRowCount = intent.RowCount,
            ModelManagedDependencyCount = intent.ForeignKeys.Count,
        };
    }

    private string BuildModelManagedDataPrerequisite(
        ModelManagedDataIntent intent
    )
    {
        var columns = intent.KeyColumns
            .Concat(intent.Columns)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var predicates = new List<string> { TableAndColumnsExist(intent.Table, columns) };

        if (intent is DeleteModelManagedDataIntent deletion)
        {
            predicates.AddRange(
                deletion.ForeignKeys.Select(foreignKey =>
                    TableAndColumnsExist(foreignKey.Table, foreignKey.Columns)));
        }

        return string.Join(" AND ", predicates.Select(static predicate => $"({predicate})"));
    }

    private string ExpectedRelation(
        ModelManagedDataIntent intent,
        params (string Prefix, IReadOnlyList<string> Columns, IReadOnlyList<string> Types,
            ModelManagedDataMatrix Values)[] matrices
    )
    {
        var rows = new string[intent.RowCount];
        for (var row = 0; row < intent.RowCount; row++)
        {
            var values = new List<string>(
                1 + intent.KeyColumns.Count + matrices.Sum(static matrix => matrix.Columns.Count))
            {
                $"{row.ToString(CultureInfo.InvariantCulture)} AS {Delimited("r")}",
            };

            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                values.Add(
                    $"{ValueLiteral(intent.KeyValues.GetUnsafeValue(row, column), intent.KeyColumnTypes[column])} "
                    + $"AS {Delimited($"k{column}")}");
            }

            foreach (var matrix in matrices)
            {
                for (var column = 0; column < matrix.Columns.Count; column++)
                {
                    values.Add(
                        $"{ValueLiteral(matrix.Values.GetUnsafeValue(row, column), matrix.Types[column])} "
                        + $"AS {Delimited($"{matrix.Prefix}{column}")}");
                }
            }

            rows[row] = $"SELECT {string.Join(", ", values)}";
        }

        return $"({string.Join(" UNION ALL ", rows)}) AS doka_expected";
    }

    private string KeyMatch(
        ModelManagedDataIntent intent,
        string actualAlias,
        string expectedAlias
    ) => string.Join(
        " AND ",
        intent.KeyColumns.Select((column, ordinal) =>
            $"{actualAlias}.{Delimited(column)} <=> {expectedAlias}.{Delimited($"k{ordinal}")}"));

    private string ColumnMatch(
        IReadOnlyList<string> columns,
        string actualAlias,
        string expectedAlias,
        string prefix
    ) => string.Join(
        " AND ",
        columns.Select((column, ordinal) =>
            $"{actualAlias}.{Delimited(column)} <=> {expectedAlias}.{Delimited($"{prefix}{ordinal}")}"));

    private IEnumerable<string> ColumnAliases(
        int count,
        string alias,
        string prefix
    ) => Enumerable.Range(0, count).Select(ordinal => $"{alias}.{Delimited($"{prefix}{ordinal}")}");

    private string UniqueCollision(
        ModelManagedDataIntent intent,
        IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> uniqueKeys,
        string relation,
        string targetPrefix
    )
    {
        if (uniqueKeys.Count == 0)
        {
            return "FALSE";
        }

        var collisions = uniqueKeys.Select(uniqueKey =>
        {
            var uniqueMatch = string.Join(
                " AND ",
                uniqueKey.Columns.Select(column =>
                {
                    var ordinal = ColumnOrdinal(intent.Columns, column);

                    return $"doka_conflict.{Delimited(column)} <=> "
                        + $"doka_expected.{Delimited($"{targetPrefix}{ordinal}")}";
                }));

            var nonNullTarget = string.Join(
                " AND ",
                uniqueKey.Columns.Select(column =>
                {
                    var ordinal = ColumnOrdinal(intent.Columns, column);

                    return $"doka_expected.{Delimited($"{targetPrefix}{ordinal}")} IS NOT NULL";
                }));

            var sameKey = KeyMatch(intent, "doka_conflict", "doka_expected");

            return $"EXISTS (SELECT 1 FROM {relation} JOIN {Delimited(intent.Table)} AS doka_conflict "
                + $"ON {uniqueMatch} WHERE ({nonNullTarget}) AND NOT ({sameKey}))";
        });

        return $"({string.Join(" OR ", collisions)})";
    }

    private string DependencyExists(
        DeleteModelManagedDataIntent intent,
        string relation
    )
    {
        if (intent.ForeignKeys.Count == 0)
        {
            return "FALSE";
        }

        var dependencies = intent.ForeignKeys.Select(foreignKey =>
        {
            var match = string.Join(
                " AND ",
                foreignKey.Columns.Select((column, ordinal) =>
                {
                    var principalOrdinal = ColumnOrdinal(intent.Columns, foreignKey.PrincipalColumns[ordinal]);

                    return $"doka_dependent.{Delimited(column)} <=> "
                        + $"doka_expected.{Delimited($"o{principalOrdinal}")}";
                }));

            return $"EXISTS (SELECT 1 FROM {relation} JOIN {Delimited(foreignKey.Table)} AS doka_dependent "
                + $"ON {match})";
        });

        return $"({string.Join(" OR ", dependencies)})";
    }

    private string DependencyCounts(
        DeleteModelManagedDataIntent intent,
        string relation
    )
    {
        if (intent.ForeignKeys.Count == 0)
        {
            return "''";
        }

        var counts = intent.ForeignKeys.Select(foreignKey =>
        {
            var match = string.Join(
                " AND ",
                foreignKey.Columns.Select((column, ordinal) =>
                {
                    var principalOrdinal = ColumnOrdinal(intent.Columns, foreignKey.PrincipalColumns[ordinal]);

                    return $"doka_dependent.{Delimited(column)} <=> "
                        + $"doka_expected.{Delimited($"o{principalOrdinal}")}";
                }));

            return $"CAST((SELECT COUNT(*) FROM {relation} "
                + $"JOIN {Delimited(foreignKey.Table)} AS doka_dependent ON {match}) AS CHAR)";
        });

        return $"CONCAT_WS(',', {string.Join(", ", counts)})";
    }

    private string UnmodeledIncomingForeignKey(
        DeleteModelManagedDataIntent intent
    )
    {
        var modeledShapes = intent.ForeignKeys.Select(foreignKey =>
        {
            var dependentSchema = foreignKey.Schema is null ? "DATABASE()" : Literal(foreignKey.Schema);
            var columns = foreignKey.Columns.Select((column, ordinal) =>
                "SUM(kcu.ORDINAL_POSITION = "
                + (ordinal + 1).ToString(CultureInfo.InvariantCulture)
                + $" AND kcu.COLUMN_NAME = {Literal(column)}"
                + $" AND kcu.REFERENCED_COLUMN_NAME = {Literal(foreignKey.PrincipalColumns[ordinal])}) = 1");

            return $"(kcu.TABLE_SCHEMA = {dependentSchema} "
                + $"AND kcu.TABLE_NAME = {Literal(foreignKey.Table)} "
                + $"AND COUNT(*) = {foreignKey.Columns.Count.ToString(CultureInfo.InvariantCulture)} "
                + $"AND {string.Join(" AND ", columns)})";
        });

        var modeled = intent.ForeignKeys.Count == 0
            ? "FALSE"
            : $"({string.Join(" OR ", modeledShapes)})";

        var principalSchema = intent.Schema is null ? "DATABASE()" : Literal(intent.Schema);

        // A live incoming FK outside the source-frozen model cannot be guarded
        // by a statically rendered delete. Reject the operation before its
        // referential action can cascade, null, or default dependent rows.
        return "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu "
            + $"WHERE kcu.REFERENCED_TABLE_SCHEMA = {principalSchema} "
            + $"AND kcu.REFERENCED_TABLE_NAME = {Literal(intent.Table)} "
            + "GROUP BY kcu.CONSTRAINT_SCHEMA, kcu.TABLE_SCHEMA, "
            + "kcu.TABLE_NAME, kcu.CONSTRAINT_NAME "
            + $"HAVING NOT ({modeled}))";
    }

    private string EngineUnsupported(
        string table
    ) => "COALESCE((SELECT UPPER(t.ENGINE) FROM INFORMATION_SCHEMA.TABLES t "
        + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(table)} "
        + "AND t.TABLE_TYPE = 'BASE TABLE'), '') <> 'INNODB'";

    private static int ColumnOrdinal(
        IReadOnlyList<string> columns,
        string column
    )
    {
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (StringComparer.Ordinal.Equals(columns[ordinal], column))
            {
                return ordinal;
            }
        }

        throw new InvalidOperationException(
            $"Model-managed metadata references column '{column}' outside the captured value set.");
    }
}
