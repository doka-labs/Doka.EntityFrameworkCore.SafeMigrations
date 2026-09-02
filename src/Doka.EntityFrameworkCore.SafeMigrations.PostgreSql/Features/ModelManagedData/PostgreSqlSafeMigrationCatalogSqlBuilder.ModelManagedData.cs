namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    private PostgreSqlSafeMigrationRuntimePlan BuildEnsureModelManagedData(
        EnsureModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(intent, ("t", intent.Columns, intent.ColumnTypes, intent.Values));
        var table = Qualified(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var targetMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "t");
        var found = $"doka_actual.{Delimited(intent.KeyColumns[0])} IS NOT NULL";
        var uniqueCollision = UniqueCollision(intent, intent.UniqueKeys, relation, "t");
        var state = "(SELECT CASE "
            + $"WHEN COALESCE(BOOL_OR(({found}) AND NOT ({targetMatch})), FALSE) THEN 'different' "
            + $"WHEN {uniqueCollision} THEN 'data_blocked' "
            + $"WHEN COALESCE(BOOL_OR(NOT ({found})), FALSE) THEN 'missing' "
            + "ELSE 'matching' END "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var postcondition = "NOT EXISTS (SELECT 1 "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch} "
            + $"WHERE NOT ({found}) OR NOT ({targetMatch}))";

        var rowEvidence = $"(SELECT STRING_AGG(CASE WHEN NOT ({found}) THEN '0' "
            + $"WHEN ({targetMatch}) THEN '2' ELSE '3' END, '' "
            + $"ORDER BY doka_expected.{Delimited("r")}) "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        return Plan(state, postcondition) with
        {
            ModelManagedRowEvidenceExpression = rowEvidence,
            ModelManagedRowCount = intent.RowCount,
        };
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildUpdateModelManagedData(
        UpdateModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(
            intent,
            ("o", intent.Columns, intent.ColumnTypes, intent.OldValues),
            ("n", intent.Columns, intent.ColumnTypes, intent.NewValues));

        var table = Qualified(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var sourceMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "o");
        var targetMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "n");
        var found = $"doka_actual.{Delimited(intent.KeyColumns[0])} IS NOT NULL";
        var uniqueCollision = UniqueCollision(intent, intent.UniqueKeys, relation, "n");
        var state = "(SELECT CASE "
            + $"WHEN COALESCE(BOOL_OR(NOT ({found})), FALSE) THEN 'prerequisite_missing' "
            + $"WHEN COALESCE(BOOL_OR(NOT ({sourceMatch}) AND NOT ({targetMatch})), FALSE) THEN 'different' "
            + $"WHEN {uniqueCollision} THEN 'data_blocked' "
            + $"WHEN NOT COALESCE(BOOL_OR(NOT ({targetMatch})), FALSE) THEN 'matching' "
            + "ELSE 'transition_ready' END "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var postcondition = "NOT EXISTS (SELECT 1 "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch} "
            + $"WHERE NOT ({found}) OR NOT ({targetMatch}))";

        var rowEvidence = $"(SELECT STRING_AGG(CASE WHEN NOT ({found}) THEN '0' "
            + $"WHEN ({targetMatch}) THEN '2' WHEN ({sourceMatch}) THEN '1' ELSE '3' END, '' "
            + $"ORDER BY doka_expected.{Delimited("r")}) "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        return Plan(state, postcondition) with
        {
            ModelManagedRowEvidenceExpression = rowEvidence,
            ModelManagedRowCount = intent.RowCount,
        };
    }

    private PostgreSqlSafeMigrationRuntimePlan BuildDeleteModelManagedData(
        DeleteModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(intent, ("o", intent.Columns, intent.ColumnTypes, intent.OldValues));
        var table = Qualified(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var sourceMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "o");
        var found = $"doka_actual.{Delimited(intent.KeyColumns[0])} IS NOT NULL";
        var dependencyExists = DependencyExists(intent, relation);
        var unmodeledDependency = UnmodeledIncomingForeignKey(intent);
        var state = "(SELECT CASE "
            + $"WHEN {unmodeledDependency} THEN 'unsupported' "
            + $"WHEN COALESCE(BOOL_OR(({found}) AND NOT ({sourceMatch})), FALSE) THEN 'different' "
            + $"WHEN {dependencyExists} THEN 'data_blocked' "
            + $"WHEN NOT COALESCE(BOOL_OR({found}), FALSE) THEN 'missing' "
            + "ELSE 'transition_ready' END "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var postcondition = "NOT EXISTS (SELECT 1 "
            + $"FROM {relation} JOIN {table} AS doka_actual ON {keyMatch})";

        var rowEvidence = $"(SELECT STRING_AGG(CASE WHEN NOT ({found}) THEN '0' "
            + $"WHEN ({sourceMatch}) THEN '1' ELSE '3' END, '' "
            + $"ORDER BY doka_expected.{Delimited("r")}) "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        return Plan(state, postcondition) with
        {
            ModelManagedRowEvidenceExpression = rowEvidence,
            ModelManagedDependencyCountsExpression = DependencyCounts(intent, relation),
            ModelManagedRowCount = intent.RowCount,
            ModelManagedDependencyCount = intent.ForeignKeys.Count,
        };
    }

    internal string BuildModelManagedDataMutationSql(
        ModelManagedDataIntent intent
    ) => intent switch
    {
        EnsureModelManagedDataIntent value => BuildEnsureMutation(value),
        UpdateModelManagedDataIntent value => BuildUpdateMutation(value),
        DeleteModelManagedDataIntent value => BuildDeleteMutation(value),
        _ => throw new ArgumentOutOfRangeException(nameof(intent)),
    };

    private string BuildEnsureMutation(
        EnsureModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(intent, ("t", intent.Columns, intent.ColumnTypes, intent.Values));
        var table = Qualified(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var insertColumns = string.Join(", ", intent.Columns.Select(Delimited));
        var insertValues = string.Join(", ", ColumnAliases(intent.Columns.Count, "doka_expected", "t"));

        return $"INSERT INTO {table} ({insertColumns}) "
            + $"SELECT {insertValues} FROM {relation} "
            + $"WHERE NOT EXISTS (SELECT 1 FROM {table} AS doka_actual WHERE {keyMatch})";
    }

    private string BuildUpdateMutation(
        UpdateModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(
            intent,
            ("o", intent.Columns, intent.ColumnTypes, intent.OldValues),
            ("n", intent.Columns, intent.ColumnTypes, intent.NewValues));

        var table = Qualified(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var sourceMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "o");
        var targetMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "n");
        var assignments = string.Join(", ", intent.Columns.Select((column, ordinal) =>
            $"{Delimited(column)} = doka_expected.{Delimited($"n{ordinal}")}"));

        return $"UPDATE {table} AS doka_actual SET {assignments} FROM {relation} "
            + $"WHERE {keyMatch} AND ({sourceMatch}) AND NOT ({targetMatch})";
    }

    private string BuildDeleteMutation(
        DeleteModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(intent, ("o", intent.Columns, intent.ColumnTypes, intent.OldValues));
        var table = Qualified(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var sourceMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "o");
        var dependencyExists = DependencyExists(intent, relation);

        return $"DELETE FROM {table} AS doka_actual USING {relation} "
            + $"WHERE {keyMatch} AND ({sourceMatch}) AND NOT ({dependencyExists})";
    }

    private string BuildModelManagedDataPrerequisite(
        ModelManagedDataIntent intent
    )
    {
        var columns = intent.KeyColumns
            .Concat(intent.Columns)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var predicates = new List<string>
        {
            TableAndColumnsExist(intent.Table, intent.Schema, columns),
        };

        if (intent is DeleteModelManagedDataIntent deletion)
        {
            predicates.AddRange(
                deletion.ForeignKeys.Select(foreignKey =>
                    TableAndColumnsExist(foreignKey.Table, foreignKey.Schema, foreignKey.Columns)));
        }

        return string.Join(" AND ", predicates.Select(static predicate => $"({predicate})"));
    }

    private string ExpectedRelation(
        ModelManagedDataIntent intent,
        params (string Prefix, IReadOnlyList<string> Columns, IReadOnlyList<string> Types,
            ModelManagedDataMatrix Values)[] matrices
    )
    {
        var aliases = new List<string>(
            1 + intent.KeyColumns.Count + matrices.Sum(static matrix => matrix.Columns.Count))
        {
            "r",
        };

        aliases.AddRange(Enumerable.Range(0, intent.KeyColumns.Count).Select(static ordinal => $"k{ordinal}"));
        foreach (var matrix in matrices)
        {
            aliases.AddRange(Enumerable.Range(0, matrix.Columns.Count).Select(ordinal => $"{matrix.Prefix}{ordinal}"));
        }

        var rows = new string[intent.RowCount];
        for (var row = 0; row < intent.RowCount; row++)
        {
            var values = new List<string>(aliases.Count)
            {
                row.ToString(CultureInfo.InvariantCulture),
            };

            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                values.Add(TypedValue(intent.KeyValues.GetUnsafeValue(row, column), intent.KeyColumnTypes[column]));
            }

            foreach (var matrix in matrices)
            {
                for (var column = 0; column < matrix.Columns.Count; column++)
                {
                    values.Add(TypedValue(matrix.Values.GetUnsafeValue(row, column), matrix.Types[column]));
                }
            }

            rows[row] = $"({string.Join(", ", values)})";
        }

        return $"(VALUES {string.Join(", ", rows)}) AS doka_expected("
            + $"{string.Join(", ", aliases.Select(Delimited))})";
    }

    private string TypedValue(
        object? value,
        string storeType
    )
    {
        var mapping = value is null
            ? _typeMappingSource.FindMapping(storeType)
            : _typeMappingSource.FindMapping(value.GetType(), storeType);

        var mappedStoreType = (mapping
                ?? throw new NotSupportedException(
                    $"PostgreSQL has no type mapping for store type '{storeType}'."))
            .StoreType;

        return $"{ValueLiteral(value, storeType)}::{mappedStoreType}";
    }

    private string KeyMatch(
        ModelManagedDataIntent intent,
        string actualAlias,
        string expectedAlias
    ) => string.Join(
        " AND ",
        intent.KeyColumns.Select((column, ordinal) =>
            $"{actualAlias}.{Delimited(column)} IS NOT DISTINCT FROM "
            + $"{expectedAlias}.{Delimited($"k{ordinal}")}"));

    private string ColumnMatch(
        IReadOnlyList<string> columns,
        string actualAlias,
        string expectedAlias,
        string prefix
    ) => string.Join(
        " AND ",
        columns.Select((column, ordinal) =>
            $"{actualAlias}.{Delimited(column)} IS NOT DISTINCT FROM "
            + $"{expectedAlias}.{Delimited($"{prefix}{ordinal}")}"));

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

                    return $"doka_conflict.{Delimited(column)} IS NOT DISTINCT FROM "
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

            return $"EXISTS (SELECT 1 FROM {relation} JOIN {Qualified(intent.Table, intent.Schema)} "
                + $"AS doka_conflict ON {uniqueMatch} WHERE ({nonNullTarget}) AND NOT ({sameKey}))";
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

                    return $"doka_dependent.{Delimited(column)} IS NOT DISTINCT FROM "
                        + $"doka_expected.{Delimited($"o{principalOrdinal}")}";
                }));

            return $"EXISTS (SELECT 1 FROM {relation} JOIN {Qualified(foreignKey.Table, foreignKey.Schema)} "
                + $"AS doka_dependent ON {match})";
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

                    return $"doka_dependent.{Delimited(column)} IS NOT DISTINCT FROM "
                        + $"doka_expected.{Delimited($"o{principalOrdinal}")}";
                }));

            return $"(SELECT COUNT(*) FROM {relation} "
                + $"JOIN {Qualified(foreignKey.Table, foreignKey.Schema)} AS doka_dependent ON {match})::text";
        });

        return $"CONCAT_WS(',', {string.Join(", ", counts)})";
    }

    private string UnmodeledIncomingForeignKey(
        DeleteModelManagedDataIntent intent
    )
    {
        var modeledShapes = intent.ForeignKeys.Select(foreignKey =>
            $"(dependent_namespace.nspname = {SchemaExpression(foreignKey.Schema)} "
            + $"AND dependent_table.relname = {Literal(foreignKey.Table)} "
            + "AND ARRAY(SELECT attribute.attname "
            + "FROM unnest(constraint_row.conkey) WITH ORDINALITY AS key(attnum, ord) "
            + "JOIN pg_catalog.pg_attribute attribute "
            + "ON attribute.attrelid = constraint_row.conrelid AND attribute.attnum = key.attnum "
            + $"ORDER BY key.ord) = {NameArray(foreignKey.Columns)} "
            + "AND ARRAY(SELECT attribute.attname "
            + "FROM unnest(constraint_row.confkey) WITH ORDINALITY AS key(attnum, ord) "
            + "JOIN pg_catalog.pg_attribute attribute "
            + "ON attribute.attrelid = constraint_row.confrelid AND attribute.attnum = key.attnum "
            + $"ORDER BY key.ord) = {NameArray(foreignKey.PrincipalColumns)})");

        var modeled = intent.ForeignKeys.Count == 0
            ? "FALSE"
            : $"({string.Join(" OR ", modeledShapes)})";

        // PostgreSQL can expose incoming constraints that are absent from the
        // source model. Without frozen dependent columns no static CAS delete
        // can exclude their side effects, so that catalog shape is unsupported.
        return "EXISTS (SELECT 1 FROM pg_catalog.pg_constraint constraint_row "
            + "JOIN pg_catalog.pg_class dependent_table "
            + "ON dependent_table.oid = constraint_row.conrelid "
            + "JOIN pg_catalog.pg_namespace dependent_namespace "
            + "ON dependent_namespace.oid = dependent_table.relnamespace "
            + "WHERE constraint_row.contype = 'f'::\"char\" "
            + $"AND constraint_row.confrelid = {QualifiedRegclass(intent.Table, intent.Schema)} "
            + $"AND NOT ({modeled}))";
    }

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
