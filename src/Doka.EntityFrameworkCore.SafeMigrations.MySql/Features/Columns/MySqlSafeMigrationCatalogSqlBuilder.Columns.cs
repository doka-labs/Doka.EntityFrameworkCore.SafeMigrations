namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private string? GetUnsupportedColumnFeature(
        SafeMigrationIntent intent,
        MySqlMigrationFeatureSet features,
        MySqlServerVersion serverVersion
    )
    {
        var isMariaDb = serverVersion.IsMariaDb;
        var definitions = intent switch
        {
            EnsureTableIntent value => value.Definition.Columns,
            EnsureColumnIntent value => [value.Definition],
            AlterColumnIntent value => [value.Definition],
            _ => [],
        };

        if (definitions.Any(definition => !CanMap(definition)))
        {
            return "column_type_mapping";
        }

        if (definitions.Any(static definition => definition.Collation?.Schema is not null))
        {
            return "schema_qualified_collation";
        }

        if (definitions.Any(definition => !CanRepresentLiteralDefault(definition, serverVersion)))
        {
            return "literal_default_catalog_representation";
        }

        if (definitions.Any(HasUnsupportedProviderColumnAnnotation))
        {
            return "provider_column_annotation";
        }

        if (definitions.Any(definition =>
                (definition.ComputedColumnSql is not null || definition.ComputedExpression is not null)
                && !Supported(
                    features,
                    definition.IsStored == true
                        ? MySqlMigrationFeature.StoredGeneratedColumns
                        : MySqlMigrationFeature.VirtualGeneratedColumns)))
        {
            return "generated_column";
        }

        if (isMariaDb
            && definitions.Any(static definition => !definition.IsNullable
                && (definition.ComputedColumnSql is not null || definition.ComputedExpression is not null)))
        {
            // MariaDB's generated-column grammar has no NOT NULL facet and
            // reports the resulting column as nullable even when incoming DDL
            // contains that clause. Reject the unrepresentable contract before
            // target DDL instead of accepting a guaranteed postcondition fault.
            return "generated_column_nullability";
        }

        return definitions.Any(static definition => definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Sql)
            && !Supported(features, MySqlMigrationFeature.ExpressionDefaults)
                ? "expression_default"
                : null;
    }

    /// <summary>
    /// Determines whether a captured provider annotation cannot be represented
    /// by the current MySQL/MariaDB catalog comparison contract.
    /// </summary>
    /// <param name="definition">The immutable expected column definition.</param>
    /// <returns><see langword="true" /> when the annotation must fail closed.</returns>
    internal static bool HasUnsupportedProviderColumnAnnotation(
        ExpectedColumnDefinition definition
    ) => !MySqlSafeMigrationColumnMetadata.TryCreate(definition, out _);

    private MySqlSafeMigrationRuntimePlan BuildEnsureColumn(
        EnsureColumnIntent intent,
        bool isMariaDb,
        bool repairRequested,
        bool includeAnalysisEvidence,
        bool includeTransitionEvidence
    )
    {
        var tableExists = BaseTableExists(intent.Table);
        var columnExists = ColumnExists(intent.Table, intent.Definition.Name);
        var matching = BuildColumnMatches(intent.Table, intent.Definition, isMariaDb);
        var unsafeAdd = !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition);
        var repairCapability = repairRequested
            && MySqlSafeMigrationColumnMetadata.CanSafelyConverge(intent.Definition)
            ? SafeMigrationRepairCapability.Safe
            : SafeMigrationRepairCapability.None;

        var transition = repairCapability == SafeMigrationRepairCapability.Safe
            ? BuildColumnRepairTransition(
                intent.Table,
                intent.Definition,
                isMariaDb,
                includeTransitionEvidence)
            : MySqlColumnRepairTransition.None;

        var repairInvariant = repairCapability == SafeMigrationRepairCapability.Safe
            ? $"({BuildColumnRepairInvariantMatches(intent.Table, intent.Definition, isMariaDb)}) "
                + $"OR ({transition.InvariantExpression})"
            : "FALSE";

        var dataBlocked = unsafeAdd ? $"EXISTS (SELECT 1 FROM {Delimited(intent.Table)} LIMIT 1)" : "FALSE";
        var hasNull = repairCapability == SafeMigrationRepairCapability.Safe
            && !intent.Definition.IsNullable
                ? $"EXISTS (SELECT 1 FROM {Delimited(intent.Table)} WHERE "
                + $"{Delimited(intent.Definition.Name)} IS NULL LIMIT 1)"
                : "FALSE";

        var dataTransitionBlocked = transition.DataBlockedExpression;
        var repairDataBlocked = $"({hasNull}) OR ({dataTransitionBlocked})";

        var repairPrecondition = repairCapability == SafeMigrationRepairCapability.Safe
            ? $"({repairInvariant}) AND NOT ({repairDataBlocked}) "
                + $"AND ({transition.ExecutionInvariantExpression})"
            : "FALSE";

        var plan = Plan(
            $"CASE WHEN NOT {tableExists} THEN 'prerequisite_missing' "
            + $"WHEN NOT {columnExists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {columnExists} THEN 'missing' "
            + $"WHEN {matching} THEN 'matching' "
            + $"WHEN ({repairInvariant}) AND ({repairDataBlocked}) THEN 'data_blocked' ELSE 'different' END",
            matching,
            repairCapability,
            repairPrecondition) with
        {
            ClassificationCodeExpression = transition.HasDataProbe
                ? $"CASE WHEN {dataTransitionBlocked} THEN 'varchar_narrowing_value_too_long' ELSE NULL END"
                : null,
            RepairOperationalImpact = transition.OperationalImpact,
            DataProbe = transition.DataProbe,
            MayRequireNullabilityDataProof = repairCapability == SafeMigrationRepairCapability.Safe
                && !intent.Definition.IsNullable,
            DiagnosticEvidenceExpression = includeAnalysisEvidence
                ? BuildColumnDiagnosticEvidence(
                    intent.Table,
                    intent.Definition,
                    isMariaDb)
                : null,
        };

        // The data probe for nullability tightening can mention the target
        // column only after the catalog proves that the column exists. Missing
        // remains an applicable state rather than a missing prerequisite.
        return repairCapability == SafeMigrationRepairCapability.Safe
            && (!intent.Definition.IsNullable || transition.HasDataProbe)
                ? plan with
                {
                    StateEvaluationGuardExpression = columnExists,
                    StateEvaluationGuardFailureExpression = unsafeAdd
                        ? $"CASE WHEN {dataBlocked} THEN 'data_blocked' ELSE 'missing' END"
                        : "'missing'",
                }
                : plan;
    }

    private MySqlSafeMigrationRuntimePlan BuildDropColumn(
        DropColumnIntent intent
    )
    {
        var objectExists = TableExists(intent.Table);
        var tableExists = BaseTableExists(intent.Table);
        var columnExists = ColumnExists(intent.Table, intent.Name);

        return Plan(
            $"CASE WHEN NOT {objectExists} THEN 'missing' "
            + $"WHEN NOT {tableExists} THEN 'different' "
            + $"WHEN NOT {columnExists} THEN 'missing' ELSE 'matching' END",
            $"NOT {columnExists}");
    }

    private MySqlSafeMigrationRuntimePlan BuildRenameColumn(
        RenameColumnIntent intent
    )
    {
        var tableExists = BaseTableExists(intent.Table);
        var sourceExists = ColumnExists(intent.Table, intent.Name);
        var targetExists = ColumnExists(intent.Table, intent.NewName);

        return Plan(
            $"CASE WHEN NOT {sourceExists} THEN 'missing' "
            + $"WHEN NOT {tableExists} THEN 'different' "
            + $"WHEN {targetExists} THEN 'different' ELSE 'matching' END",
            $"NOT {sourceExists}");
    }

    private MySqlSafeMigrationRuntimePlan BuildAlterColumn(
        AlterColumnIntent intent,
        bool isMariaDb
    )
    {
        var columnExists = ColumnExists(intent.Table, intent.Definition.Name);
        var matching = BuildColumnMatches(intent.Table, intent.Definition, isMariaDb);
        var repairCapability = intent.OldDefinition is not null
            && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(intent.OldDefinition, intent.Definition)
                ? SafeMigrationRepairCapability.Safe
                : SafeMigrationRepairCapability.None;

        var repairPrecondition = repairCapability == SafeMigrationRepairCapability.Safe
            ? BuildColumnMatches(intent.Table, intent.OldDefinition!, isMariaDb)
            : "FALSE";

        var nullBlocked =
            repairCapability == SafeMigrationRepairCapability.Safe
            && intent.OldDefinition!.IsNullable
            && !intent.Definition.IsNullable
                ? $"({repairPrecondition}) AND EXISTS (SELECT 1 FROM {Delimited(intent.Table)} WHERE "
                + $"{Delimited(intent.Definition.Name)} IS NULL LIMIT 1)"
                : "FALSE";

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(intent.Table)} OR NOT {columnExists} THEN 'different' "
            + $"WHEN {matching} THEN 'matching' "
            + $"WHEN {nullBlocked} THEN 'data_blocked' ELSE 'different' END",
            matching,
            repairCapability,
            repairPrecondition) with
        {
            MayRequireNullabilityDataProof = repairCapability == SafeMigrationRepairCapability.Safe
                && intent.OldDefinition!.IsNullable
                && !intent.Definition.IsNullable,
        };
    }

    private string BuildColumnMatches(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb,
        int? ordinal = null,
        bool includeRepairableFacets = true,
        bool requireRepairSafeExtra = false
    )
    {
        var mapping = _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale)
            ?? throw new InvalidOperationException(
                $"No MySQL type mapping exists for '{definition.ClrType.FullName}'.");

        var storeType = definition.StoreType ?? mapping.StoreType;
        var temporalRowVersion = IsTemporalRowVersion(definition);
        var mariaDbJsonAlias = isMariaDb
            && StringComparer.OrdinalIgnoreCase.Equals(storeType.Trim(), "json");
        var collationContract = BuildCollationContract(table, definition.Collation, mariaDbJsonAlias);

        var conditions = new List<string>
        {
            BuildStoreTypeMatches(storeType, isMariaDb),
            collationContract.MatchExpression,
            BuildComputedMatches(definition, isMariaDb),
            BuildValueGenerationMatches(definition, temporalRowVersion),
        };

        if (includeRepairableFacets)
        {
            conditions.Add($"c.IS_NULLABLE = {(definition.IsNullable ? "'YES'" : "'NO'")}");
            conditions.Add($"COALESCE(c.COLUMN_COMMENT, '') = {Literal(definition.Comment ?? string.Empty)}");
            conditions.Add(
                BuildDefaultMatches(
                    "c.COLUMN_DEFAULT",
                    definition.DefaultValue,
                    definition.IsNullable,
                    mapping,
                    temporalRowVersion));
        }

        if (requireRepairSafeExtra)
        {
            conditions.Add(OrdinaryColumnExtraMatches());
        }

        if (ordinal is not null)
        {
            conditions.Add($"c.ORDINAL_POSITION = {ordinal.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
            + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
            + $"AND c.COLUMN_NAME = {Literal(definition.Name)} AND {string.Join(" AND ", conditions)})";
    }

    private string BuildColumnRepairInvariantMatches(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb
    ) => BuildColumnMatches(
        table,
        definition,
        isMariaDb,
        includeRepairableFacets: false,
        requireRepairSafeExtra: true);

    private string BuildColumnDiagnosticEvidence(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb
    )
    {
        var mapping = _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale)
            ?? throw new InvalidOperationException(
                $"No MySQL type mapping exists for '{definition.ClrType.FullName}'.");

        var storeType = definition.StoreType ?? mapping.StoreType;
        var temporalRowVersion = IsTemporalRowVersion(definition);
        var mariaDbJsonAlias = isMariaDb
            && StringComparer.OrdinalIgnoreCase.Equals(storeType.Trim(), "json");
        var collationContract = BuildCollationContract(table, definition.Collation, mariaDbJsonAlias);

        var defaultMatches = BuildDefaultMatches(
            "c.COLUMN_DEFAULT",
            definition.DefaultValue,
            definition.IsNullable,
            mapping,
            temporalRowVersion);

        var defaultKindMatches = BuildDefaultKindMatches(
            "c.COLUMN_DEFAULT",
            definition.DefaultValue,
            definition.IsNullable,
            temporalRowVersion);

        var expectedDefaultPayload = DefaultDiagnosticPayload(
            definition.DefaultValue,
            mapping,
            temporalRowVersion);

        var records = new List<string>
        {
            DiagnosticRecord(
                $"NOT ({BuildStoreTypeMatches(storeType, isMariaDb)})",
                "column_store_type",
                Literal(storeType),
                "LEFT(COALESCE(c.COLUMN_TYPE, 'none'), 256)"),
            DiagnosticRecord(
                $"c.IS_NULLABLE <> {(definition.IsNullable ? "'YES'" : "'NO'")}",
                "column_nullability",
                definition.IsNullable ? "'nullable'" : "'not_nullable'",
                "CASE c.IS_NULLABLE WHEN 'YES' THEN 'nullable' WHEN 'NO' THEN 'not_nullable' ELSE 'unknown' END"),
            DiagnosticRecord(
                $"NOT ({collationContract.MatchExpression})",
                "column_collation",
                BuildCollationExpectedExpression(collationContract),
                "LEFT(COALESCE(c.COLLATION_NAME, 'none'), 256)"),
            DiagnosticRecord(
                $"NOT ({defaultKindMatches})",
                "column_default_kind",
                Literal(DefaultKind(definition.DefaultValue, definition.IsNullable, temporalRowVersion)),
                "CASE WHEN c.COLUMN_DEFAULT IS NULL THEN 'none' "
                    + "WHEN UPPER(TRIM(c.COLUMN_DEFAULT)) = 'NULL' THEN 'null' ELSE 'present' END"),
            DiagnosticRecord(
                $"NOT ({defaultMatches})",
                "column_default_digest",
                $"CONCAT('sha256:', LOWER(SHA2({Literal(expectedDefaultPayload)}, 256)))",
                "CONCAT('sha256:', LOWER(SHA2(COALESCE(c.COLUMN_DEFAULT, '<none>'), 256)))"),
            DiagnosticRecord(
                $"NOT ({BuildValueGenerationMatches(definition, temporalRowVersion)})",
                "column_value_generation",
                Literal(ValueGenerationKind(definition, temporalRowVersion)),
                "CASE WHEN LOCATE('auto_increment', LOWER(COALESCE(c.EXTRA, ''))) > 0 THEN 'identity' "
                    + "WHEN LOCATE('on update', LOWER(COALESCE(c.EXTRA, ''))) > 0 THEN 'row_version' "
                    + "WHEN LOCATE('generated', LOWER(COALESCE(c.EXTRA, ''))) > 0 THEN 'generated' "
                    + "ELSE 'none' END"),
            DiagnosticRecord(
                $"COALESCE(c.COLUMN_COMMENT, '') <> {Literal(definition.Comment ?? string.Empty)}",
                "column_comment_digest",
                $"LOWER(SHA2({Literal(definition.Comment ?? string.Empty)}, 256))",
                "LOWER(SHA2(COALESCE(c.COLUMN_COMMENT, ''), 256))"),
        };

        if (TryParseVarcharLength(storeType, out var targetLength))
        {
            records.Insert(
                1,
                DiagnosticRecord(
                    $"c.CHARACTER_MAXIMUM_LENGTH <> {targetLength}",
                    "column_max_length",
                    Literal(targetLength.ToString(CultureInfo.InvariantCulture)),
                    "COALESCE(CAST(c.CHARACTER_MAXIMUM_LENGTH AS CHAR), 'none')"));
        }

        return "(SELECT NULLIF(CONCAT_WS(CHAR(30), "
            + string.Join(", ", records)
            + "), '') FROM INFORMATION_SCHEMA.COLUMNS c "
            + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
            + $"AND c.COLUMN_NAME = {Literal(definition.Name)} LIMIT 1)";
    }

    private static string DiagnosticRecord(
        string mismatch,
        string facet,
        string expected,
        string actual
    ) => $"CASE WHEN {mismatch} THEN CONCAT_WS(CHAR(31), '{facet}', {expected}, {actual}) END";

    private static string DefaultKind(
        SafeMigrationDefaultValue value,
        bool isNullable,
        bool temporalRowVersion
    )
    {
        if (temporalRowVersion)
        {
            return "present";
        }

        if (value.Kind == SafeMigrationDefaultValueKind.Literal
            && value.IsNullLiteral)
        {
            return "null";
        }

        if (value.Kind != SafeMigrationDefaultValueKind.None)
        {
            return "present";
        }

        return isNullable ? "none_or_null" : "none";
    }

    private string DefaultDiagnosticPayload(
        SafeMigrationDefaultValue value,
        RelationalTypeMapping mapping,
        bool temporalRowVersion
    )
    {
        if (temporalRowVersion)
        {
            return "CURRENT_TIMESTAMP(6)";
        }

        if (value.Kind == SafeMigrationDefaultValueKind.None)
        {
            return "<none>";
        }

        if (value.Kind == SafeMigrationDefaultValueKind.Sql)
        {
            return value.SqlExpression ?? _expressionRenderer.Render(value.StructuredExpression!);
        }

        var literal = value.GetLiteralValue();

        return literal is null ? "NULL" : mapping.GenerateSqlLiteral(literal);
    }

    private static string ValueGenerationKind(
        ExpectedColumnDefinition definition,
        bool temporalRowVersion
    )
    {
        if (temporalRowVersion)
        {
            return "row_version";
        }

        return MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata)
            && metadata.ValueGenerationStrategy == MySqlValueGenerationStrategy.AutoIncrement
                ? "identity"
                : definition.ComputedColumnSql is not null || definition.ComputedExpression is not null
                    ? "generated"
                    : "none";
    }

    private MySqlColumnRepairTransition BuildColumnRepairTransition(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb,
        bool includeTransitionEvidence
    )
    {
        var storeType = ResolveStoreType(definition);
        if (TryParseVarcharLength(storeType, out var targetLength)
            && definition.ClrType == typeof(string))
        {
            var narrowing = $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS narrowing_column "
                + "WHERE narrowing_column.TABLE_SCHEMA = DATABASE() "
                + $"AND narrowing_column.TABLE_NAME = {Literal(table)} "
                + $"AND narrowing_column.COLUMN_NAME = {Literal(definition.Name)} "
                + $"AND narrowing_column.CHARACTER_MAXIMUM_LENGTH > {targetLength})";

            var strictMode = $"NOT ({narrowing}) "
                + "OR FIND_IN_SET('STRICT_TRANS_TABLES', @@SESSION.sql_mode) > 0 "
                + "OR FIND_IN_SET('STRICT_ALL_TABLES', @@SESSION.sql_mode) > 0";

            return new MySqlColumnRepairTransition(
                MySqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
                MySqlSafeMigrationRuntimePlan.DataProbePlaceholder,
                strictMode,
                new MySqlSafeMigrationDataProbe(
                    table,
                    definition.Name,
                    targetLength,
                    Delimited(table),
                    Delimited(definition.Name),
                    includeTransitionEvidence
                        ? BuildVarcharTransitionColumnExists(
                            table,
                            definition,
                            targetLength,
                            isMariaDb)
                        : null,
                    narrowing),
                SafeMigrationOperationalImpact.TableRewritePossible);
        }

        if (IsBooleanTinyInt(storeType, definition.ClrType))
        {
            if (!IsSupportedBooleanTarget(definition))
            {
                return MySqlColumnRepairTransition.None;
            }

            var columnInvariant = "LOWER(c.DATA_TYPE) = 'bit' AND LOWER(c.COLUMN_TYPE) = 'bit(1)' "
                + $"AND {BooleanSourceDefaultIsRepresentable()} "
                + $"AND {BuildComputedMatches(definition, isMariaDb)} "
                + $"AND {BuildValueGenerationMatches(definition, temporalRowVersion: false)} "
                + $"AND {OrdinaryColumnExtraMatches()} "
                + $"AND {NoForeignKeyDependency(table, definition.Name)}";

            var transitionInvariant = ColumnWithInvariantExists(
                table,
                definition.Name,
                columnInvariant);

            return new MySqlColumnRepairTransition(
                transitionInvariant,
                "FALSE",
                "TRUE",
                DataProbe: null,
                SafeMigrationOperationalImpact.TableRewritePossible);
        }

        return MySqlColumnRepairTransition.None;
    }

    private static bool IsSupportedBooleanTarget(
        ExpectedColumnDefinition definition
    )
    {
        if (!MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata)
            || metadata.GuidFormat is not null
            || metadata.ValueGenerationStrategy is not null and not MySqlValueGenerationStrategy.None)
        {
            return false;
        }

        return definition.DefaultValue.Kind switch
        {
            SafeMigrationDefaultValueKind.None => true,
            SafeMigrationDefaultValueKind.Literal => definition.DefaultValue.GetLiteralValue() is null or bool,
            SafeMigrationDefaultValueKind.Sql => false,
            _ => false,
        };
    }

    private static string BooleanSourceDefaultIsRepresentable() =>
        // WHY: A BIT(1) value is losslessly representable as TINYINT(1), but an
        // arbitrary default expression is a separate behavioral contract. Only
        // the catalog spellings proven to represent the Boolean literal domain
        // are eligible for automatic repair.
        "(c.COLUMN_DEFAULT IS NULL OR UPPER(TRIM(c.COLUMN_DEFAULT)) "
        + "IN ('NULL', '0', '1', 'B''0''', 'B''1'''))";

    private string NoForeignKeyDependency(
        string table,
        string column
    )
    {
        var builder = new StringBuilder(768);

        AppendNoForeignKeyDependency(builder, table, column);

        return builder.ToString();
    }

    private void AppendNoForeignKeyDependency(
        StringBuilder builder,
        string table,
        string column
    )
    {
        // WHY: MySQL requires compatible types on both sides of a foreign key.
        // A single-column repair cannot prove or atomically apply the required
        // coupled transition, so ownership remains with an explicit migration.
        builder
            .Append("NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE dependency ")
            .Append("WHERE dependency.CONSTRAINT_SCHEMA = DATABASE() ")
            .Append("AND dependency.REFERENCED_TABLE_NAME IS NOT NULL AND ((")
            .Append("dependency.TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND dependency.COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(") OR (dependency.REFERENCED_TABLE_SCHEMA = DATABASE() ")
            .Append("AND dependency.REFERENCED_TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND dependency.REFERENCED_COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(")))");
    }

    private string ColumnWithInvariantExists(
        string table,
        string column,
        string invariant
    ) => "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
        + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
        + $"AND c.COLUMN_NAME = {Literal(column)} AND ({invariant}))";

    private string BuildVarcharTransitionColumnExists(
        string table,
        ExpectedColumnDefinition definition,
        int targetLength,
        bool isMariaDb
    )
    {
        // WHY: The fixed predicate body is approximately 5.3K characters and
        // references seven table plus six column literals. Deriving the initial
        // capacity avoids retaining a mostly empty 6K buffer per operation;
        // StringBuilder can still grow for escaped or unusually long names.
        var initialCapacity = checked(
            5400
            + (Literal(table).Length * 7)
            + (Literal(definition.Name).Length * 6));
        var builder = new StringBuilder(initialCapacity);

        builder
            .Append("EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c ")
            .Append("WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND c.COLUMN_NAME = ")
            .Append(Literal(definition.Name))
            .Append(" AND (LOWER(c.DATA_TYPE) = 'varchar' ")
            .Append("AND c.CHARACTER_MAXIMUM_LENGTH <> ")
            .Append(targetLength.ToString(CultureInfo.InvariantCulture))
            .Append(" AND ");

        AppendSupportedVarcharRepairTable(builder, table);
        builder
            .Append(" AND ")
            .Append(BuildCollationContract(table, definition.Collation, mariaDbJsonAlias: false).MatchExpression)
            .Append(" AND ")
            .Append(BuildComputedMatches(definition, isMariaDb))
            .Append(" AND ")
            .Append(BuildValueGenerationMatches(definition, temporalRowVersion: false))
            .Append(" AND ")
            .Append(OrdinaryColumnExtraMatches())
            .Append(" AND ");

        AppendTargetDeclaredRowSizeFits(builder, table, definition.Name, targetLength);
        builder.Append(" AND ");
        AppendDependentIndexesRemainRepresentable(builder, table, definition.Name, targetLength);
        builder.Append(" AND ");
        AppendNoForeignKeyDependency(builder, table, definition.Name);
        builder.Append("))");

        return builder.ToString();
    }

    private void AppendSupportedVarcharRepairTable(
        StringBuilder builder,
        string table
    ) => builder
        .Append("EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES repair_table ")
        .Append("WHERE repair_table.TABLE_SCHEMA = DATABASE() AND repair_table.TABLE_NAME = ")
        .Append(Literal(table))
        .Append(" AND repair_table.TABLE_TYPE = 'BASE TABLE' ")
        .Append("AND UPPER(repair_table.ENGINE) = 'INNODB')");

    private void AppendTargetDeclaredRowSizeFits(
        StringBuilder builder,
        string table,
        string column,
        int targetLength
    )
    {
        // WHY: MySQL rejects a declared row above 65,535 bytes before
        // InnoDB can move long values off-page. Unknown families consume the
        // complete budget so incomplete catalog knowledge fails closed.
        var targetLengthSql = targetLength.ToString(CultureInfo.InvariantCulture);

        builder
            .Append("COALESCE((SELECT SUM(CASE WHEN row_column.COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(" THEN (")
            .Append(targetLengthSql)
            .Append(" * COALESCE(character_set.MAXLEN, 1)) + CASE WHEN ")
            .Append(targetLengthSql)
            .Append(" * COALESCE(character_set.MAXLEN, 1) <= 255 THEN 1 ELSE 2 END ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('varchar', 'varbinary') ")
            .Append("THEN row_column.CHARACTER_OCTET_LENGTH ")
            .Append("+ CASE WHEN row_column.CHARACTER_OCTET_LENGTH <= 255 THEN 1 ELSE 2 END ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('char', 'binary') ")
            .Append("THEN row_column.CHARACTER_OCTET_LENGTH ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('tinyblob', 'tinytext', 'blob', 'text', ")
            .Append("'mediumblob', 'mediumtext', 'longblob', 'longtext', 'json', 'geometry') THEN 12 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'tinyint' THEN 1 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'smallint' THEN 2 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'mediumint' THEN 3 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('int', 'integer', 'float') THEN 4 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('bigint', 'double') THEN 8 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'decimal' ")
            .Append("THEN CEIL(COALESCE(row_column.NUMERIC_PRECISION, 65) / 2) ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'bit' ")
            .Append("THEN CEIL(COALESCE(row_column.NUMERIC_PRECISION, 64) / 8) ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'date' THEN 3 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('time', 'datetime', 'timestamp') THEN 8 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'year' THEN 1 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'enum' THEN 2 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'set' THEN 8 ")
            .Append("ELSE 65536 END) ")
            .Append("+ CEIL(SUM(CASE WHEN row_column.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END) / 8) ")
            .Append("FROM INFORMATION_SCHEMA.COLUMNS row_column ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.CHARACTER_SETS character_set ")
            .Append("ON character_set.CHARACTER_SET_NAME = row_column.CHARACTER_SET_NAME ")
            .Append("WHERE row_column.TABLE_SCHEMA = DATABASE() AND row_column.TABLE_NAME = ")
            .Append(Literal(table))
            .Append("), 65536) <= 65535");
    }

    private void AppendDependentIndexesRemainRepresentable(
        StringBuilder builder,
        string table,
        string column,
        int targetLength
    )
    {
        builder
            .Append("NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS target_index ")
            .Append("WHERE target_index.TABLE_SCHEMA = DATABASE() AND target_index.TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND target_index.COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(" AND target_index.SUB_PART IS NULL AND (SELECT COALESCE(SUM(CASE ")
            .Append("WHEN index_part.COLUMN_NAME IS NULL THEN 3073 ")
            .Append("WHEN index_part.COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(" AND index_part.SUB_PART IS NULL THEN ")
            .Append(targetLength.ToString(CultureInfo.InvariantCulture))
            .Append(" * COALESCE(character_set.MAXLEN, 1) ")
            .Append("WHEN index_part.SUB_PART IS NOT NULL ")
            .Append("THEN index_part.SUB_PART * CASE WHEN indexed_column.COLLATION_NAME IS NULL ")
            .Append("THEN 1 ELSE COALESCE(character_set.MAXLEN, 1) END ")
            .Append("WHEN indexed_column.CHARACTER_OCTET_LENGTH IS NOT NULL ")
            .Append("THEN indexed_column.CHARACTER_OCTET_LENGTH ")
            .Append("WHEN LOWER(indexed_column.DATA_TYPE) = 'tinyint' THEN 1 ")
            .Append("WHEN LOWER(indexed_column.DATA_TYPE) = 'smallint' THEN 2 ")
            .Append("WHEN LOWER(indexed_column.DATA_TYPE) = 'mediumint' THEN 3 ")
            .Append("WHEN LOWER(indexed_column.DATA_TYPE) IN ('int', 'integer', 'float') THEN 4 ")
            .Append("WHEN LOWER(indexed_column.DATA_TYPE) ")
            .Append("IN ('bigint', 'double', 'datetime', 'timestamp') THEN 8 ")
            .Append("WHEN LOWER(indexed_column.DATA_TYPE) = 'date' THEN 3 ")
            .Append("WHEN LOWER(indexed_column.DATA_TYPE) = 'time' THEN 6 ")
            .Append("WHEN LOWER(indexed_column.DATA_TYPE) = 'year' THEN 1 ")
            .Append("WHEN LOWER(indexed_column.DATA_TYPE) = 'decimal' ")
            .Append("THEN CEIL(COALESCE(indexed_column.NUMERIC_PRECISION, 65) / 2) ")
            .Append("ELSE 3073 END), 0) FROM INFORMATION_SCHEMA.STATISTICS index_part ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.COLUMNS indexed_column ")
            .Append("ON indexed_column.TABLE_SCHEMA = index_part.TABLE_SCHEMA ")
            .Append("AND indexed_column.TABLE_NAME = index_part.TABLE_NAME ")
            .Append("AND indexed_column.COLUMN_NAME = index_part.COLUMN_NAME ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.COLLATIONS collation ")
            .Append("ON collation.COLLATION_NAME = indexed_column.COLLATION_NAME ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.CHARACTER_SETS character_set ")
            .Append("ON character_set.CHARACTER_SET_NAME = collation.CHARACTER_SET_NAME ")
            .Append("WHERE index_part.TABLE_SCHEMA = target_index.TABLE_SCHEMA ")
            .Append("AND index_part.TABLE_NAME = target_index.TABLE_NAME ")
            .Append("AND index_part.INDEX_NAME = target_index.INDEX_NAME) > ")
            .Append("COALESCE((SELECT CASE WHEN UPPER(dependent_table.ENGINE) = 'INNODB' THEN ")
            .Append(BuildMaximumIndexKeyWidth("dependent_table"))
            .Append(" ELSE 0 END FROM INFORMATION_SCHEMA.TABLES dependent_table ")
            .Append("WHERE dependent_table.TABLE_SCHEMA = target_index.TABLE_SCHEMA ")
            .Append("AND dependent_table.TABLE_NAME = target_index.TABLE_NAME LIMIT 1), 0))");
    }

    private string ResolveStoreType(
        ExpectedColumnDefinition definition
    ) => definition.StoreType
        ?? _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale)
            ?.StoreType
        ?? throw new InvalidOperationException(
            $"No MySQL type mapping exists for '{definition.ClrType.FullName}'.");

    private static bool TryParseVarcharLength(
        string storeType,
        out int length
    )
    {
        var value = storeType.AsSpan().Trim();
        const string prefix = "varchar(";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || value[^1] != ')'
            || !int.TryParse(value[prefix.Length..^1], CultureInfo.InvariantCulture, out length))
        {
            length = 0;

            return false;
        }

        return length > 0;
    }

    private static bool IsBooleanTinyInt(
        string storeType,
        Type clrType
    )
    {
        var underlyingType = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return underlyingType == typeof(bool)
            && StringComparer.OrdinalIgnoreCase.Equals(storeType.Trim(), "tinyint(1)");
    }

    private sealed record MySqlColumnRepairTransition(
        string InvariantExpression,
        string DataBlockedExpression,
        string ExecutionInvariantExpression,
        MySqlSafeMigrationDataProbe? DataProbe,
        SafeMigrationOperationalImpact OperationalImpact
    )
    {
        public bool HasDataProbe => DataProbe is not null;

        public static MySqlColumnRepairTransition None { get; } = new(
            "FALSE",
            "FALSE",
            "TRUE",
            DataProbe: null,
            SafeMigrationOperationalImpact.NotApplicable);
    }

    private static string OrdinaryColumnExtraMatches() =>
        // MySQL and MariaDB require the complete column definition for MODIFY
        // COLUMN. Reject unmodeled EXTRA metadata because omitting ON UPDATE,
        // INVISIBLE, or a similar modifier would silently erase it. MySQL may
        // expose DEFAULT_GENERATED for an otherwise modeled expression default.
        "TRIM(REPLACE(LOWER(COALESCE(c.EXTRA, '')), 'default_generated', '')) = ''";

    private static string BuildValueGenerationMatches(
        ExpectedColumnDefinition definition,
        bool temporalRowVersion
    )
    {
        if (!MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata))
        {
            return "FALSE";
        }

        if (metadata.ValueGenerationStrategy == MySqlValueGenerationStrategy.AutoIncrement)
        {
            return "LOCATE('auto_increment', LOWER(c.EXTRA)) > 0";
        }

        if (temporalRowVersion)
        {
            // Doka materializes a temporal row version as both a generated
            // default and an ON UPDATE expression. Comparing the complete
            // normalized EXTRA value prevents an unrelated provider modifier
            // from being accepted as part of that owned contract.
            return "LOCATE('auto_increment', LOWER(c.EXTRA)) = 0 "
                + "AND TRIM(REPLACE(LOWER(COALESCE(c.EXTRA, '')), "
                + "'default_generated', '')) = 'on update current_timestamp(6)'";
        }

        return "LOCATE('auto_increment', LOWER(c.EXTRA)) = 0";
    }

    private static bool IsTemporalRowVersion(
        ExpectedColumnDefinition definition
    )
    {
        if (!definition.IsRowVersion)
        {
            return false;
        }

        if (definition.StoreType is not null)
        {
            var normalizedStoreType = definition
                .StoreType
                .AsSpan()
                .TrimStart();

            return normalizedStoreType.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)
                || normalizedStoreType.StartsWith("datetime", StringComparison.OrdinalIgnoreCase);
        }

        // This is the same fallback Doka applies to a hand-authored migration
        // without ColumnType. Keeping the predicate aligned with the provider's
        // DDL decision prevents inferred type mappings from changing ownership.
        var clrType = Nullable.GetUnderlyingType(definition.ClrType) ?? definition.ClrType;

        return clrType == typeof(byte[]) || clrType == typeof(DateTime) || clrType == typeof(DateTimeOffset);
    }

    private CollationContract BuildCollationContract(
        string table,
        SafeMigrationCollationIdentifier? expectedCollation,
        bool mariaDbJsonAlias
    )
    {
        if (mariaDbJsonAlias)
        {
            // Doka materializes MariaDB's JSON alias as LONGTEXT with the
            // binary JSON collation. Compare the provider-owned physical
            // representation, not the table default inherited by ordinary text.
            return new CollationContract(
                "LOWER(c.COLLATION_NAME) = 'utf8mb4_bin'",
                CollationExpectationKind.Expression,
                "'utf8mb4_bin'");
        }

        if (expectedCollation is not null)
        {
            var expectedExpression = Literal(expectedCollation.Name);

            return new CollationContract(
                $"c.COLLATION_NAME <=> {expectedExpression}",
                CollationExpectationKind.Expression,
                expectedExpression);
        }

        // WHY: The expected table-default expression is only needed for
        // diagnostics. Retaining the table name avoids materializing a second
        // catalog subquery for every comparison-only operation.
        return new CollationContract(
            "(c.COLLATION_NAME IS NULL OR c.COLLATION_NAME <=> "
            + "(SELECT t.TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES t "
            + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(table)}))",
            CollationExpectationKind.InheritedTableDefault,
            table);
    }

    private string BuildCollationExpectedExpression(
        CollationContract contract
    ) => contract.ExpectationKind switch
    {
        CollationExpectationKind.Expression => contract.ExpectationSource,
        CollationExpectationKind.InheritedTableDefault =>
            "COALESCE((SELECT t.TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES t "
            + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(contract.ExpectationSource)}), 'none')",
        _ => throw new InvalidOperationException(
            $"Unsupported collation expectation kind '{contract.ExpectationKind}'."),
    };

    private enum CollationExpectationKind : byte
    {
        Expression = 1,
        InheritedTableDefault = 2,
    }

    private readonly record struct CollationContract(
        string MatchExpression,
        CollationExpectationKind ExpectationKind,
        string ExpectationSource);

    private string BuildStoreTypeMatches(
        string storeType,
        bool isMariaDb
    )
    {
        if (isMariaDb
            && StringComparer.OrdinalIgnoreCase.Equals(storeType.Trim(), "json"))
        {
            return "LOWER(c.DATA_TYPE) = 'longtext'";
        }

        if (!isMariaDb
            || storeType.Contains('(', StringComparison.Ordinal))
        {
            return $"LOWER(c.COLUMN_TYPE) = LOWER({Literal(storeType)})";
        }

        var normalized = storeType
            .Trim()
            .ToLowerInvariant();

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var integerType = parts[0] is "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint";
        if (!integerType)
        {
            return $"LOWER(c.COLUMN_TYPE) = {Literal(normalized)}";
        }

        var canonicalType = parts[0] == "integer" ? "int" : parts[0];
        var expectedUnsigned = parts.Contains("unsigned", StringComparer.Ordinal);
        var expected = expectedUnsigned ? $"{canonicalType} unsigned" : canonicalType;

        return $"CONCAT(LOWER(c.DATA_TYPE), "
            + $"CASE WHEN LOCATE('unsigned', LOWER(c.COLUMN_TYPE)) > 0 "
            + $"THEN ' unsigned' ELSE '' END) = {Literal(expected)}";
    }

    private string BuildDefaultMatches(
        string catalogExpression,
        SafeMigrationDefaultValue expected,
        bool isNullable,
        RelationalTypeMapping mapping,
        bool temporalRowVersion = false
    )
    {
        if (expected.Kind == SafeMigrationDefaultValueKind.None)
        {
            if (temporalRowVersion)
            {
                var providerDefaultCandidates = BuildDefaultSqlCandidates("CURRENT_TIMESTAMP(6)")
                    .Select(Literal);

                return $"{catalogExpression} IN ({string.Join(", ", providerDefaultCandidates)})";
            }

            return isNullable
                ? $"({catalogExpression} IS NULL OR UPPER({catalogExpression}) = 'NULL')"
                : $"{catalogExpression} IS NULL";
        }

        if (expected.Kind == SafeMigrationDefaultValueKind.Sql)
        {
            var expression = expected.SqlExpression ?? _expressionRenderer.Render(expected.StructuredExpression!);
            var sqlCandidates = BuildDefaultSqlCandidates(expression)
                .Select(Literal);

            return $"{catalogExpression} IN ({string.Join(", ", sqlCandidates)})";
        }

        var value = expected.GetLiteralValue();
        if (value is null)
        {
            return $"({catalogExpression} IS NULL OR UPPER({catalogExpression}) = 'NULL')";
        }

        if (value is byte[] bytes)
        {
            return BuildBinaryDefaultMatches(catalogExpression, bytes);
        }

        var providerLiteral = mapping.GenerateSqlLiteral(value);
        var candidates = new HashSet<string>(StringComparer.Ordinal) { providerLiteral };
        AddSimpleStringLiteralDisplayCandidate(candidates, providerLiteral);
        AddExpressionDefaultDisplayCandidate(candidates, providerLiteral);

        if (value is string text)
        {
            AddQuotedStringDefaultDisplayCandidate(candidates, text);
        }

        var invariant = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (invariant is not null)
        {
            candidates.Add(invariant);
        }

        if (value is bool boolean)
        {
            candidates.Add(boolean ? "1" : "0");
        }

        AddTemporalDefaultCandidates(candidates, value);

        return $"{catalogExpression} IN ({string.Join(", ", candidates.Select(Literal))})";
    }

    private static string BuildDefaultKindMatches(
        string catalogExpression,
        SafeMigrationDefaultValue expected,
        bool isNullable,
        bool temporalRowVersion
    )
    {
        if (temporalRowVersion)
        {
            return $"{catalogExpression} IS NOT NULL "
                + $"AND UPPER(TRIM({catalogExpression})) <> 'NULL'";
        }

        if (expected.Kind == SafeMigrationDefaultValueKind.Literal
            && expected.IsNullLiteral)
        {
            return $"({catalogExpression} IS NULL OR UPPER(TRIM({catalogExpression})) = 'NULL')";
        }

        if (expected.Kind != SafeMigrationDefaultValueKind.None)
        {
            return $"{catalogExpression} IS NOT NULL "
                + $"AND UPPER(TRIM({catalogExpression})) <> 'NULL'";
        }

        return isNullable
            ? $"({catalogExpression} IS NULL OR UPPER(TRIM({catalogExpression})) = 'NULL')"
            : $"{catalogExpression} IS NULL";
    }

    private static void AddSimpleStringLiteralDisplayCandidate(
        HashSet<string> candidates,
        string providerLiteral
    )
    {
        if (providerLiteral.Length < 2
            || providerLiteral[0] != '\''
            || providerLiteral[^1] != '\'')
        {
            return;
        }

        var interior = providerLiteral[1..^1];
        var unescaped = interior.Replace("''", "'", StringComparison.Ordinal);
        var escapedQuoteCount = interior.Count(static character => character == '\'');
        var unescapedQuoteCount = unescaped.Count(static character => character == '\'');

        if (unescapedQuoteCount * 2 != escapedQuoteCount)
        {
            return;
        }

        candidates.Add(unescaped);
    }

    private static void AddExpressionDefaultDisplayCandidate(
        HashSet<string> candidates,
        string providerLiteral
    )
    {
        if (providerLiteral.Contains('\'', StringComparison.Ordinal))
        {
            candidates.Add(providerLiteral.Replace("'", "\\'", StringComparison.Ordinal));
        }
    }

    private static void AddQuotedStringDefaultDisplayCandidate(
        HashSet<string> candidates,
        string value
    )
    {
        // MariaDB can expose a character default as its quoted SQL text even
        // when the provider emitted a hexadecimal literal to avoid sql_mode
        // ambiguity. Preserve exact value semantics while accepting that
        // catalog representation; raw and provider-literal forms remain
        // separate candidates.
        var escaped = value
            .Replace("\\", @"\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);

        candidates.Add($"'{escaped}'");
    }

    private string BuildBinaryDefaultMatches(
        string catalogExpression,
        byte[] value
    )
    {
        var hex = Convert.ToHexString(value);
        var textualForms = new[] { $"0X{hex}", $"X'{hex}'", }.Select(Literal);

        var encodedForms = new[] { hex, $"27{hex}27", }.Select(Literal);

        return $"(UPPER({catalogExpression}) IN ({string.Join(", ", textualForms)}) "
            + $"OR UPPER(HEX({catalogExpression})) IN ({string.Join(", ", encodedForms)}))";
    }

    private static string[] BuildDefaultSqlCandidates(
        string expression
    )
    {
        var trimmed = expression.Trim();
        var candidates = new List<string>
        {
            trimmed,
            $"({trimmed})",
        };

        const string currentTimestamp = "CURRENT_TIMESTAMP";
        if (trimmed.StartsWith(currentTimestamp, StringComparison.OrdinalIgnoreCase))
        {
            var suffix = trimmed[currentTimestamp.Length..];
            if (suffix.Length == 0
                || (suffix is ['(', _, ..]
                    && suffix[^1] == ')'
                    && suffix[1..^1]
                        .All(char.IsAsciiDigit)))
            {
                candidates.Add($"current_timestamp{suffix}");
                candidates.Add($"now{suffix}");
            }
        }

        return candidates
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddTemporalDefaultCandidates(
        HashSet<string> candidates,
        object value
    )
    {
        switch (value)
        {
            case DateOnly date:
                AddTemporalTypedCandidate(
                    candidates,
                    "DATE",
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                AddTemporalTypedCandidate(candidates, "TIME", time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                AddTemporalTypedCandidate(
                    candidates,
                    "TIME",
                    time.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
                break;
            case DateTime dateTime:
                AddQuotedCandidate(candidates, dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                AddQuotedCandidate(
                    candidates,
                    dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
                break;
            case TimeSpan timeSpan:
                AddTemporalTypedCandidate(
                    candidates,
                    "TIME",
                    timeSpan.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
                AddTemporalTypedCandidate(
                    candidates,
                    "TIME",
                    timeSpan.ToString(@"hh\:mm\:ss\.ffffff", CultureInfo.InvariantCulture));
                break;
        }
    }

    private static void AddTemporalTypedCandidate(
        HashSet<string> candidates,
        string keyword,
        string value
    )
    {
        AddQuotedCandidate(candidates, value);
        candidates.Add($"{keyword} '{value}'");
        candidates.Add($"{keyword}'{value}'");
        candidates.Add($"{keyword}\\'{value}\\'");
        candidates.Add($"_utf8mb4'{value}'");
        candidates.Add($"_utf8mb4\\'{value}\\'");
    }

    private static void AddQuotedCandidate(
        HashSet<string> candidates,
        string value
    )
    {
        candidates.Add(value);
        candidates.Add($"'{value.Replace("'", "''", StringComparison.Ordinal)}'");
    }

    private string BuildComputedMatches(
        ExpectedColumnDefinition definition,
        bool isMariaDb
    )
    {
        if (definition.ComputedColumnSql is null
            && definition.ComputedExpression is null)
        {
            return "(c.GENERATION_EXPRESSION IS NULL OR c.GENERATION_EXPRESSION = '')";
        }

        var expression = definition.ComputedColumnSql ?? _expressionRenderer.Render(definition.ComputedExpression!);
        var candidates = new[] { expression, $"({expression})" }
            .Concat(
                MySqlExpressionCanonicalizer.BuildCatalogDisplayCandidates(
                    expression,
                    includeMySqlEncodedDisplay: !isMariaDb))
            .Distinct(StringComparer.Ordinal)
            .Select(Literal);

        var storage = definition.IsStored == true ? "STORED GENERATED" : "VIRTUAL GENERATED";

        return $"c.GENERATION_EXPRESSION IN ({string.Join(", ", candidates)}) "
            + $"AND LOCATE({Literal(storage)}, c.EXTRA) > 0";
    }

    private bool CanMap(
        ExpectedColumnDefinition definition
    )
    {
        try
        {
            return _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                definition.IsUnicode,
                definition.MaxLength,
                definition.IsRowVersion,
                definition.IsFixedLength,
                definition.Precision,
                definition.Scale) is not null;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or InvalidOperationException
                                              or NotSupportedException)
        {
            return false;
        }
    }

    private bool CanRepresentLiteralDefault(
        ExpectedColumnDefinition definition,
        MySqlServerVersion serverVersion
    )
    {
        if (definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.Literal)
        {
            return true;
        }

        var value = definition.DefaultValue.GetLiteralValue();
        if (value is null)
        {
            return true;
        }

        var mapping = _typeMappingSource.FindMapping(
            definition.ClrType,
            definition.StoreType,
            keyOrIndex: false,
            definition.IsUnicode,
            definition.MaxLength,
            definition.IsRowVersion,
            definition.IsFixedLength,
            definition.Precision,
            definition.Scale);

        var storeType = definition.StoreType ?? mapping?.StoreType;
        if (value is Guid
            && storeType?.StartsWith("binary", StringComparison.OrdinalIgnoreCase) == true)
        {
            var version = serverVersion.Version;

            return serverVersion.IsMariaDb
                && version is { Major: 11, Minor: 8 } or { Major: 12, Minor: 3 };
        }

        return true;
    }

    private string ColumnExists(
        string table,
        string column
    ) => $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
        + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
        + $"AND c.COLUMN_NAME = {Literal(column)})";
}
