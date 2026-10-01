namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private SqlServerSafeMigrationRuntimePlan BuildEnsureModelManagedData(
        EnsureModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(intent, ("t", intent.Columns, intent.ColumnTypes, intent.Values));
        var table = QualifiedTable(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var targetMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "t");
        var found = $"doka_actual.{Delimited(intent.KeyColumns[0])} IS NOT NULL";
        var collision = UniqueCollision(intent, intent.UniqueKeys, relation, "t")
            + " OR " + ExpectedSelfCollision(intent, intent.UniqueKeys, relation, "t", intent.Values);

        var state = "(SELECT CASE "
            + $"WHEN MAX(CASE WHEN {found} AND NOT ({targetMatch}) THEN 1 ELSE 0 END) = 1 THEN N'different' "
            + $"WHEN {collision} THEN N'data_blocked' "
            + $"WHEN MAX(CASE WHEN NOT ({found}) THEN 1 ELSE 0 END) = 1 THEN N'missing' "
            + "ELSE N'matching' END "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var postcondition = $"NOT EXISTS (SELECT 1 FROM {relation} "
            + $"LEFT JOIN {table} AS doka_actual ON {keyMatch} "
            + $"WHERE NOT ({found}) OR NOT ({targetMatch}))";

        var evidence = $"(SELECT STRING_AGG(CONVERT(varchar(max), CASE WHEN NOT ({found}) THEN '0' "
            + $"WHEN ({targetMatch}) THEN '2' ELSE '3' END), '') "
            + $"WITHIN GROUP (ORDER BY doka_expected.{Delimited("r")}) "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var commonGuard = BuildModelManagedDataCommonGuard(intent);

        return Plan(state, Bit(postcondition)) with
        {
            DifferentDifference = ModelManagedRowContentDifference(),
            ModelManagedRowEvidenceExpression = evidence,
            ModelManagedRowCount = intent.RowCount,
            RequiresDelayedBinding = true,
            StateEvaluationGuardExpression = BuildModelManagedDataTypeGuard(intent, commonGuard),
            StateEvaluationGuardFailureExpression = ModelManagedDataGuardFailure(commonGuard),
        };
    }

    private SqlServerSafeMigrationRuntimePlan BuildUpdateModelManagedData(
        UpdateModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(
            intent,
            ("o", intent.Columns, intent.ColumnTypes, intent.OldValues),
            ("n", intent.Columns, intent.ColumnTypes, intent.NewValues));

        var table = QualifiedTable(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var sourceMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "o");
        var targetMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "n");
        var found = $"doka_actual.{Delimited(intent.KeyColumns[0])} IS NOT NULL";
        var collision = UniqueCollision(intent, intent.UniqueKeys, relation, "n")
            + " OR " + ExpectedSelfCollision(intent, intent.UniqueKeys, relation, "n", intent.NewValues);

        var state = "(SELECT CASE "
            + $"WHEN MAX(CASE WHEN NOT ({found}) THEN 1 ELSE 0 END) = 1 THEN N'prerequisite_missing' "
            + $"WHEN MAX(CASE WHEN NOT ({sourceMatch}) AND NOT ({targetMatch}) THEN 1 ELSE 0 END) = 1 "
            + "THEN N'different' "
            + $"WHEN {collision} THEN N'data_blocked' "
            + $"WHEN MAX(CASE WHEN NOT ({targetMatch}) THEN 1 ELSE 0 END) = 0 THEN N'matching' "
            + "ELSE N'transition_ready' END "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var postcondition = $"NOT EXISTS (SELECT 1 FROM {relation} "
            + $"LEFT JOIN {table} AS doka_actual ON {keyMatch} "
            + $"WHERE NOT ({found}) OR NOT ({targetMatch}))";

        var evidence = $"(SELECT STRING_AGG(CONVERT(varchar(max), CASE WHEN NOT ({found}) THEN '0' "
            + $"WHEN ({targetMatch}) THEN '2' WHEN ({sourceMatch}) THEN '1' ELSE '3' END), '') "
            + $"WITHIN GROUP (ORDER BY doka_expected.{Delimited("r")}) "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var commonGuard = BuildModelManagedDataCommonGuard(intent);

        return Plan(state, Bit(postcondition)) with
        {
            DifferentDifference = ModelManagedRowContentDifference(),
            ModelManagedRowEvidenceExpression = evidence,
            ModelManagedRowCount = intent.RowCount,
            RequiresDelayedBinding = true,
            StateEvaluationGuardExpression = BuildModelManagedDataTypeGuard(intent, commonGuard),
            StateEvaluationGuardFailureExpression = ModelManagedDataGuardFailure(commonGuard),
        };
    }

    private SqlServerSafeMigrationRuntimePlan BuildDeleteModelManagedData(
        DeleteModelManagedDataIntent intent
    )
    {
        var relation = ExpectedRelation(intent, ("o", intent.Columns, intent.ColumnTypes, intent.OldValues));
        var table = QualifiedTable(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var sourceMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "o");
        var found = $"doka_actual.{Delimited(intent.KeyColumns[0])} IS NOT NULL";
        var dependent = DependencyExists(intent, relation);
        var unmodeled = UnmodeledIncomingForeignKey(intent);
        var state = "(SELECT CASE "
            + $"WHEN {unmodeled} THEN N'unsupported' "
            + $"WHEN MAX(CASE WHEN {found} AND NOT ({sourceMatch}) THEN 1 ELSE 0 END) = 1 "
            + "THEN N'different' "
            + $"WHEN {dependent} THEN N'data_blocked' "
            + $"WHEN MAX(CASE WHEN {found} THEN 1 ELSE 0 END) = 0 THEN N'missing' "
            + "ELSE N'transition_ready' END "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var postcondition = $"NOT EXISTS (SELECT 1 FROM {relation} "
            + $"JOIN {table} AS doka_actual ON {keyMatch})";

        var evidence = $"(SELECT STRING_AGG(CONVERT(varchar(max), CASE WHEN NOT ({found}) THEN '0' "
            + $"WHEN ({sourceMatch}) THEN '1' ELSE '3' END), '') "
            + $"WITHIN GROUP (ORDER BY doka_expected.{Delimited("r")}) "
            + $"FROM {relation} LEFT JOIN {table} AS doka_actual ON {keyMatch})";

        var commonGuard = BuildModelManagedDataCommonGuard(intent);

        return Plan(state, Bit(postcondition)) with
        {
            DifferentDifference = ModelManagedRowContentDifference(),
            ModelManagedRowEvidenceExpression = evidence,
            ModelManagedDependencyCountsExpression = DependencyCounts(intent, relation),
            ModelManagedRowCount = intent.RowCount,
            ModelManagedDependencyCount = intent.ForeignKeys.Count,
            RequiresDelayedBinding = true,
            StateEvaluationGuardExpression = BuildModelManagedDataTypeGuard(intent, commonGuard),
            StateEvaluationGuardFailureExpression = ModelManagedDataGuardFailure(commonGuard),
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

    private string BuildEnsureMutation(EnsureModelManagedDataIntent intent)
    {
        var relation = ExpectedRelation(intent, ("t", intent.Columns, intent.ColumnTypes, intent.Values));
        var table = QualifiedTable(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var columns = string.Join(", ", intent.Columns.Select(Delimited));
        var values = string.Join(", ", Enumerable.Range(0, intent.Columns.Count)
            .Select(index => $"doka_expected.{Delimited($"t{index}")}"));

        var mutation = $"INSERT INTO {table} ({columns}) SELECT {values} FROM {relation} "
            + $"WHERE NOT EXISTS (SELECT 1 FROM {table} AS doka_actual WHERE {keyMatch})";

        var includedColumns = string.Join(", ", intent.Columns.Select(Literal));
        var hasExplicitIdentity = "EXISTS (SELECT 1 FROM sys.identity_columns ic "
            + $"WHERE ic.object_id = {TableId(intent.Table, intent.Schema)} "
            + $"AND ic.name IN ({includedColumns}))";

        // WHY: EF HasData includes explicit identity keys. IDENTITY_INSERT is
        // session-scoped. TRY/CATCH restores ordinary SQL errors; client
        // attentions require MigrationCommand cleanup or script-client recovery.

        return $"DECLARE @doka_identity_insert bit = CASE WHEN {hasExplicitIdentity} THEN 1 ELSE 0 END; "
            + $"IF @doka_identity_insert = 1 SET IDENTITY_INSERT {table} ON; "
            + $"BEGIN TRY {mutation}; END TRY "
            + $"BEGIN CATCH IF @doka_identity_insert = 1 SET IDENTITY_INSERT {table} OFF; THROW; END CATCH; "
            + $"IF @doka_identity_insert = 1 SET IDENTITY_INSERT {table} OFF";
    }

    private string BuildUpdateMutation(UpdateModelManagedDataIntent intent)
    {
        var relation = ExpectedRelation(
            intent,
            ("o", intent.Columns, intent.ColumnTypes, intent.OldValues),
            ("n", intent.Columns, intent.ColumnTypes, intent.NewValues));

        var table = QualifiedTable(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var sourceMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "o");
        var targetMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "n");
        var assignments = string.Join(", ", intent.Columns.Select((column, index) =>
            $"{Delimited(column)} = doka_expected.{Delimited($"n{index}")}"));

        return $"UPDATE doka_actual SET {assignments} FROM {table} AS doka_actual "
            + $"JOIN {relation} ON {keyMatch} WHERE ({sourceMatch}) AND NOT ({targetMatch})";
    }

    private string BuildDeleteMutation(DeleteModelManagedDataIntent intent)
    {
        var relation = ExpectedRelation(intent, ("o", intent.Columns, intent.ColumnTypes, intent.OldValues));
        var table = QualifiedTable(intent.Table, intent.Schema);
        var keyMatch = KeyMatch(intent, "doka_actual", "doka_expected");
        var sourceMatch = ColumnMatch(intent.Columns, "doka_actual", "doka_expected", "o");
        var dependent = DependencyExists(intent, relation);

        return $"DELETE doka_actual FROM {table} AS doka_actual "
            + $"JOIN {relation} ON {keyMatch} WHERE ({sourceMatch}) AND NOT ({dependent})";
    }

    private string BuildModelManagedDataPrerequisite(ModelManagedDataIntent intent)
    {
        var columns = intent.KeyColumns.Concat(intent.Columns).Distinct(StringComparer.Ordinal).ToArray();
        var predicates = new List<string> { TableAndColumnsExist(intent.Table, intent.Schema, columns) };

        if (intent is DeleteModelManagedDataIntent deletion)
        {
            predicates.AddRange(deletion.ForeignKeys.Select(foreignKey =>
                TableAndColumnsExist(foreignKey.Table, foreignKey.Schema, foreignKey.Columns)));
        }

        return string.Join(" AND ", predicates.Select(static predicate => $"({predicate})"));
    }

    private string BuildModelManagedDataTypeGuard(
        ModelManagedDataIntent intent,
        string commonGuard
    )
    {
        var predicates = new List<string>(intent.KeyColumns.Count + intent.Columns.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < intent.KeyColumns.Count; index++)
        {
            if (seen.Add(intent.KeyColumns[index]))
            {
                predicates.Add(ColumnStoreTypeMatches(intent.Table, intent.Schema,
                    intent.KeyColumns[index], intent.KeyColumnTypes[index]));
            }
        }

        for (var index = 0; index < intent.Columns.Count; index++)
        {
            if (seen.Add(intent.Columns[index]))
            {
                predicates.Add(ColumnStoreTypeMatches(intent.Table, intent.Schema,
                    intent.Columns[index], intent.ColumnTypes[index]));
            }
        }

        if (intent is DeleteModelManagedDataIntent deletion)
        {
            foreach (var foreignKey in deletion.ForeignKeys)
            {
                for (var index = 0; index < foreignKey.Columns.Count; index++)
                {
                    var principalOrdinal = ColumnOrdinal(intent.Columns, foreignKey.PrincipalColumns[index]);

                    predicates.Add(ColumnStoreTypeMatches(foreignKey.Table, foreignKey.Schema,
                        foreignKey.Columns[index], intent.ColumnTypes[principalOrdinal]));
                }
            }
        }

        // WHY: Physical type mismatches remain Different, while unsupported
        // values, triggers, and server semantics share the same rejection proof.

        return Bit($"({commonGuard}) AND "
            + string.Join(" AND ", predicates.Select(static predicate => $"({predicate})")));
    }

    /// <summary>Builds shared semantic proofs once for both classification branches.</summary>
    private string BuildModelManagedDataCommonGuard(
        ModelManagedDataIntent intent
    )
    {
        // WHY: Rendering all row values twice doubles transient allocation.
        // Reuse the exact proof for both acceptance and failure classification,
        // without caching row data beyond this immutable operation's plan.
        return $"COALESCE({SqlServerCompatibilityLevel()}, 0) >= 110 "
            + $"AND ({BuildSourceCollisionCollationGuard(intent)}) "
            + $"AND ({BuildModelManagedValueGuard(intent)}) "
            + $"AND ({BuildModelManagedDataTriggerGuard(intent)}) "
            + $"AND ({BuildModelManagedDataWritableColumnGuard(intent)})";
    }

    private static string SqlServerCompatibilityLevel()
        => "(SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME())";

    private static string ModelManagedDataGuardFailure(
        string commonGuard
    )
        => $"CASE WHEN NOT ({commonGuard}) "
            + "THEN N'unsupported' ELSE N'different' END";

    private string BuildModelManagedDataTriggerGuard(ModelManagedDataIntent intent)
    {
        // WHY: DML triggers can mutate rows outside the captured model-managed
        // contract, including INSTEAD OF triggers that suppress the write.
        return "NOT EXISTS (SELECT 1 FROM sys.triggers tr "
            + $"WHERE tr.parent_id = {TableId(intent.Table, intent.Schema)} "
            + "AND tr.is_disabled = 0 AND tr.type = 'TR')";
    }

    private string BuildModelManagedDataWritableColumnGuard(ModelManagedDataIntent intent)
    {
        if (intent is DeleteModelManagedDataIntent)
        {
            return "1 = 1";
        }

        var columns = string.Join(", ", intent.Columns.Select(Literal));
        var generated = "NOT EXISTS (SELECT 1 FROM sys.columns c "
            + $"WHERE c.object_id = {TableId(intent.Table, intent.Schema)} "
            + $"AND c.name IN ({columns}) "
            + "AND (c.is_computed = 1 OR c.generated_always_type <> 0))";

        if (intent is not UpdateModelManagedDataIntent)
        {
            return generated;
        }

        // WHY: Explicit identity values are permitted for INSERT with
        // IDENTITY_INSERT, but SQL Server never permits updating that column.

        return generated + " AND NOT EXISTS (SELECT 1 FROM sys.identity_columns ic "
            + $"WHERE ic.object_id = {TableId(intent.Table, intent.Schema)} "
            + $"AND ic.name IN ({columns}))";
    }

    private string BuildSourceCollisionCollationGuard(ModelManagedDataIntent intent)
    {
        if (intent.RowCount == 1)
        {
            return "1 = 1";
        }

        var columns = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < intent.KeyColumns.Count; index++)
        {
            if (IsCharacterManagedStoreType(intent.KeyColumnTypes[index]))
            {
                columns.Add(intent.KeyColumns[index]);
            }
        }

        var uniqueKeys = intent switch
        {
            EnsureModelManagedDataIntent ensure => ensure.UniqueKeys,
            UpdateModelManagedDataIntent update => update.UniqueKeys,
            DeleteModelManagedDataIntent => Array.Empty<ExpectedModelManagedDataUniqueKeyDefinition>(),
            _ => throw new UnreachableException(),
        };

        foreach (var uniqueKey in uniqueKeys)
        {
            foreach (var column in uniqueKey.Columns)
            {
                var ordinal = ColumnOrdinal(intent.Columns, column);

                if (IsCharacterManagedStoreType(intent.ColumnTypes[ordinal]))
                {
                    columns.Add(column);
                }
            }
        }

        if (columns.Count == 0)
        {
            return "1 = 1";
        }

        var expected = string.Join(", ", columns.Select(Literal));

        // WHY: An unqualified expected-vs-expected comparison uses database
        // collation. A different physical key collation could hide a collision.

        return "NOT EXISTS (SELECT 1 FROM sys.columns c "
            + $"WHERE c.object_id = {TableId(intent.Table, intent.Schema)} "
            + $"AND c.name IN ({expected}) "
            + "AND (c.collation_name IS NULL OR c.collation_name <> "
            + "CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation'))))";
    }

    private static bool IsCharacterManagedStoreType(string storeType)
    {
        var value = storeType.Trim();
        var opening = value.IndexOf('(');
        var type = opening < 0 ? value : value[..opening].TrimEnd();

        return type.Equals("char", StringComparison.OrdinalIgnoreCase)
            || type.Equals("varchar", StringComparison.OrdinalIgnoreCase)
            || type.Equals("nchar", StringComparison.OrdinalIgnoreCase)
            || type.Equals("nvarchar", StringComparison.OrdinalIgnoreCase);
    }

    private string? GetUnsupportedModelManagedDataFeature(ModelManagedDataIntent intent)
    {
        // WHY: Captured store-type text becomes a T-SQL CAST target. Only known
        // built-in mappings may be embedded; opaque provider types fail closed.
        foreach (var storeType in intent.KeyColumnTypes.Concat(intent.ColumnTypes))
        {
            if (!IsSupportedManagedStoreType(storeType) || _typeMappingSource.FindMapping(storeType) is null)
            {
                return "model_managed_store_type";
            }
        }

        var matrices = intent switch
        {
            EnsureModelManagedDataIntent ensure => new[] { ensure.KeyValues, ensure.Values },
            UpdateModelManagedDataIntent update => new[] { update.KeyValues, update.OldValues, update.NewValues },
            DeleteModelManagedDataIntent delete => new[] { delete.KeyValues, delete.OldValues },
            _ => throw new UnreachableException(),
        };

        foreach (var matrix in matrices)
        {
            for (var row = 0; row < matrix.RowCount; row++)
            {
                for (var column = 0; column < matrix.ColumnCount; column++)
                {
                    var value = matrix.GetUnsafeValue(row, column);
                    var types = ReferenceEquals(matrix, intent.KeyValues)
                        ? intent.KeyColumnTypes
                        : intent.ColumnTypes;

                    if (!FitsManagedValueSize(value, types[column]))
                    {
                        return "model_managed_value_size";
                    }

                    if (!IsManagedValueRepresentable(value, types[column]))
                    {
                        return "model_managed_value_range";
                    }

                    if (value is not null
                        && _typeMappingSource.FindMapping(value.GetType(), types[column]) is null)
                    {
                        return "model_managed_value_mapping";
                    }
                }
            }
        }

        return null;
    }

    private string BuildModelManagedValueGuard(ModelManagedDataIntent intent)
    {
        var predicates = new HashSet<string>(StringComparer.Ordinal);
        var rendered = new HashSet<(object? Value, string Type, string Column)>();

        AddValues(intent.KeyColumns, intent.KeyColumnTypes, intent.KeyValues);

        switch (intent)
        {
            case EnsureModelManagedDataIntent ensure:
                AddValues(intent.Columns, intent.ColumnTypes, ensure.Values);
                break;
            case UpdateModelManagedDataIntent update:
                AddValues(intent.Columns, intent.ColumnTypes, update.OldValues);
                AddValues(intent.Columns, intent.ColumnTypes, update.NewValues);
                break;
            case DeleteModelManagedDataIntent delete:
                AddValues(intent.Columns, intent.ColumnTypes, delete.OldValues);
                break;
            default:
                throw new UnreachableException();
        }

        return predicates.Count == 0
            ? "1 = 1"
            : string.Join(" AND ", predicates.Select(static predicate => "(" + predicate + ")"));

        void AddValues(
            IReadOnlyList<string> columns,
            IReadOnlyList<string> types,
            ModelManagedDataMatrix matrix
        )
        {
            for (var column = 0; column < matrix.ColumnCount; column++)
            {
                for (var row = 0; row < matrix.RowCount; row++)
                {
                    var value = matrix.GetUnsafeValue(row, column);
                    // WHY: Repeated CLR values in the same typed column emit
                    // the identical constant proof. This only avoids rendering;
                    // SQL Server still decides row equality and uniqueness.
                    if (!rendered.Add((value, types[column], columns[column])))
                    {
                        continue;
                    }

                    if (value is not null)
                    {
                        predicates.Add(ManagedValueRepresentationGuard(value, types[column]));
                    }

                    if (!IsAnsiManagedStoreType(types[column]))
                    {
                        continue;
                    }

                    predicates.Add(ManagedAnsiValueRepresentationGuard(value, types[column]));

                    var text = value switch
                    {
                        string stringValue => stringValue,
                        char character => character.ToString(),
                        _ => null,
                    };

                    if (text is null || text.All(static character => character <= 127))
                    {
                        continue;
                    }

                    // WHY: The expected row relation uses database-default
                    // literals; a different physical code page cannot borrow it.
                    predicates.Add("EXISTS (SELECT 1 FROM sys.columns c "
                        + $"WHERE c.object_id = {TableId(intent.Table, intent.Schema)} "
                        + $"AND c.name = {Literal(columns[column])} "
                        + "AND c.collation_name = CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation')))");

                }
            }
        }
    }

    private static bool FitsManagedValueSize(
        object? value,
        string storeType
    )
    {
        var length = value switch
        {
            string text => text.Length,
            char => 1,
            byte[] bytes => bytes.Length,
            _ => 0,
        };

        return !TryManagedValueMaximum(storeType, out var maximum) || length <= maximum;
    }

    private static bool TryManagedValueMaximum(
        string storeType,
        out int maximum
    )
    {
        var value = storeType.Trim().ToLowerInvariant();
        var opening = value.IndexOf('(');
        var type = opening < 0 ? value : value[..opening].TrimEnd();
        maximum = 0;

        return (type is "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary")
            && opening >= 0 && value.EndsWith(')')
            && int.TryParse(value[(opening + 1)..^1].Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out maximum);
    }

    private static bool IsAnsiManagedStoreType(string storeType)
    {
        var value = storeType.Trim();
        var opening = value.IndexOf('(');
        var type = opening < 0 ? value : value[..opening].TrimEnd();

        return type.Equals("char", StringComparison.OrdinalIgnoreCase)
            || type.Equals("varchar", StringComparison.OrdinalIgnoreCase);
    }

    private string UnicodeManagedLiteral(string value)
    {
        var mapping = _typeMappingSource.FindMapping(typeof(string), "nvarchar(max)")
            ?? throw new InvalidOperationException("SQL Server has no Unicode literal mapping.");

        return mapping.GenerateSqlLiteral(value);
    }

    private static bool IsSupportedManagedStoreType(string storeType)
    {
        var value = storeType.Trim().ToLowerInvariant();
        var opening = value.IndexOf('(');
        var type = opening < 0 ? value : value[..opening].TrimEnd();
        var args = opening < 0 || !value.EndsWith(')')
            ? null
            : value[(opening + 1)..^1].Trim();

        if (opening >= 0 && (args is null || args.Contains('(') || args.Contains(')')))
        {
            return false;
        }

        if (type is "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary")
        {
            if (args is null)
            {
                return false;
            }

            if (args == "max")
            {
                return type is "varchar" or "nvarchar" or "varbinary";
            }

            var maximum = type is "nchar" or "nvarchar" ? 4_000 : 8_000;

            return int.TryParse(args, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                && length is > 0 && length <= maximum;
        }

        if (type is "decimal" or "numeric")
        {
            if (args is null)
            {
                return false;
            }

            var parts = args.Split(',', StringSplitOptions.TrimEntries);

            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var precision)
                && precision is >= 1 and <= 38
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var scale)
                && scale >= 0 && scale <= precision;
        }

        if (type is "datetime2" or "datetimeoffset" or "time")
        {
            return args is null
                || int.TryParse(args, NumberStyles.None, CultureInfo.InvariantCulture, out var scale)
                && scale is >= 0 and <= 7;
        }

        if (type == "float")
        {
            return args is null
                || int.TryParse(args, NumberStyles.None, CultureInfo.InvariantCulture, out var precision)
                && precision is >= 25 and <= 53;
        }

        return args is null && type is ("bigint" or "bit" or "date" or "datetime" or "int"
            or "money" or "real" or "smalldatetime" or "smallint" or "smallmoney"
            or "tinyint" or "uniqueidentifier");
    }

    private string ExpectedRelation(
        ModelManagedDataIntent intent,
        params (string Prefix, IReadOnlyList<string> Columns, IReadOnlyList<string> Types,
            ModelManagedDataMatrix Values)[] matrices
    )
        => ExpectedRelationNamed(intent, "doka_expected", matrices);

    private string ExpectedRelationNamed(
        ModelManagedDataIntent intent,
        string alias,
        params (string Prefix, IReadOnlyList<string> Columns, IReadOnlyList<string> Types,
            ModelManagedDataMatrix Values)[] matrices
    )
    {
        var aliases = new List<string>(1 + intent.KeyColumns.Count
            + matrices.Sum(static matrix => matrix.Columns.Count)) { "r" };

        aliases.AddRange(Enumerable.Range(0, intent.KeyColumns.Count).Select(static index => $"k{index}"));

        foreach (var matrix in matrices)
        {
            aliases.AddRange(Enumerable.Range(0, matrix.Columns.Count)
                .Select(index => $"{matrix.Prefix}{index}"));
        }

        var rows = new string[intent.RowCount];
        for (var row = 0; row < intent.RowCount; row++)
        {
            var values = new string[aliases.Count];
            values[0] = row.ToString(CultureInfo.InvariantCulture);
            var position = 1;

            for (var column = 0; column < intent.KeyColumns.Count; column++)
            {
                values[position++] = TypedValue(intent.KeyValues.GetUnsafeValue(row, column),
                    intent.KeyColumnTypes[column]);
            }

            foreach (var matrix in matrices)
            {
                for (var column = 0; column < matrix.Columns.Count; column++)
                {
                    values[position++] = TypedValue(matrix.Values.GetUnsafeValue(row, column), matrix.Types[column]);
                }
            }

            rows[row] = $"({string.Join(", ", values)})";
        }

        return $"(VALUES {string.Join(", ", rows)}) AS {alias}("
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

        if (mapping is null || !IsSupportedManagedStoreType(mapping.StoreType)
            || !FitsManagedValueSize(value, storeType) || !IsManagedValueRepresentable(value, storeType))
        {
            throw new NotSupportedException($"SQL Server has no safe mapping for '{storeType}'.");
        }

        var literal = ManagedValueLiteral(value, storeType);

        return $"CAST({literal} AS {mapping.StoreType})";
    }

    private string KeyMatch(
        ModelManagedDataIntent intent,
        string actual,
        string expected
    )
        => string.Join(" AND ", intent.KeyColumns.Select((column, index) =>
            NullSafeSqlEqual($"{actual}.{Delimited(column)}", $"{expected}.{Delimited($"k{index}")}")));

    private string ColumnMatch(
        IReadOnlyList<string> columns,
        string actual,
        string expected,
        string prefix
    )
        => string.Join(" AND ", columns.Select((column, index) =>
            ExactValueMatch($"{actual}.{Delimited(column)}", $"{expected}.{Delimited($"{prefix}{index}")}")));

    private static string NullSafeSqlEqual(
        string left,
        string right
    )
        => $"({left} = {right} OR ({left} IS NULL AND {right} IS NULL))";

    private static string ExactValueMatch(
        string actual,
        string expected
    )
    {
        // WHY: Database collations may ignore case, accents, or trailing spaces.
        // Comparing canonical typed bytes preserves model-managed row evidence.
        return $"({actual} IS NULL AND {expected} IS NULL OR "
            + $"{actual} IS NOT NULL AND {expected} IS NOT NULL "
            + $"AND DATALENGTH({actual}) = DATALENGTH({expected}) "
            + $"AND CONVERT(varbinary(max), {actual}) = CONVERT(varbinary(max), {expected}))";
    }

    private string UniqueCollision(
        ModelManagedDataIntent intent,
        IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> uniqueKeys,
        string relation,
        string targetPrefix
    )
    {
        if (uniqueKeys.Count == 0)
        {
            return "1 = 0";
        }

        var checks = uniqueKeys.Select(uniqueKey =>
        {
            var columns = string.Join(" AND ", uniqueKey.Columns.Select(column =>
            {
                var ordinal = ColumnOrdinal(intent.Columns, column);

                // WHY: Unlike PostgreSQL, SQL Server's ordinary unique keys
                // consider duplicate NULL combinations conflicting.

                return NullSafeSqlEqual($"doka_conflict.{Delimited(column)}",
                    $"doka_expected.{Delimited($"{targetPrefix}{ordinal}")}");
            }));

            var key = KeyMatch(intent, "doka_conflict", "doka_expected");

            return $"EXISTS (SELECT 1 FROM {relation} "
                + $"JOIN {QualifiedTable(intent.Table, intent.Schema)} AS doka_conflict "
                + $"ON {columns} WHERE NOT ({key}))";
        });

        return $"({string.Join(" OR ", checks)})";
    }

    private string ExpectedSelfCollision(
        ModelManagedDataIntent intent,
        IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> uniqueKeys,
        string relation,
        string targetPrefix,
        ModelManagedDataMatrix targetValues
    )
    {
        if (intent.RowCount == 1)
        {
            return "1 = 0";
        }

        var other = ExpectedRelationNamed(intent, "doka_other",
            (targetPrefix, intent.Columns, intent.ColumnTypes, targetValues));

        var collisionKeys = new List<string>(uniqueKeys.Count + 1)
        {
            string.Join(" AND ", intent.KeyColumns.Select((_, index) =>
                NullSafeSqlEqual($"doka_expected.{Delimited($"k{index}")}",
                    $"doka_other.{Delimited($"k{index}")}"))),
        };

        foreach (var uniqueKey in uniqueKeys)
        {
            collisionKeys.Add(string.Join(" AND ", uniqueKey.Columns.Select(column =>
            {
                var ordinal = ColumnOrdinal(intent.Columns, column);

                return NullSafeSqlEqual($"doka_expected.{Delimited($"{targetPrefix}{ordinal}")}",
                    $"doka_other.{Delimited($"{targetPrefix}{ordinal}")}");
            })));
        }

        // WHY: CLR equality cannot model the live database collation. Two
        // distinct expected strings may collide under a case-insensitive key.

        return $"EXISTS (SELECT 1 FROM {relation} CROSS JOIN {other} "
            + $"WHERE doka_expected.{Delimited("r")} < doka_other.{Delimited("r")} "
            + $"AND ({string.Join(" OR ", collisionKeys.Select(static key => $"({key})"))}))";
    }

    private string DependencyExists(
        DeleteModelManagedDataIntent intent,
        string relation
    )
    {
        if (intent.ForeignKeys.Count == 0)
        {
            return "1 = 0";
        }

        var checks = intent.ForeignKeys.Select(foreignKey =>
        {
            var match = string.Join(" AND ", foreignKey.Columns.Select((column, index) =>
            {
                var ordinal = ColumnOrdinal(intent.Columns, foreignKey.PrincipalColumns[index]);

                return $"doka_dependent.{Delimited(column)} = "
                    + $"doka_expected.{Delimited($"o{ordinal}")}";
            }));

            return $"EXISTS (SELECT 1 FROM {relation} "
                + $"JOIN {QualifiedTable(foreignKey.Table, foreignKey.Schema)} AS doka_dependent ON {match})";
        });

        return $"({string.Join(" OR ", checks)})";
    }

    private string DependencyCounts(
        DeleteModelManagedDataIntent intent,
        string relation
    )
    {
        if (intent.ForeignKeys.Count == 0)
        {
            return "N''";
        }

        var counts = intent.ForeignKeys.Select(foreignKey =>
        {
            var match = string.Join(" AND ", foreignKey.Columns.Select((column, index) =>
            {
                var ordinal = ColumnOrdinal(intent.Columns, foreignKey.PrincipalColumns[index]);

                return $"doka_dependent.{Delimited(column)} = "
                    + $"doka_expected.{Delimited($"o{ordinal}")}";
            }));

            return $"CONVERT(varchar(20), (SELECT COUNT_BIG(*) FROM {relation} "
                + $"JOIN {QualifiedTable(foreignKey.Table, foreignKey.Schema)} AS doka_dependent ON {match}))";
        });

        // WHY: COUNT_BIG is never NULL. Concatenation covers a single dependency
        // and avoids CONCAT_WS's 254-argument limit; varchar(max) prevents truncation.
        return "(" + string.Join(" + ',' + ", counts.Select(static (count, index) =>
            index == 0 ? $"CONVERT(varchar(max), {count})" : count)) + ")";
    }

    private string UnmodeledIncomingForeignKey(DeleteModelManagedDataIntent intent)
    {
        var modeled = intent.ForeignKeys.Select(foreignKey =>
        {
            var expected = string.Join(", ", foreignKey.Columns.Select((column, index) =>
                $"({index + 1}, {Literal(column)}, {Literal(foreignKey.PrincipalColumns[index])})"));

            return $"(fk.parent_object_id = {TableId(foreignKey.Table, foreignKey.Schema)} "
                + $"AND (SELECT COUNT(*) FROM sys.foreign_key_columns fkc "
                + $"WHERE fkc.constraint_object_id = fk.object_id) = {foreignKey.Columns.Count} "
                + $"AND NOT EXISTS (SELECT 1 FROM (VALUES {expected}) expected(ordinal, dependent, principal) "
                + "WHERE NOT EXISTS (SELECT 1 FROM sys.foreign_key_columns fkc "
                + "JOIN sys.columns dc ON dc.object_id = fkc.parent_object_id "
                + "AND dc.column_id = fkc.parent_column_id "
                + "JOIN sys.columns pc ON pc.object_id = fkc.referenced_object_id "
                + "AND pc.column_id = fkc.referenced_column_id "
                + "WHERE fkc.constraint_object_id = fk.object_id "
                + "AND fkc.constraint_column_id = expected.ordinal "
                + "AND dc.name = expected.dependent AND pc.name = expected.principal)))";
        });

        var known = intent.ForeignKeys.Count == 0 ? "1 = 0" : $"({string.Join(" OR ", modeled)})";

        // WHY: An unmodeled incoming FK can cascade or rewrite dependent rows.
        // Source-frozen FK metadata is the only evidence that the delete is safe.

        return "EXISTS (SELECT 1 FROM sys.foreign_keys fk "
            + $"WHERE fk.referenced_object_id = {TableId(intent.Table, intent.Schema)} "
            + $"AND NOT ({known}))";
    }

    private string QualifiedTable(
        string table,
        string? schema
    )
        => _sqlGenerationHelper.DelimitIdentifier(table, schema ?? "dbo");

    private string Delimited(string name) => _sqlGenerationHelper.DelimitIdentifier(name);

    private static int ColumnOrdinal(
        IReadOnlyList<string> columns,
        string column
    )
    {
        for (var index = 0; index < columns.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(columns[index], column))
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            $"Model-managed metadata references column '{column}' outside the captured value set.");
    }

    private static SafeMigrationFacetDifference ModelManagedRowContentDifference() => new(
        "model_managed_row_content", "modeled", "different_or_conflicting");
}
