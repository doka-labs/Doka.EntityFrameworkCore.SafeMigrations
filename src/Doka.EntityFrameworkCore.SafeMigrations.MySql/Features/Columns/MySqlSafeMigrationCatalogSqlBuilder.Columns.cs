namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private const string StrictSqlModeExpression = "FIND_IN_SET('STRICT_TRANS_TABLES', @@SESSION.sql_mode) > 0 "
        + "OR FIND_IN_SET('STRICT_ALL_TABLES', @@SESSION.sql_mode) > 0";

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
                intent.Schema,
                intent.Definition,
                isMariaDb,
                includeTransitionEvidence)
            : MySqlColumnRepairTransition.None;

        var repairInvariant = repairCapability == SafeMigrationRepairCapability.Safe
            ? $"({BuildColumnRepairInvariantMatches(intent.Table, intent.Definition, isMariaDb)}) "
                + $"OR ({transition.InvariantExpression})"
            : "FALSE";

        var dataBlocked = unsafeAdd
            ? $"EXISTS (SELECT 1 FROM {Delimited(intent.Table, intent.Schema)} LIMIT 1)"
            : "FALSE";
        // WHY: Classification and repair need the same NULL proof. Runtime
        // materializes it once after physical eligibility is established;
        // columns that are already NOT NULL never need a row scan.
        var nullabilityProbe = repairCapability == SafeMigrationRepairCapability.Safe
            && !intent.Definition.IsNullable
                ? new MySqlSafeMigrationNullabilityDataProbe(
                    ColumnWithInvariantExists(intent.Table, intent.Definition.Name, "c.IS_NULLABLE = 'YES'"),
                    repairInvariant,
                    $"EXISTS (SELECT 1 FROM {Delimited(intent.Table, intent.Schema)} WHERE "
                    + $"{Delimited(intent.Definition.Name)} IS NULL LIMIT 1)")
                : null;

        var hasNull = nullabilityProbe is not null
            ? MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder
            : "FALSE";

        var classificationRepairInvariant = nullabilityProbe is not null
            ? MySqlSafeMigrationRuntimePlan.ColumnRepairInvariantPlaceholder
            : repairInvariant;

        var dataTransitionBlocked = transition.DataBlockedExpression;
        var repairDataBlocked = $"({hasNull}) OR ({dataTransitionBlocked})";

        var repairPrecondition = repairCapability == SafeMigrationRepairCapability.Safe
            ? $"({classificationRepairInvariant}) AND NOT ({repairDataBlocked}) "
                + $"AND ({transition.ExecutionInvariantExpression})"
            : "FALSE";

        // WHY: An ordinary-string tail prevents interpolation fusion and
        // materializes another complete state string for every operation.
        var plan = Plan(
            $"CASE WHEN NOT {tableExists} THEN 'prerequisite_missing' "
            + $"WHEN NOT {columnExists} AND {dataBlocked} THEN 'data_blocked' "
            + $"WHEN NOT {columnExists} THEN 'missing' "
            + $"WHEN {matching} THEN 'matching' "
            + $"WHEN ({classificationRepairInvariant}) AND "
            + $"({repairDataBlocked}) THEN 'data_blocked' ELSE 'different' END",
            matching,
            repairCapability,
            repairPrecondition) with
        {
            ClassificationCodeExpression = transition.HasDataProbe
                ? $"CASE WHEN {dataTransitionBlocked} THEN 'varchar_narrowing_value_too_long' ELSE NULL END"
                : null,
            RepairOperationalImpact = transition.OperationalImpact,
            DataProbe = transition.DataProbe,
            NullabilityDataProbe = nullabilityProbe,
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
        bool isMariaDb,
        bool repairRequested,
        bool includeAnalysisEvidence,
        bool includeTransitionEvidence,
        bool requiresNullFreeBackfill
    )
    {
        var columnExists = ColumnExists(intent.Table, intent.Definition.Name);
        var matching = BuildColumnMatches(intent.Table, intent.Definition, isMariaDb);
        var repairCapability = intent.OldDefinition is not null
            && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(intent.OldDefinition, intent.Definition)
                ? SafeMigrationRepairCapability.Safe
                : SafeMigrationRepairCapability.None;

        if (repairRequested
            && repairCapability == SafeMigrationRepairCapability.None
            && intent.OldDefinition is { ClrType: var sourceClrType } oldDefinition
            && (sourceClrType == typeof(string) || sourceClrType == typeof(bool) || sourceClrType == typeof(bool?))
            && intent.Definition.ClrType == sourceClrType
            && MySqlSafeMigrationColumnMetadata.CanSafelyConverge(oldDefinition)
            && MySqlSafeMigrationColumnMetadata.CanSafelyConverge(intent.Definition))
        {
            var transition = BuildColumnRepairTransition(
                intent.Table, intent.Schema, intent.Definition, isMariaDb, includeTransitionEvidence);

            if (transition != MySqlColumnRepairTransition.None)
            {
                return BuildAlterColumnTransitionPlan(
                    intent, isMariaDb, matching, transition, includeAnalysisEvidence,
                    requiresNullFreeBackfill);
            }
        }

        var repairPrecondition = repairCapability == SafeMigrationRepairCapability.Safe
            ? BuildColumnMatches(intent.Table, intent.OldDefinition!, isMariaDb)
            : "FALSE";

        var hasProvablyNonNullBackfill =
            SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(intent.Definition.DefaultValue);

        var nullBlocked =
            repairCapability == SafeMigrationRepairCapability.Safe
            && intent.OldDefinition!.IsNullable
            && !intent.Definition.IsNullable
            && !hasProvablyNonNullBackfill
                ? $"({repairPrecondition}) AND EXISTS (SELECT 1 FROM {Delimited(intent.Table, intent.Schema)} WHERE "
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
                && !intent.Definition.IsNullable
                && !hasProvablyNonNullBackfill,
            DiagnosticEvidenceExpression = includeAnalysisEvidence
                ? BuildColumnDiagnosticEvidence(intent.Table, intent.Definition, isMariaDb)
                : null,
        };
    }

    /// <summary>Combines the shared type proof with an explicit alteration's exact source contract.</summary>
    private MySqlSafeMigrationRuntimePlan BuildAlterColumnTransitionPlan(
        AlterColumnIntent intent,
        bool isMariaDb,
        string matching,
        MySqlColumnRepairTransition transition,
        bool includeAnalysisEvidence,
        bool expectedBackfillConstraint
    )
    {
        var sourceMatches = $"({BuildColumnMatches(intent.Table, intent.OldDefinition!, isMariaDb)}) "
            + $"AND ({BuildAlterColumnBackfillInvariant(intent, isMariaDb)}) "
            + $"AND ({BuildAlterColumnInlineRowFits(intent)})";

        var dataProbe = transition.DataProbe;
        if (dataProbe?.TransitionInvariantExpression is { } transitionInvariant)
        {
            // WHY: An Ensure proof identifies the current shape, whereas Alter
            // also promises an exact previous definition. Qualify before a row
            // scan, not only in the final repair predicate.
            dataProbe = dataProbe with
            {
                TransitionInvariantExpression = $"({sourceMatches}) AND ({transitionInvariant})",
            };
        }

        var repairInvariant = $"({sourceMatches}) AND ({transition.InvariantExpression})";
        var nullableColumn = ColumnWithInvariantExists(intent.Table, intent.Definition.Name, "c.IS_NULLABLE = 'YES'");
        if (SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(intent.Definition.DefaultValue))
        {
            // WHY: A fitting replacement can still collide with a unique key
            // or violate a CHECK. Those contracts permit this transition only
            // when no row needs the UPDATE; unconstrained backfills avoid it.
            nullableColumn = expectedBackfillConstraint
                ? nullableColumn
                : $"({nullableColumn}) AND ({BuildConstrainedBackfillExists(intent)})";
        }

        var nullabilityProbe = intent.OldDefinition!.IsNullable
            && !intent.Definition.IsNullable
                ? new MySqlSafeMigrationNullabilityDataProbe(
                    nullableColumn,
                    repairInvariant,
                    $"EXISTS (SELECT 1 FROM {Delimited(intent.Table, intent.Schema)} WHERE "
                    + $"{Delimited(intent.Definition.Name)} IS NULL LIMIT 1)")
                : null;

        var classificationInvariant = nullabilityProbe is null
            ? repairInvariant
            : MySqlSafeMigrationRuntimePlan.ColumnRepairInvariantPlaceholder;

        var nullBlocked = nullabilityProbe is null
            ? "FALSE"
            : MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder;

        var dataBlocked = $"({nullBlocked}) OR ({transition.DataBlockedExpression})";

        return Plan(
            $"CASE WHEN NOT {BaseTableExists(intent.Table)} "
            + $"OR NOT {ColumnExists(intent.Table, intent.Definition.Name)} THEN 'different' "
            + $"WHEN {matching} THEN 'matching' "
            + $"WHEN ({classificationInvariant}) AND ({dataBlocked}) THEN 'data_blocked' ELSE 'different' END",
            matching,
            SafeMigrationRepairCapability.Safe,
            $"({classificationInvariant}) AND NOT ({dataBlocked}) AND ({transition.ExecutionInvariantExpression})") with
        {
            DataProbe = dataProbe,
            NullabilityDataProbe = nullabilityProbe,
            MayRequireNullabilityDataProof = nullabilityProbe is not null,
            StateEvaluationGuardExpression = ColumnExists(intent.Table, intent.Definition.Name),
            StateEvaluationGuardFailureExpression = "'different'",
            ClassificationCodeExpression = transition.HasDataProbe
                ? $"CASE WHEN {transition.DataBlockedExpression} THEN 'varchar_narrowing_value_too_long' ELSE NULL END"
                : null,
            RepairOperationalImpact = transition.OperationalImpact,
            DiagnosticEvidenceExpression = includeAnalysisEvidence
                ? BuildColumnDiagnosticEvidence(intent.Table, intent.Definition, isMariaDb)
                : null,
        };
    }

    /// <summary>Finds live dependencies that require a NULL-free proof before a replacement update.</summary>
    private string BuildConstrainedBackfillExists(AlterColumnIntent intent) =>
        "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS backfill_key "
        + "WHERE backfill_key.TABLE_SCHEMA = DATABASE() "
        + $"AND backfill_key.TABLE_NAME = {Literal(intent.Table)} "
        + $"AND backfill_key.COLUMN_NAME = {Literal(intent.Definition.Name)} AND backfill_key.NON_UNIQUE = 0) "
        + "OR EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS backfill_check "
        + "WHERE backfill_check.CONSTRAINT_SCHEMA = DATABASE() "
        + $"AND backfill_check.TABLE_NAME = {Literal(intent.Table)} AND backfill_check.CONSTRAINT_TYPE = 'CHECK')";

    /// <summary>Checks a conservative InnoDB inline-record budget for newly admitted type alterations.</summary>
    private string BuildAlterColumnInlineRowFits(AlterColumnIntent intent)
    {
        var targetType = ResolveStoreType(intent.Definition);
        var targetColumn = $"inline_column.COLUMN_NAME = {Literal(intent.Definition.Name)}";
        var noPrimaryKey = "NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS inline_primary "
            + "WHERE inline_primary.TABLE_SCHEMA = inline_column.TABLE_SCHEMA "
            + "AND inline_primary.TABLE_NAME = inline_column.TABLE_NAME AND inline_primary.INDEX_NAME = 'PRIMARY')";

        var nonNullableUnique = "NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS inline_part "
            + "LEFT JOIN INFORMATION_SCHEMA.COLUMNS inline_part_column "
            + "ON inline_part_column.TABLE_SCHEMA = inline_part.TABLE_SCHEMA "
            + "AND inline_part_column.TABLE_NAME = inline_part.TABLE_NAME "
            + "AND inline_part_column.COLUMN_NAME = inline_part.COLUMN_NAME "
            + "WHERE inline_part.TABLE_SCHEMA = inline_key.TABLE_SCHEMA "
            + "AND inline_part.TABLE_NAME = inline_key.TABLE_NAME AND inline_part.INDEX_NAME = inline_key.INDEX_NAME "
            + "AND (inline_part_column.COLUMN_NAME IS NULL OR CASE WHEN inline_part.COLUMN_NAME = "
            + $"{Literal(intent.Definition.Name)} THEN {(intent.Definition.IsNullable ? "TRUE" : "FALSE")} "
            + "ELSE inline_part_column.IS_NULLABLE <> 'NO' END))";

        var primaryColumn = "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS inline_key "
            + "WHERE inline_key.TABLE_SCHEMA = inline_column.TABLE_SCHEMA "
            + "AND inline_key.TABLE_NAME = inline_column.TABLE_NAME "
            + "AND inline_key.COLUMN_NAME = inline_column.COLUMN_NAME AND (inline_key.INDEX_NAME = 'PRIMARY' "
            + $"OR (inline_key.NON_UNIQUE = 0 AND {noPrimaryKey} AND {nonNullableUnique})))";

        var dynamicRow = "UPPER(inline_table.ROW_FORMAT) = 'DYNAMIC'";
        var octets = "COALESCE(inline_column.CHARACTER_OCTET_LENGTH, 65536)";
        var textWidth = $"CASE WHEN {dynamicRow} THEN 40 ELSE 788 END";
        var variableWidth = $"CASE WHEN {primaryColumn} THEN {octets} + CASE WHEN {octets} <= 255 THEN 1 ELSE 2 END "
            + $"WHEN {dynamicRow} THEN LEAST({octets}, 255) "
            + $"WHEN {octets} <= 768 THEN {octets} ELSE 788 END";
        var fixedWidth = $"CASE WHEN NOT ({primaryColumn}) AND {dynamicRow} AND {octets} >= 768 "
            + $"THEN LEAST({octets}, 767) ELSE {octets} END";

        string targetWidth;
        if (TryParseVarcharLength(targetType, out var targetLength))
        {
            var targetOctets = $"({targetLength.ToString(CultureInfo.InvariantCulture)} "
                + "* COALESCE(inline_charset.MAXLEN, 65536))";

            targetWidth = $"CASE WHEN {primaryColumn} THEN {targetOctets} "
                + $"+ CASE WHEN {targetOctets} <= 255 THEN 1 ELSE 2 END "
                + $"WHEN {dynamicRow} THEN LEAST({targetOctets}, 255) "
                + $"WHEN {targetOctets} <= 768 THEN {targetOctets} ELSE 788 END";
        }
        else if (TryGetTextCapacity(targetType, out _))
        {
            targetWidth = $"CASE WHEN {primaryColumn} THEN 4294967296 ELSE {textWidth} END";
        }
        else
        {
            targetWidth = IsBooleanTinyInt(targetType, intent.Definition.ClrType) ? "1" : "65536";
        }

        // WHY: The SQL-layer 65,535-byte limit does not prove that an InnoDB
        // record fits its page. Include directory/null overhead, retain full
        // primary-key values, and fail closed for unknown storage layouts.
        var width = $"CASE WHEN {targetColumn} THEN ({targetWidth}) "
            + "WHEN LOWER(inline_column.DATA_TYPE) IN ('varchar', 'varbinary') "
            + $"THEN ({variableWidth}) "
            + $"WHEN LOWER(inline_column.DATA_TYPE) IN ('char', 'binary') THEN ({fixedWidth}) "
            + "WHEN LOWER(inline_column.DATA_TYPE) IN ('tinyblob', 'tinytext', 'blob', 'text', "
            + "'mediumblob', 'mediumtext', 'longblob', 'longtext', 'json', 'geometry') "
            + $"THEN CASE WHEN {primaryColumn} THEN {octets} ELSE {textWidth} END "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'tinyint' THEN 1 "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'smallint' THEN 2 "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'mediumint' THEN 3 "
            + "WHEN LOWER(inline_column.DATA_TYPE) IN ('int', 'integer') THEN 4 "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'float' "
            + "THEN CASE WHEN inline_column.NUMERIC_PRECISION <= 24 THEN 4 ELSE 8 END "
            + "WHEN LOWER(inline_column.DATA_TYPE) IN ('bigint', 'double') THEN 8 "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'decimal' THEN 36 "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'bit' "
            + "THEN CEIL(COALESCE(inline_column.NUMERIC_PRECISION, 64) / 8) "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'date' THEN 3 "
            + "WHEN LOWER(inline_column.DATA_TYPE) IN ('time', 'datetime', 'timestamp') THEN 8 "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'year' THEN 1 "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'enum' THEN 2 "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'set' THEN 8 ELSE 65536 END";

        return "@@innodb_page_size IN (4096, 8192, 16384, 32768, 65536) AND COALESCE((SELECT "
            + $"SUM(({width}) + 2) + CEIL(SUM(CASE WHEN {targetColumn} "
            + $"THEN {(intent.Definition.IsNullable ? 1 : 0)} "
            + "WHEN inline_column.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END) / 8) "
            + "FROM INFORMATION_SCHEMA.COLUMNS inline_column "
            + "JOIN INFORMATION_SCHEMA.TABLES inline_table ON inline_table.TABLE_SCHEMA = inline_column.TABLE_SCHEMA "
            + "AND inline_table.TABLE_NAME = inline_column.TABLE_NAME "
            + "LEFT JOIN INFORMATION_SCHEMA.CHARACTER_SETS inline_charset "
            + "ON inline_charset.CHARACTER_SET_NAME = inline_column.CHARACTER_SET_NAME "
            + "WHERE inline_column.TABLE_SCHEMA = DATABASE() "
            + $"AND inline_column.TABLE_NAME = {Literal(intent.Table)} AND UPPER(inline_table.ENGINE) = 'INNODB' "
            + "AND UPPER(inline_table.ROW_FORMAT) IN ('DYNAMIC', 'COMPACT', 'REDUNDANT')), 65536) "
            + "<= LEAST(@@innodb_page_size / 2, 16384) - 256";
    }

    /// <summary>Proves a replacement value fits both sides of the provider's UPDATE-before-MODIFY sequence.</summary>
    /// <param name="intent">The alteration with an exact source definition and its requested target.</param>
    /// <param name="isMariaDb">Whether MariaDB expression-default serialization rules apply.</param>
    /// <returns>The SQL predicate proving default identity and any backfill in both column domains.</returns>
    private string BuildAlterColumnBackfillInvariant(
        AlterColumnIntent intent,
        bool isMariaDb
    )
    {
        if (!HasComparableAlterLiteralDefault(intent.OldDefinition!)
            || !HasComparableAlterLiteralDefault(intent.Definition))
        {
            return "FALSE";
        }

        var sourceType = ResolveStoreType(intent.OldDefinition!);
        var targetType = ResolveStoreType(intent.Definition);
        if (isMariaDb
            && RequiresTextExpressionControlCharacters(intent.Definition, targetType))
        {
            return "FALSE";
        }

        var expressionMode = !isMariaDb && RequiresQuotedAlterExpressionDefault(intent.Definition, targetType)
            ? "FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) = 0"
            : "TRUE";

        if (!intent.OldDefinition!.IsNullable
            || intent.Definition.IsNullable
            || !SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(intent.Definition.DefaultValue))
        {
            return expressionMode;
        }

        if (intent.Definition.ClrType == typeof(bool)
            || intent.Definition.ClrType == typeof(bool?))
        {
            return CanRepresentAlterColumnBackfill(
                intent.OldDefinition, intent.Definition, sourceType, targetType, null,
                strictMode: true, supportsQuotedExpressionDefaults: true,
                supportsTextExpressionControlCharacters: true)
                ? StrictSqlModeExpression
                : "FALSE";
        }

        string[] characterSets = ["ascii", "latin1", "utf8", "utf8mb3", "utf8mb4", "ucs2", "utf16", "utf16le", "utf32"];
        var permitted = characterSets.Where(characterSet => CanRepresentAlterColumnBackfill(
                intent.OldDefinition, intent.Definition, sourceType, targetType, characterSet,
                strictMode: true, supportsQuotedExpressionDefaults: true,
                supportsTextExpressionControlCharacters: true))
            .Select(Literal)
            .ToArray();

        if (permitted.Length == 0)
        {
            return "FALSE";
        }

        // WHY: Exact source/target collation checks ensure both domains retain
        // this charset. Unknown encodings cannot authorize a backfill merely
        // because strict mode would eventually reject a lossy UPDATE.
        var charsetMatches = ColumnWithInvariantExists(intent.Table, intent.Definition.Name,
            $"c.CHARACTER_SET_NAME IN ({string.Join(", ", permitted)})");

        return $"({charsetMatches}) AND ({StrictSqlModeExpression}) AND ({expressionMode})";
    }

    /// <summary>Shares literal, character-set, size, and strict-mode backfill proof with ordered projection.</summary>
    /// <param name="source">The exact definition receiving any UPDATE before type alteration.</param>
    /// <param name="target">The final definition that must retain the replacement value.</param>
    /// <param name="sourceType">The resolved source store type.</param>
    /// <param name="targetType">The resolved target store type.</param>
    /// <param name="characterSet">The invariant character set, or null when unproven.</param>
    /// <param name="strictMode">Whether conversion rejects unrepresentable values.</param>
    /// <param name="supportsQuotedExpressionDefaults">Whether the captured engine and SQL mode preserve quoted
    /// expression-default payloads.</param>
    /// <param name="supportsTextExpressionControlCharacters">Whether TEXT expression defaults preserve
    /// backslashes and control characters in their literal payloads.</param>
    /// <returns>Whether literal identity and any replacement are safe in both source and target domains.</returns>
    internal static bool CanRepresentAlterColumnBackfill(
        ExpectedColumnDefinition source,
        ExpectedColumnDefinition target,
        string sourceType,
        string targetType,
        string? characterSet,
        bool strictMode,
        bool supportsQuotedExpressionDefaults,
        bool supportsTextExpressionControlCharacters
    )
    {
        if (!HasComparableAlterLiteralDefault(source)
            || !HasComparableAlterLiteralDefault(target)
            || (!supportsQuotedExpressionDefaults
                && RequiresQuotedAlterExpressionDefault(target, targetType))
            || (!supportsTextExpressionControlCharacters
                && RequiresTextExpressionControlCharacters(target, targetType)))
        {
            return false;
        }

        if (!source.IsNullable
            || target.IsNullable
            || !SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(target.DefaultValue))
        {
            return true;
        }

        if (!strictMode)
        {
            return false;
        }

        var literal = target.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal
            ? target.DefaultValue.GetLiteralValue()
            : (target.DefaultValue.StructuredExpression as SafeMigrationSqlLiteralExpression)?.Value;

        if (literal is bool)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(sourceType.Trim(), "bit(1)")
                && StringComparer.OrdinalIgnoreCase.Equals(targetType.Trim(), "tinyint(1)");
        }

        if (literal is not string value)
        {
            return false;
        }

        var characters = 0;
        var ascii = true;
        var basicPlane = true;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            characters++;
            ascii &= rune.IsAscii;
            basicPlane &= rune.IsBmp;
            remaining = remaining[consumed..];
        }

        // WHY: ASCII is the proven common subset for latin1. Unicode encodings
        // have exact byte counts; unsupported repertoires remain unproven.
        var bytes = characterSet?.ToLowerInvariant() switch
        {
            "ascii" or "latin1" when ascii => (long)characters,
            "utf8" or "utf8mb3" when basicPlane => Encoding.UTF8.GetByteCount(value),
            "utf8mb4" => Encoding.UTF8.GetByteCount(value),
            "ucs2" when basicPlane => (long)characters * 2,
            "utf16" or "utf16le" => (long)value.Length * 2,
            "utf32" => (long)characters * 4,
            _ => -1,
        };

        return bytes >= 0 && StringBackfillFits(sourceType, characters, bytes)
            && StringBackfillFits(targetType, characters, bytes);
    }

    /// <summary>Identifies target defaults emitted as expressions whose payload contains a quote.</summary>
    /// <param name="target">The definition emitted by the provider's MODIFY command.</param>
    /// <param name="targetType">The resolved target store type.</param>
    /// <returns>Whether the target needs the engine's quoted-expression SQL-mode capability.</returns>
    private static bool RequiresQuotedAlterExpressionDefault(
        ExpectedColumnDefinition target,
        string targetType
    )
    {
        // WHY: MySQL reparses expression-default text during ALTER. Under
        // NO_BACKSLASH_ESCAPES a quote in that payload can fail after UPDATE.
        // Plain VARCHAR defaults and source-only defaults do not take that path.
        var value = target.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal
            ? target.DefaultValue.GetLiteralValue()
            : (target.DefaultValue.StructuredExpression as SafeMigrationSqlLiteralExpression)?.Value;

        return value is string text && text.Contains('\'', StringComparison.Ordinal)
            && (target.DefaultValue.Kind == SafeMigrationDefaultValueKind.Sql || TryGetTextCapacity(targetType, out _));
    }

    /// <summary>
    /// Identifies TEXT literal payloads that require control-character-preserving expression defaults.
    /// </summary>
    /// <param name="target">The definition emitted by the provider's MODIFY command.</param>
    /// <param name="targetType">The resolved target store type.</param>
    /// <returns>Whether a TEXT default contains a backslash or control character.</returns>
    private static bool RequiresTextExpressionControlCharacters(
        ExpectedColumnDefinition target,
        string targetType
    )
    {
        var value = target.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal
            ? target.DefaultValue.GetLiteralValue()
            : (target.DefaultValue.StructuredExpression as SafeMigrationSqlLiteralExpression)?.Value;

        // WHY: A backfill can retain these bytes while the engine serializes
        // the TEXT expression default into a different value. Row preservation
        // alone therefore cannot establish the resulting default contract.
        return TryGetTextCapacity(targetType, out _) && value is string text
            && text.Any(static character => character == '\\' || char.IsControl(character));
    }

    /// <summary>Rejects literal identities that the metadata comparison cannot represent exactly.</summary>
    private static bool HasComparableAlterLiteralDefault(ExpectedColumnDefinition definition)
    {
        // A typed structured literal emits CAST; its input value does not
        // prove either the resulting default identity or replacement domain.
        if (definition.DefaultValue.StructuredExpression is SafeMigrationSqlLiteralExpression { StoreType: not null })
        {
            return false;
        }

        var literal = definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal
            ? definition.DefaultValue.GetLiteralValue()
            : (definition.DefaultValue.StructuredExpression as SafeMigrationSqlLiteralExpression)?.Value;

        // WHY: The UTF-8 three-byte catalog surface can expose supplementary
        // defaults as question marks even while the stored default is intact.
        // Neither accepting that lossy spelling nor mutating before a failed
        // postcondition establishes the exact source/target contract.
        return literal is not string value || !value.Any(char.IsSurrogate);
    }

    /// <summary>Checks character-limited VARCHAR and byte-limited TEXT against one literal shape.</summary>
    private static bool StringBackfillFits(
        string storeType,
        int characters,
        long bytes
    ) =>
        TryParseVarcharLength(storeType, out var length)
            ? characters <= length
            : TryGetTextCapacity(storeType, out var capacity) && (ulong)bytes <= capacity;

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
                    isMariaDb,
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

    /// <summary>Verifies every expected column of a table definition through one catalog probe.</summary>
    /// <remarks>
    /// WHY: The per-column classifier repeated the same INFORMATION_SCHEMA.COLUMNS lookup once per
    /// column, so a wide table produced one correlated lookup per column in a single statement.
    /// This form reads the catalog once per table and counts how many columns at the expected
    /// positions match, while every facet predicate stays verbatim and server-side.
    ///
    /// The expected set deliberately does not become a derived table joined against the catalog.
    /// MariaDB cannot use the table name as an information_schema lookup value once it sits in a
    /// join condition, so it opens every table definition in the schema for each such join. In a
    /// paired run on MariaDB 11.8, analyzing 100 expected tables took 2,218 ms in that form against
    /// 167 ms in this one, and 3,611 ms against 132 ms with 1,000 further tables in the schema,
    /// while MySQL 8.4 stayed within one percent of itself either way. Counting matches keeps the
    /// name a plain lookup value.
    ///
    /// Counting is equivalent to testing each expected column separately: ORDINAL_POSITION is
    /// unique per table, so at most one row can satisfy each arm. A missing column, a mismatching
    /// facet and a facet predicate that evaluates to NULL all leave the count short, the last one
    /// because a WHERE clause keeps only rows whose predicate is true.
    /// </remarks>
    /// <param name="table">The table whose columns are verified.</param>
    /// <param name="definition">The expected table definition.</param>
    /// <param name="isMariaDb">Whether the connected engine is MariaDB.</param>
    /// <returns>A predicate that is true when every expected column matches at its own position.</returns>
    private string BuildAllColumnsMatch(
        string table,
        ExpectedTableDefinition definition,
        bool isMariaDb
    )
    {
        var facets = new List<(List<string> Conditions, CollationContract Collation)>(definition.Columns.Count);
        foreach (var column in definition.Columns)
        {
            var conditions = new List<string>
            {
                $"c.COLUMN_NAME = {Literal(column.Name)}",
                $"c.IS_NULLABLE = {(column.IsNullable ? "'YES'" : "'NO'")}",
                $"COALESCE(c.COLUMN_COMMENT, '') = {Literal(column.Comment ?? string.Empty)}",
            };

            conditions.AddRange(BuildColumnStructuralConditions(table, column, isMariaDb, out var collation));
            facets.Add((conditions, collation));
        }

        // WHY: A column that inherits the table default compares against an uncorrelated subquery
        // over INFORMATION_SCHEMA.TABLES which is the same for every column, so repeating it per
        // column made the catalog work grow with the column count. Carrying it once per table
        // instead measured 353 ms against 906 ms on MySQL 8.4 and 131 ms against 172 ms on MariaDB
        // 11.8, over 50 tables of 16 columns, as the best of three samples per arm. It may only
        // leave the arms when no column expects a collation of its own, because the shared clause
        // applies to every row the count sees.
        var sharedCollation = facets.Count > 0
            && facets.TrueForAll(static facet =>
                facet.Collation.ExpectationKind == CollationExpectationKind.InheritedTableDefault)
            ? facets[0].Collation.MatchExpression
            : null;

        var arms = new StringBuilder(definition.Columns.Count * 256);
        for (var index = 0; index < facets.Count; index++)
        {
            var (conditions, collation) = facets[index];
            if (sharedCollation is null)
            {
                conditions.Add(collation.MatchExpression);
            }

            arms.Append(" WHEN ").Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .Append(" THEN (").AppendJoin(" AND ", conditions).Append(')');
        }

        var columnCount = definition.Columns.Count.ToString(CultureInfo.InvariantCulture);

        // WHY: The ordinal bound is implied by the arms, which yield NULL beyond the expected
        // count, but it lets the engine drop surplus columns before evaluating any facet.
        return "(SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c WHERE c.TABLE_SCHEMA = DATABASE() "
            + $"AND c.TABLE_NAME = {Literal(table)} AND c.ORDINAL_POSITION <= {columnCount} "
            + (sharedCollation is null ? string.Empty : $"AND {sharedCollation} ")
            + $"AND (CASE c.ORDINAL_POSITION{arms} END)) = {columnCount}";
    }

    /// <summary>Builds the column facets whose SQL shape depends on the expected column itself.</summary>
    /// <param name="table">The owning table.</param>
    /// <param name="definition">The expected column.</param>
    /// <param name="isMariaDb">Whether the connected engine is MariaDB.</param>
    /// <param name="collation">The collation contract, returned apart so the caller can share it.</param>
    /// <returns>The structural predicates for that column, without the collation contract.</returns>
    private List<string> BuildColumnStructuralConditions(
        string table,
        ExpectedColumnDefinition definition,
        bool isMariaDb,
        out CollationContract collation
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

        collation = BuildCollationContract(table, definition.Collation, mariaDbJsonAlias);

        return
        [
            BuildStoreTypeMatches(storeType, isMariaDb),
            BuildComputedMatches(definition, isMariaDb),
            BuildValueGenerationMatches(definition, temporalRowVersion),
            BuildDefaultMatches(
                "c.COLUMN_DEFAULT", definition.DefaultValue, definition.IsNullable, mapping, isMariaDb,
                temporalRowVersion),
        ];
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
            isMariaDb,
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
        string? schema,
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
                + "AND (LOWER(narrowing_column.DATA_TYPE) <> 'varchar' "
                + $"OR narrowing_column.CHARACTER_MAXIMUM_LENGTH > {targetLength}))";

            var strictMode = $"NOT ({narrowing}) OR {StrictSqlModeExpression}";

            return new MySqlColumnRepairTransition(
                MySqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
                MySqlSafeMigrationRuntimePlan.DataProbePlaceholder,
                strictMode,
                new MySqlSafeMigrationDataProbe(
                    table,
                    definition.Name,
                    targetLength,
                    Delimited(table, schema),
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

        if (TryGetTextCapacity(storeType, out var targetCapacity)
            && definition.ClrType == typeof(string))
        {
            return new MySqlColumnRepairTransition(
                BuildTextTransitionColumnExists(
                    table,
                    definition,
                    targetCapacity,
                    isMariaDb),
                "FALSE",
                "TRUE",
                DataProbe: null,
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
        // WHY: The fixed predicate body is approximately 4.9K characters and
        // references seven table plus six column literals. The bounded
        // headroom keeps ordinary identifiers on one backing buffer;
        // StringBuilder can still grow for heavily escaped or long names.
        var initialCapacity = checked(
            5800
            + (Literal(table).Length * 7)
            + (Literal(definition.Name).Length * 6));
        var builder = new StringBuilder(initialCapacity);

        builder
            .Append("EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c ")
            .Append("WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND c.COLUMN_NAME = ")
            .Append(Literal(definition.Name))
            .Append(" AND (LOWER(c.DATA_TYPE) IN ")
            .Append("('varchar', 'tinytext', 'text', 'mediumtext', 'longtext') ")
            .Append("AND NOT (LOWER(c.DATA_TYPE) = 'varchar' AND c.CHARACTER_MAXIMUM_LENGTH = ")
            .Append(targetLength.ToString(CultureInfo.InvariantCulture))
            .Append(") AND ");

        AppendSupportedStringRepairTable(builder, table);
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

    private string BuildTextTransitionColumnExists(
        string table,
        ExpectedColumnDefinition definition,
        ulong targetCapacity,
        bool isMariaDb
    )
    {
        var initialCapacity = checked(
            3900
            + (Literal(table).Length * 6)
            + (Literal(definition.Name).Length * 5));
        var builder = new StringBuilder(initialCapacity);

        builder
            .Append("EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c ")
            .Append("WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND c.COLUMN_NAME = ")
            .Append(Literal(definition.Name))
            .Append(" AND (LOWER(c.DATA_TYPE) IN ")
            .Append("('varchar', 'tinytext', 'text', 'mediumtext', 'longtext') ")
            .Append("AND LOWER(c.DATA_TYPE) <> ")
            .Append(Literal(ResolveStoreType(definition).Trim().ToLowerInvariant()))
            .Append(" AND COALESCE(c.CHARACTER_OCTET_LENGTH, 4294967296) <= ")
            .Append(targetCapacity.ToString(CultureInfo.InvariantCulture))
            .Append(" AND ");

        AppendSupportedStringRepairTable(builder, table);
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

        AppendTargetDeclaredRowSizeFits(builder, table, definition.Name, targetLength: null);
        builder.Append(" AND ");
        AppendDependentIndexesRemainRepresentable(builder, table, definition.Name, targetLength: null);
        builder.Append(" AND ");
        AppendNoForeignKeyDependency(builder, table, definition.Name);
        builder.Append("))");

        return builder.ToString();
    }

    private void AppendSupportedStringRepairTable(
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
        int? targetLength
    )
    {
        // WHY: MySQL rejects a declared row above 65,535 bytes before
        // InnoDB can move long values off-page. Unknown families consume the
        // complete budget so incomplete catalog knowledge fails closed.
        builder
            .Append("COALESCE((SELECT SUM(CASE WHEN row_column.COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(" THEN ");

        if (targetLength is int varcharLength)
        {
            var targetLengthSql = varcharLength.ToString(CultureInfo.InvariantCulture);

            builder
                .Append('(')
                .Append(targetLengthSql)
                .Append(" * COALESCE(character_set.MAXLEN, 1)) + CASE WHEN ")
                .Append(targetLengthSql)
                .Append(" * COALESCE(character_set.MAXLEN, 1) <= 255 THEN 1 ELSE 2 END ");
        }
        else
        {
            builder.Append("12 ");
        }

        builder
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
            .Append("WHEN LOWER(row_column.DATA_TYPE) IN ('int', 'integer') THEN 4 ")
            .Append("WHEN LOWER(row_column.DATA_TYPE) = 'float' ")
            .Append("THEN CASE WHEN row_column.NUMERIC_PRECISION <= 24 THEN 4 ELSE 8 END ")
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
        int? targetLength
    )
    {
        builder
            .Append("NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS ti ")
            .Append("WHERE ti.TABLE_SCHEMA = DATABASE() AND ti.TABLE_NAME = ")
            .Append(Literal(table))
            .Append(" AND ti.COLUMN_NAME = ")
            .Append(Literal(column));

        if (targetLength is null)
        {
            // MySQL and MariaDB require a prefix for non-FULLTEXT indexes over
            // a TEXT column. Existing prefix and FULLTEXT indexes retain their
            // physical key shape across this transition.
            builder
                .Append(" AND ti.SUB_PART IS NULL ")
                .Append("AND UPPER(COALESCE(ti.INDEX_TYPE, '')) <> 'FULLTEXT')");

            return;
        }

        builder
            .Append(" AND ti.SUB_PART IS NULL ")
            .Append("AND UPPER(COALESCE(ti.INDEX_TYPE, '')) <> 'FULLTEXT' ")
            .Append("AND (SELECT COALESCE(SUM(CASE ")
            .Append("WHEN ip.COLUMN_NAME IS NULL THEN 3073 ")
            .Append("WHEN ip.COLUMN_NAME = ")
            .Append(Literal(column))
            .Append(" AND ip.SUB_PART IS NULL THEN ")
            .Append(targetLength.Value.ToString(CultureInfo.InvariantCulture))
            .Append(" * COALESCE(cs.MAXLEN, 1) ")
            .Append("WHEN ip.SUB_PART IS NOT NULL ")
            .Append("THEN ip.SUB_PART * CASE WHEN ic.COLLATION_NAME IS NULL ")
            .Append("THEN 1 ELSE COALESCE(cs.MAXLEN, 1) END ")
            .Append("WHEN ic.CHARACTER_OCTET_LENGTH IS NOT NULL ")
            .Append("THEN ic.CHARACTER_OCTET_LENGTH ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'tinyint' THEN 1 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'smallint' THEN 2 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'mediumint' THEN 3 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) IN ('int', 'integer') THEN 4 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'float' ")
            .Append("THEN CASE WHEN ic.NUMERIC_PRECISION <= 24 THEN 4 ELSE 8 END ")
            .Append("WHEN LOWER(ic.DATA_TYPE) ")
            .Append("IN ('bigint', 'double', 'datetime', 'timestamp') THEN 8 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'date' THEN 3 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'time' THEN 6 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'year' THEN 1 ")
            .Append("WHEN LOWER(ic.DATA_TYPE) = 'decimal' ")
            .Append("THEN CEIL(COALESCE(ic.NUMERIC_PRECISION, 65) / 2) ")
            .Append("ELSE 3073 END), 0) FROM INFORMATION_SCHEMA.STATISTICS ip ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.COLUMNS ic ")
            .Append("ON ic.TABLE_SCHEMA = ip.TABLE_SCHEMA ")
            .Append("AND ic.TABLE_NAME = ip.TABLE_NAME ")
            .Append("AND ic.COLUMN_NAME = ip.COLUMN_NAME ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.COLLATIONS co ")
            .Append("ON co.COLLATION_NAME = ic.COLLATION_NAME ")
            .Append("LEFT JOIN INFORMATION_SCHEMA.CHARACTER_SETS cs ")
            .Append("ON cs.CHARACTER_SET_NAME = co.CHARACTER_SET_NAME ")
            .Append("WHERE ip.TABLE_SCHEMA = ti.TABLE_SCHEMA ")
            .Append("AND ip.TABLE_NAME = ti.TABLE_NAME ")
            .Append("AND ip.INDEX_NAME = ti.INDEX_NAME) > ")
            .Append("COALESCE((SELECT CASE WHEN UPPER(dt.ENGINE) = 'INNODB' THEN ")
            .Append(BuildMaximumIndexKeyWidth("dt"))
            .Append(" ELSE 0 END FROM INFORMATION_SCHEMA.TABLES dt ")
            .Append("WHERE dt.TABLE_SCHEMA = ti.TABLE_SCHEMA ")
            .Append("AND dt.TABLE_NAME = ti.TABLE_NAME LIMIT 1), 0))");
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

    /// <summary>Parses the variable-character length shared by runtime and ordered projection.</summary>
    internal static bool TryParseVarcharLength(
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

    /// <summary>Resolves a text family's byte capacity for runtime and ordered projection proofs.</summary>
    internal static bool TryGetTextCapacity(
        string storeType,
        out ulong capacity
    )
    {
        capacity = storeType.Trim().ToLowerInvariant() switch
        {
            "tinytext" => byte.MaxValue,
            "text" => ushort.MaxValue,
            "mediumtext" => 16777215UL,
            "longtext" => uint.MaxValue,
            _ => 0,
        };

        return capacity > 0;
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

        var type = normalized.AsSpan();
        var separator = type.IndexOf(' ');
        var name = separator < 0 ? type : type[..separator];
        var canonicalType = name switch
        {
            "tinyint" => "tinyint",
            "smallint" => "smallint",
            "mediumint" => "mediumint",
            "int" or "integer" => "int",
            "bigint" => "bigint",
            _ => null,
        };

        if (canonicalType is null)
        {
            return $"LOWER(c.COLUMN_TYPE) = {Literal(normalized)}";
        }

        // WHY: MariaDB display widths are ignored for integer families, but the
        // literal-space modifier grammar and unsigned semantics must remain exact.
        var expectedUnsigned = false;
        foreach (var range in type.Split(' '))
        {
            if (type[range].SequenceEqual("unsigned"))
            {
                expectedUnsigned = true;
                break;
            }
        }

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
        bool isMariaDb,
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

            return isNullable ? NullDefaultMatches() : $"{catalogExpression} IS NULL";
        }

        var structuredString = mapping.ClrType == typeof(string)
            && expected.StructuredExpression is SafeMigrationSqlLiteralExpression { StoreType: null } literal
            ? literal
            : null;

        if (expected.Kind == SafeMigrationDefaultValueKind.Sql
            && structuredString?.Value is not string)
        {
            var expression = expected.SqlExpression ?? _expressionRenderer.Render(expected.StructuredExpression!);
            var sqlCandidates = BuildDefaultSqlCandidates(expression)
                .Select(Literal);

            return $"{catalogExpression} IN ({string.Join(", ", sqlCandidates)})";
        }

        var value = structuredString?.Value ?? expected.GetLiteralValue();
        if (value is null)
        {
            return NullDefaultMatches();
        }

        if (value is byte[] bytes)
        {
            return BuildBinaryDefaultMatches(catalogExpression, bytes);
        }

        if (value is string text)
        {
            return isMariaDb
                ? BuildMariaDbStringDefaultMatches(catalogExpression, text)
                : BuildMySqlStringDefaultMatches(catalogExpression, text);
        }

        var providerLiteral = mapping.GenerateSqlLiteral(value);
        var candidates = new HashSet<string>(StringComparer.Ordinal) { providerLiteral };
        AddSimpleStringLiteralDisplayCandidate(candidates, providerLiteral);
        AddExpressionDefaultDisplayCandidate(candidates, providerLiteral);

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

        // WHY: MySQL exposes DEFAULT (NULL) as the exact text NULL plus
        // DEFAULT_GENERATED. Neither a plain string 'NULL' nor its serialized
        // expression spelling may impersonate that SQL NULL identity.
        string NullDefaultMatches() => isMariaDb
            ? $"({catalogExpression} IS NULL OR UPPER({catalogExpression}) = 'NULL')"
            : $"({catalogExpression} IS NULL OR (CAST({catalogExpression} AS BINARY) = 'NULL' "
                + "AND LOCATE('DEFAULT_GENERATED', UPPER(COALESCE(c.EXTRA, ''))) > 0))";
    }

    /// <summary>Separates MySQL raw literal values from exact serialized expression defaults.</summary>
    /// <param name="catalogExpression">The owning column's default metadata expression.</param>
    /// <param name="value">The exact expected string value.</param>
    /// <returns>A binary comparison qualified by the catalog's expression-default marker.</returns>
    private string BuildMySqlStringDefaultMatches(
        string catalogExpression,
        string value
    )
    {
        // WHY: MySQL serializes an expression literal, then prints its binary
        // UTF-8 bytes as individual code points for INFORMATION_SCHEMA. This
        // spelling differs from both the value and MariaDB's quoted display.
        // Keeping DEFAULT_GENERATED separate prevents a plain value containing
        // quotes from impersonating a serialized expression for another value.
        var expression = "_utf8mb4'" + EscapeMySqlDefaultDisplay(value) + "'";
        var display = EscapeMySqlDefaultDisplay(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(expression)));
        var generated = "LOCATE('DEFAULT_GENERATED', UPPER(COALESCE(c.EXTRA, ''))) > 0";

        return $"CASE WHEN {generated} THEN CAST({catalogExpression} AS BINARY) = {Literal(display)} "
            + $"ELSE CAST({catalogExpression} AS BINARY) = {Literal(value)} END";
    }

    /// <summary>Escapes one serialization layer without normalizing case, Unicode, or quoted payloads.</summary>
    /// <param name="value">The literal value or binary expression display being serialized.</param>
    /// <returns>The exact MySQL display spelling for the input characters.</returns>
    private static string EscapeMySqlDefaultDisplay(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\0", "\\0", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\u001A", "\\Z", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal);

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

    /// <summary>Separates MariaDB TEXT expression display from ordinary quoted character-default display.</summary>
    /// <param name="catalogExpression">The owning column's default metadata expression.</param>
    /// <param name="value">The exact expected string value.</param>
    /// <returns>A byte-exact comparison selected by the live column's storage family.</returns>
    private string BuildMariaDbStringDefaultMatches(
        string catalogExpression,
        string value
    )
    {
        // WHY: TEXT defaults remain expression items and use backslash quote
        // escaping. Ordinary character defaults use doubled quotes instead.
        // Select by actual storage rather than accepting ambiguous raw values.
        var ordinaryDisplay = Literal(BuildMariaDbStringDefaultDisplay(value));
        var expressionDisplay = Literal("'" + EscapeMySqlDefaultDisplay(value)
            .Replace("\b", @"\b", StringComparison.Ordinal)
            .Replace("\t", @"\t", StringComparison.Ordinal) + "'");

        return $"CAST({catalogExpression} AS BINARY) = CASE "
            + "WHEN LOWER(c.DATA_TYPE) IN ('tinytext', 'text', 'mediumtext', 'longtext') "
            + $"THEN {expressionDisplay} ELSE {ordinaryDisplay} END";
    }

    /// <summary>Produces MariaDB's exact quoted character-default display without raw-value alternatives.</summary>
    /// <param name="value">The expected string value, including any quote or backslash payload.</param>
    /// <returns>The canonical quoted catalog representation.</returns>
    private static string BuildMariaDbStringDefaultDisplay(
        string value
    )
    {
        // MariaDB can expose a character default as its quoted SQL text even
        // when the provider emitted a hexadecimal literal to avoid sql_mode
        // ambiguity. A raw-value alternative would let quote characters in
        // the expected payload impersonate a different quoted catalog value.
        var escaped = value
            .Replace("\\", @"\\", StringComparison.Ordinal)
            .Replace("\0", @"\0", StringComparison.Ordinal)
            .Replace("\n", @"\n", StringComparison.Ordinal)
            .Replace("\r", @"\r", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);

        return $"'{escaped}'";
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
