namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Recognizes range-preserving built-in integer changes with unchanged authored side effects.</summary>
    /// <param name="source">The complete authored old column.</param>
    /// <param name="target">The complete authored target column.</param>
    /// <returns>True for a canonical integer CLR/store widening, never for numeric narrowing.</returns>
    internal bool IsSupportedIntegerWidening(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target
    )
    {
        if (GetUnsupportedColumnFeature(source) is not null || GetUnsupportedColumnFeature(target) is not null
            || !TryGetColumnType(source, out var oldType) || !TryGetColumnType(target, out var newType)
            || !CanonicalIntegerType(source.ClrType, oldType.Name)
            || !CanonicalIntegerType(target.ClrType, newType.Name)
            || IntegerWidth(oldType.Name) >= IntegerWidth(newType.Name)
            || source.Name != target.Name || source.IsUnicode != target.IsUnicode
            || source.MaxLength != target.MaxLength || source.IsFixedLength != target.IsFixedLength
            || source.Precision != target.Precision || source.Scale != target.Scale
            || source.IsRowVersion || target.IsRowVersion
            || source.Collation is not null || target.Collation is not null
            || source.Comment is not null || target.Comment is not null
            || source.ComputedColumnSql is not null || target.ComputedColumnSql is not null
            || source.ComputedExpression is not null || target.ComputedExpression is not null
            || source.IsStored is not null || target.IsStored is not null
            || !TryGetIdentity(source, out var oldIdentity, out var oldSeed, out var oldIncrement)
            || !TryGetIdentity(target, out var newIdentity, out var newSeed, out var newIncrement)
            || oldIdentity != newIdentity || oldSeed != newSeed || oldIncrement != newIncrement
            || source.DefaultValue.Kind != target.DefaultValue.Kind)
        {
            return false;
        }

        // WHY: EF drops and recreates an inline DEFAULT during ALTER. Equal
        // numeric literal values preserve the contract across int/long CLR
        // defaults, while the live stamp separately proves the exact source.
        return source.DefaultValue.Kind switch
        {
            SafeMigrationDefaultValueKind.None => true,
            SafeMigrationDefaultValueKind.Literal => source.DefaultValue.IsNullLiteral
                && target.DefaultValue.IsNullLiteral
                || source.DefaultValue.LiteralValue is not null && target.DefaultValue.LiteralValue is not null
                    && Convert.ToDecimal(source.DefaultValue.LiteralValue, CultureInfo.InvariantCulture)
                        == Convert.ToDecimal(target.DefaultValue.LiteralValue, CultureInfo.InvariantCulture),
            _ => DefaultExpression(source) == DefaultExpression(target),
        };
    }

    private SqlServerSafeMigrationRuntimePlan BuildIntegerWidening(
        AlterColumnIntent intent,
        bool orderedCapture = false,
        int reservedBytes = 0
    )
    {
        var source = intent.OldDefinition!;
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Definition.Name);
        var commentAbsent = "NOT EXISTS(SELECT 1 FROM sys.extended_properties ep JOIN sys.columns c "
            + "ON c.object_id=ep.major_id AND c.column_id=ep.minor_id "
            + $"WHERE ep.class=1 AND ep.major_id={TableId(intent.Table, intent.Schema)} "
            + $"AND c.name={Literal(intent.Definition.Name)} AND ep.name=N'MS_Description')";

        var matching = $"({ColumnMatches(intent.Table, intent.Schema, intent.Definition)}) AND ({commentAbsent})";
        var matchingProof = Bit(matching);
        var oldMatches = $"({ColumnMatches(intent.Table, intent.Schema, source)}) AND ({commentAbsent})";
        var layout = BuildIntegerWideningStoragePredicate(intent, reservedBytes);
        var dependencies = orderedCapture ? ImmutableIntegerWideningDependencies(intent)
            : ColumnHasDependencies(intent.Table, intent.Schema, intent.Definition.Name,
                allowAutomaticStatistics: true,
                allowInlineDefault: source.DefaultValue.Kind != SafeMigrationDefaultValueKind.None);

        var requiresRows = source.IsNullable && !intent.Definition.IsNullable;
        var unsafeRows = requiresRows
            ? $"EXISTS (SELECT TOP (1) 1 FROM {QualifiedTable(intent.Table, intent.Schema)} "
                + $"WHERE {Delimited(intent.Definition.Name)} IS NULL)" : "1 = 0";

        var canRead = requiresRows
            ? $"COALESCE(HAS_PERMS_BY_NAME({Literal(QualifiedTable(intent.Table, intent.Schema))}, "
                + "N'OBJECT', N'SELECT'), 0) = 1" : "1 = 1";

        // WHY: ALTER COLUMN can rewrite rows without emitting UPDATE DML.
        // SQL Server still requires UPDATE permission for that engine work;
        // exact metadata matching needs no write and remains an admitted NoOp.
        var canWrite = $"COALESCE(HAS_PERMS_BY_NAME({Literal(QualifiedTable(intent.Table, intent.Schema))}, "
            + "N'OBJECT',N'UPDATE'),0)=1";

        var writeAdmission = $"CASE WHEN {canWrite} THEN 1 ELSE ({matchingProof}) END";
        var repair = $"{oldMatches} AND ({layout}) AND NOT ({dependencies}) AND NOT ({unsafeRows})";
        var metadataGuard = $"({canRead}) AND (NOT {exists} OR ({writeAdmission})=1) "
            + $"AND (NOT {exists} OR {matching} OR ({layout}))";

        var permissionFailure = $"CASE WHEN NOT ({canRead}) THEN N'column_nullability_data_unproven' "
            + $"WHEN {exists} AND NOT ({canWrite}) AND NOT ({matching}) "
            + "THEN N'column_alter_write_permission' ELSE NULL END";

        return Plan($"CASE WHEN NOT {exists} THEN N'prerequisite_missing' WHEN {matching} THEN N'matching' "
            + $"WHEN {oldMatches} AND ({layout}) AND NOT ({dependencies}) AND ({unsafeRows}) "
            + "THEN N'data_blocked' ELSE N'different' END", matchingProof,
            SafeMigrationRepairCapability.Safe, Bit(repair)) with
        {
            RequiresDelayedBinding = requiresRows,
            RequiresExpressionDependencyRead = true,
            MayRequireNullabilityDataProof = requiresRows,
            RequiresLiveDataProof = requiresRows,
            RepairOperationalImpact = SafeMigrationOperationalImpact.TableRewritePossible,
            StateEvaluationGuardExpression = Bit(metadataGuard),
            StateEvaluationGuardFailureExpression = "N'unsupported'",
            ClassificationCodeExpression = $"CASE WHEN NOT ({canRead}) THEN N'column_nullability_data_unproven' "
                + $"WHEN {exists} AND NOT ({canWrite}) AND NOT ({matching}) "
                + "THEN N'column_alter_write_permission' "
                + $"WHEN {matching} THEN NULL "
                + $"WHEN NOT ({layout}) THEN N'column_widening_storage_unproven' ELSE NULL END",
            PrerequisiteFailureCodeExpression = permissionFailure,
            PostApplySql = intent.Definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.None
                ? null : BuildDefaultStampSql(intent.Table, intent.Schema, intent.Definition),
        };
    }

    /// <summary>Captures exact source, immutable blockers, and optional NULL evidence before accepted drops.</summary>
    /// <param name="intent">The already eligible lossless widening.</param>
    /// <param name="reservedBytes">Other eligible table widenings' worst-case retained byte growth.</param>
    /// <returns>A normal guarded candidate plan which does not erase named dependency evidence.</returns>
    internal SqlServerSafeMigrationRuntimePlan BuildIntegerWideningCapturePlan(
        AlterColumnIntent intent,
        int reservedBytes
    )
        => QualifyExpressionDependencyRead(BuildIntegerWidening(intent, orderedCapture: true, reservedBytes) with
        {
            PhysicalTableSupportExpression = BuildPhysicalTableSupportExpression(intent),
            DefaultValueSupportExpression = BuildDefaultValueSupportExpression(intent),
            PrerequisiteExpression = BuildPrerequisiteExpression(intent),
        });

    /// <summary>Gets the fixed scalar growth of an already eligible widening.</summary>
    internal int IntegerWideningGrowth(
        AlterColumnIntent intent
    )
    {
        TryGetColumnType(intent.OldDefinition!, out var oldType);
        TryGetColumnType(intent.Definition, out var newType);

        return IntegerWidth(newType.Name) - IntegerWidth(oldType.Name);
    }

    private string ImmutableIntegerWideningDependencies(
        AlterColumnIntent intent
    )
    {
        var id = TableId(intent.Table, intent.Schema);
        var columnId = $"(SELECT c.column_id FROM sys.columns c WHERE c.object_id={id} "
            + $"AND c.name={Literal(intent.Definition.Name)})";

        return "EXISTS(SELECT 1 FROM sys.stats_columns sc JOIN sys.stats s "
            + "ON s.object_id=sc.object_id AND s.stats_id=sc.stats_id "
            + $"WHERE sc.object_id={id} AND sc.column_id={columnId} AND s.auto_created=0 "
            + "AND NOT EXISTS(SELECT 1 FROM sys.indexes i WHERE i.object_id=s.object_id AND i.index_id=s.stats_id)) "
            + $"OR EXISTS(SELECT 1 FROM sys.columns c WHERE c.object_id={id} AND c.is_computed=1) "
            + $"OR EXISTS(SELECT 1 FROM sys.sql_expression_dependencies d WHERE d.referenced_id={id} "
            + $"AND (d.referenced_minor_id=0 OR d.referenced_minor_id={columnId}) "
            + "AND NOT EXISTS(SELECT 1 FROM sys.check_constraints cc WHERE cc.object_id=d.referencing_id) "
            + "AND NOT EXISTS(SELECT 1 FROM sys.foreign_keys fk WHERE fk.object_id=d.referencing_id) "
            + "AND NOT EXISTS(SELECT 1 FROM sys.default_constraints dc WHERE dc.object_id=d.referencing_id))";
    }

    private string BuildIntegerWideningStoragePredicate(
        AlterColumnIntent intent,
        int reservedBytes = 0
    )
    {
        TryGetColumnType(intent.OldDefinition!, out var oldType);
        TryGetColumnType(intent.Definition, out var newType);
        var increase = (IntegerWidth(newType.Name) - IntegerWidth(oldType.Name)).ToString(CultureInfo.InvariantCulture);
        var id = TableId(intent.Table, intent.Schema);

        // WHY: Error 511 documents temporary old-plus-new row capacity for
        // some ALTERs. Bound both declared rows, including variable payload,
        // offsets and version tags; row-overflow roots do not prove a payload
        // is currently off-row. This is declared-row admission, not proof that
        // prior ALTER modification records are absent. Engine errors 511/1708
        // remain authoritative and require transaction rollback, not a rebuild.
        var query = "SELECT COUNT_BIG(*) AS column_count, "
            + "COALESCE(SUM(CONVERT(bigint, CASE WHEN ty.name=N'bit' THEN 0 ELSE c.max_length END)),0) AS bytes, "
            + "COALESCE(SUM(CASE WHEN ty.name=N'bit' THEN 1 ELSE 0 END),0) AS bits, "
            + "COALESCE(SUM(CASE WHEN ty.name IN(N'varchar',N'nvarchar',N'varbinary') "
            + "THEN 1 ELSE 0 END),0) AS variables, "
            + "COALESCE(SUM(CASE WHEN c.max_length < 0 OR ty.is_user_defined<>0 OR ty.is_assembly_type<>0 "
            + "OR c.is_computed<>0 OR c.is_sparse<>0 OR c.is_column_set<>0 OR c.is_filestream<>0 "
            + "OR c.is_hidden<>0 OR c.generated_always_type<>0 OR c.encryption_type IS NOT NULL "
            + "OR ty.name NOT IN(N'bigint',N'binary',N'bit',N'char',N'date',N'datetime',N'datetime2', "
            + "N'datetimeoffset',N'decimal',N'float',N'int',N'money',N'nchar',N'numeric',N'nvarchar', "
            + "N'real',N'smalldatetime',N'smallint',N'smallmoney',N'time',N'timestamp',N'tinyint', "
            + "N'uniqueidentifier',N'varbinary',N'varchar') THEN 1 ELSE 0 END),0) AS unknown_columns "
            + $"FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id={id}";

        return "EXISTS(SELECT 1 FROM sys.tables t CROSS APPLY (" + query + ") layout "
            + $"WHERE t.object_id={id} AND t.max_column_id_used=layout.column_count AND layout.unknown_columns=0 "
            + "AND NOT EXISTS(SELECT 1 FROM sys.indexes i JOIN sys.partition_schemes ps "
            + "ON ps.data_space_id=i.data_space_id WHERE i.object_id=t.object_id AND i.index_id IN(0,1)) "
            + "AND NOT EXISTS(SELECT 1 FROM sys.indexes i WHERE i.object_id=t.object_id AND i.type NOT IN(0,1,2)) "
            + "AND NOT EXISTS(SELECT 1 FROM sys.partitions p WHERE p.object_id=t.object_id AND p.data_compression<>0) "
            + "AND EXISTS(SELECT 1 FROM sys.databases database_state WHERE database_state.database_id=DB_ID() "
            + "AND database_state.is_accelerated_database_recovery_on=0) "
            + "AND 2 * (layout.bytes + (layout.bits+7)/8 + 6 + (layout.column_count+7)/8 "
            + "+ CASE WHEN layout.variables>0 THEN 2+2*layout.variables ELSE 0 END + 14) "
            + $"+ {increase} + {reservedBytes.ToString(CultureInfo.InvariantCulture)} * 2 <= 8060)";
    }

    private static int IntegerWidth(
        string type
    ) => type switch
    {
        "tinyint" => 1,
        "smallint" => 2,
        "int" => 4,
        "bigint" => 8,
        _ => 0,
    };

    private static bool CanonicalIntegerType(
        Type clr,
        string type
    )
    {
        clr = Nullable.GetUnderlyingType(clr) ?? clr;

        return type switch
        {
            "tinyint" => clr == typeof(byte),
            "smallint" => clr == typeof(short),
            "int" => clr == typeof(int),
            "bigint" => clr == typeof(long),
            _ => false,
        };
    }
}
