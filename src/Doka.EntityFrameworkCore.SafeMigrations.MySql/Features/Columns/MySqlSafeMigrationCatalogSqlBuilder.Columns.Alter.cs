namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private MySqlSafeMigrationRuntimePlan BuildAlterColumn(
        AlterColumnIntent intent,
        bool isMariaDb,
        bool repairRequested,
        bool includeAnalysisEvidence,
        bool includeTransitionEvidence,
        bool requiresNullFreeBackfill,
        string? nativeNullFreeBackfillCondition
    )
    {
        var columnExists = ColumnExists(intent.Table, intent.Definition.Name);
        var matching = BuildColumnMatches(intent.Table, intent.Definition, isMariaDb);
        var repairCapability = intent.OldDefinition is not null
            && SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(intent.OldDefinition, intent.Definition)
                ? SafeMigrationRepairCapability.Safe
                : SafeMigrationRepairCapability.None;

        if (repairRequested
            && intent.OldDefinition is { } integerSource
            && MySqlSignedIntegerWidening.IsSafe(integerSource, intent.Definition))
        {
            var physical = new StringBuilder();
            AppendSupportedStringRepairTable(physical, intent.Table);
            physical.Append(" AND ");
            AppendTargetDeclaredRowSizeFits(
                physical,
                intent.Table,
                intent.Definition.Name,
                null,
                MySqlSignedIntegerWidening.Width(intent.Definition));
            physical.Append(" AND ");
            AppendDependentIndexesRemainRepresentable(
                physical,
                intent.Table,
                intent.Definition.Name,
                null,
                MySqlSignedIntegerWidening.Width(intent.Definition));
            physical
                .Append(" AND ")
                .Append(NoForeignKeyDependency(intent.Table, intent.Definition.Name));
            physical
                .Append(" AND ")
                .Append(StrictSqlModeExpression);
            physical
                .Append(" AND ")
                .Append(ColumnWithInvariantExists(intent.Table, intent.Definition.Name, OrdinaryColumnExtraMatches()));

            // WHY: Injective value conversion still enlarges physical rows and
            // composite keys; exact source, fresh FK, strict mode and cumulative
            // native budgets remain mandatory even for already populated tables.
            return BuildAlterColumnTransitionPlan(
                intent,
                isMariaDb,
                matching,
                new MySqlColumnRepairTransition(
                    physical.ToString(),
                    "FALSE",
                    StrictSqlModeExpression,
                    null,
                    SafeMigrationOperationalImpact.TableRewritePossible),
                includeAnalysisEvidence,
                requiresNullFreeBackfill,
                nativeNullFreeBackfillCondition);
        }

        if (repairRequested
            && repairCapability == SafeMigrationRepairCapability.None
            && intent.OldDefinition is { ClrType: var sourceClrType } oldDefinition
            && (sourceClrType == typeof(string) || sourceClrType == typeof(bool) || sourceClrType == typeof(bool?))
            && intent.Definition.ClrType == sourceClrType
            && MySqlSafeMigrationColumnMetadata.CanSafelyConverge(oldDefinition)
            && MySqlSafeMigrationColumnMetadata.CanSafelyConverge(intent.Definition))
        {
            var transition = BuildColumnRepairTransition(
                intent.Table,
                intent.Schema,
                intent.Definition,
                isMariaDb,
                includeTransitionEvidence);

            if (transition != MySqlColumnRepairTransition.None)
            {
                return BuildAlterColumnTransitionPlan(
                    intent,
                    isMariaDb,
                    matching,
                    transition,
                    includeAnalysisEvidence,
                    requiresNullFreeBackfill,
                    nativeNullFreeBackfillCondition);
            }
        }

        var repairPrecondition = repairCapability == SafeMigrationRepairCapability.Safe
            ? BuildColumnMatches(intent.Table, intent.OldDefinition!, isMariaDb)
            : "FALSE";

        if (repairCapability == SafeMigrationRepairCapability.Safe
            && RequiresExplicitTimestampDefaults(intent.Definition))
        {
            repairPrecondition = $"({repairPrecondition}) AND @@SESSION.explicit_defaults_for_timestamp = 1";
        }

        if (_renderValuesInline
            && repairCapability == SafeMigrationRepairCapability.Safe
            && !intent.OldDefinition!.IsNullable
            && intent.Definition.IsNullable)
        {
            // WHY: Analysis captures intrinsic source eligibility independently
            // of an ordered PRIMARY removal. MySQL cannot relax a still-live
            // PRIMARY column, so execution retains this fresh dependency guard.
            repairPrecondition = $"({repairPrecondition}) AND "
                + BuildNoPrimaryKeyColumn(intent.Table, intent.Definition.Name);
        }

        if (_renderValuesInline
            && repairCapability == SafeMigrationRepairCapability.Safe
            && intent.OldDefinition!.IsNullable
            && !intent.Definition.IsNullable)
        {
            // WHY: Captured source and row proofs survive an accepted FK
            // removal. Runtime must independently reject any still-live
            // SET NULL action before a tightening or its explicit UPDATE.
            repairPrecondition = $"({repairPrecondition}) AND "
                + $"({BuildNoOutgoingSetNullForeignKey(intent.Table, intent.Definition.Name)})";
        }

        var hasProvablyNonNullBackfill =
            SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(intent.Definition.DefaultValue);

        var requiresNullFreeTightening = repairCapability == SafeMigrationRepairCapability.Safe
            && intent.OldDefinition!.IsNullable
            && !intent.Definition.IsNullable
            && !hasProvablyNonNullBackfill;

        if (repairCapability == SafeMigrationRepairCapability.Safe
            && intent.OldDefinition!.IsNullable
            && !intent.Definition.IsNullable)
        {
            // WHY: A fresh NULL proof is not a lock against later writers.
            // Even after an approved UPDATE, a raced NULL can be coerced to
            // an implicit value different from the authored backfill.
            repairPrecondition = $"({repairPrecondition}) AND ({StrictSqlModeExpression})";
        }

        var nullabilityProbe = requiresNullFreeTightening
            ? new MySqlSafeMigrationNullabilityDataProbe(
                ColumnWithInvariantExists(intent.Table, intent.Definition.Name, "c.IS_NULLABLE = 'YES'"),
                repairPrecondition,
                $"EXISTS (SELECT 1 FROM {Delimited(intent.Table, intent.Schema)} WHERE "
                + $"{Delimited(intent.Definition.Name)} IS NULL LIMIT 1)")
            : null;

        var classificationInvariant = nullabilityProbe is null
            ? repairPrecondition
            : MySqlSafeMigrationRuntimePlan.ColumnRepairInvariantPlaceholder;

        var nullBlocked = nullabilityProbe is not null
            ? MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder
            : "FALSE";

        return Plan(
                $"CASE WHEN NOT {BaseTableExists(intent.Table)} OR NOT {columnExists} THEN 'different' "
                + $"WHEN {matching} THEN 'matching' "
                + $"WHEN ({classificationInvariant}) AND ({nullBlocked}) THEN 'data_blocked' ELSE 'different' END",
                matching,
                repairCapability,
                $"({classificationInvariant}) AND NOT ({nullBlocked})") with
            {
                NullabilityDataProbe = nullabilityProbe,
                MayRequireNullabilityDataProof = nullabilityProbe is not null,
                DiagnosticEvidenceExpression = includeAnalysisEvidence
                    ? BuildColumnDiagnosticEvidence(intent.Table, intent.Definition, isMariaDb)
                    : null,
            };
    }

    /// <summary>Rejects live PRIMARY membership before making an existing column nullable.</summary>
    /// <param name="table">The exact local table identity.</param>
    /// <param name="column">The exact local column identity.</param>
    /// <returns>The fresh catalog predicate excluding PRIMARY membership for this column.</returns>
    private string BuildNoPrimaryKeyColumn(
        string table,
        string column
    ) => "NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS nullable_primary "
        + "WHERE nullable_primary.TABLE_SCHEMA = DATABASE() "
        + $"AND nullable_primary.TABLE_NAME = {Literal(table)} "
        + $"AND nullable_primary.COLUMN_NAME = {Literal(column)} "
        + "AND nullable_primary.INDEX_NAME = 'PRIMARY')";

    /// <summary>Rejects outgoing actions that require an existing local column to remain nullable.</summary>
    /// <param name="table">The exact local table identity.</param>
    /// <param name="column">The exact local column identity.</param>
    /// <returns>The fresh catalog predicate excluding only DELETE or UPDATE SET NULL actions.</returns>
    private string BuildNoOutgoingSetNullForeignKey(
        string table,
        string column
    ) => "NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS nullable_fk "
        + "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE nullable_fk_column "
        + "ON nullable_fk_column.CONSTRAINT_SCHEMA = nullable_fk.CONSTRAINT_SCHEMA "
        + "AND nullable_fk_column.CONSTRAINT_NAME = nullable_fk.CONSTRAINT_NAME "
        + "AND nullable_fk_column.TABLE_SCHEMA = nullable_fk.CONSTRAINT_SCHEMA "
        + "AND nullable_fk_column.TABLE_NAME = nullable_fk.TABLE_NAME "
        + "WHERE nullable_fk.CONSTRAINT_SCHEMA = DATABASE() "
        + $"AND nullable_fk.TABLE_NAME = {Literal(table)} "
        + $"AND nullable_fk_column.COLUMN_NAME = {Literal(column)} "
        + "AND (nullable_fk.DELETE_RULE = 'SET NULL' OR nullable_fk.UPDATE_RULE = 'SET NULL'))";

    /// <summary>Combines the shared type proof with an explicit alteration's exact source contract.</summary>
    private MySqlSafeMigrationRuntimePlan BuildAlterColumnTransitionPlan(
        AlterColumnIntent intent,
        bool isMariaDb,
        string matching,
        MySqlColumnRepairTransition transition,
        bool includeAnalysisEvidence,
        bool expectedBackfillConstraint,
        string? nativeNullFreeBackfillCondition
    )
    {
        var sourceMatches = $"({BuildColumnMatches(intent.Table, intent.OldDefinition!, isMariaDb)}) "
            + $"AND ({BuildAlterColumnBackfillInvariant(intent, isMariaDb)}) "
            + $"AND ({BuildAlterColumnInlineRowFits(intent)})";

        if (intent.OldDefinition!.IsNullable
            && !intent.Definition.IsNullable)
        {
            // WHY: An approved UPDATE cannot cover a NULL inserted afterward.
            // Strict conversion must reject that row rather than invent data.
            sourceMatches = $"({sourceMatches}) AND ({StrictSqlModeExpression})";
        }

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
            if (!expectedBackfillConstraint)
            {
                var constrained = BuildConstrainedBackfillExists(intent);
                if (nativeNullFreeBackfillCondition is not null)
                {
                    // A differently spelled declaration is a hazard only
                    // under the actual folded native owner profile.
                    constrained = $"({constrained}) OR ({nativeNullFreeBackfillCondition})";
                }

                nullableColumn = $"({nullableColumn}) AND ({constrained})";
            }
        }

        var nullabilityProbe = intent.OldDefinition!.IsNullable && !intent.Definition.IsNullable
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
                $"({classificationInvariant}) AND NOT ({dataBlocked}) AND ({transition.ExecutionInvariantExpression})")
            with
            {
                DataProbe = dataProbe,
                NullabilityDataProbe = nullabilityProbe,
                MayRequireNullabilityDataProof = nullabilityProbe is not null,
                StateEvaluationGuardExpression = ColumnExists(intent.Table, intent.Definition.Name),
                StateEvaluationGuardFailureExpression = "'different'",
                ClassificationCodeExpression =
                transition.HasDataProbe
                    ? $"CASE WHEN {transition.DataBlockedExpression} THEN 'varchar_narrowing_value_too_long' ELSE NULL END"
                    : null,
                RepairOperationalImpact = transition.OperationalImpact,
                DiagnosticEvidenceExpression = includeAnalysisEvidence
                    ? BuildColumnDiagnosticEvidence(intent.Table, intent.Definition, isMariaDb)
                    : null,
            };
    }

    /// <summary>Finds live dependencies that require a NULL-free proof before a replacement update.</summary>
    private string BuildConstrainedBackfillExists(
        AlterColumnIntent intent
    ) => "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS backfill_key "
        + "WHERE backfill_key.TABLE_SCHEMA = DATABASE() "
        + $"AND backfill_key.TABLE_NAME = {Literal(intent.Table)} "
        + $"AND backfill_key.COLUMN_NAME = {Literal(intent.Definition.Name)} AND backfill_key.NON_UNIQUE = 0) "
        + "OR EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS backfill_check "
        + "WHERE backfill_check.CONSTRAINT_SCHEMA = DATABASE() "
        + $"AND backfill_check.TABLE_NAME = {Literal(intent.Table)} AND backfill_check.CONSTRAINT_TYPE = 'CHECK')";

    /// <summary>Checks a conservative InnoDB inline-record budget for newly admitted type alterations.</summary>
    private string BuildAlterColumnInlineRowFits(
        AlterColumnIntent intent
    )
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
            var integerBytes = MySqlSignedIntegerWidening.Width(intent.Definition);
            targetWidth = integerBytes > 0
                ? integerBytes.ToString(CultureInfo.InvariantCulture)
                : IsBooleanTinyInt(targetType, intent.Definition.ClrType)
                    ? "1"
                    : "65536";
        }

        // WHY: The SQL-layer 65,535-byte limit does not prove that an InnoDB
        // record fits its page. Include directory/null overhead, retain full
        // primary-key values, and fail closed for unknown storage layouts.
        // Native UUID always remains an inline sixteen-byte scalar.
        var width = $"CASE WHEN {targetColumn} THEN ({targetWidth}) "
            + "WHEN LOWER(inline_column.DATA_TYPE) IN ('varchar', 'varbinary') "
            + $"THEN ({variableWidth}) "
            + $"WHEN LOWER(inline_column.DATA_TYPE) IN ('char', 'binary') THEN ({fixedWidth}) "
            + "WHEN LOWER(inline_column.DATA_TYPE) IN ('tinyblob', 'tinytext', 'blob', 'text', "
            + "'mediumblob', 'mediumtext', 'longblob', 'longtext', 'json', 'geometry') "
            + $"THEN CASE WHEN {primaryColumn} THEN {octets} ELSE {textWidth} END "
            + "WHEN LOWER(inline_column.DATA_TYPE) = 'uuid' THEN 16 "
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
        if (isMariaDb && RequiresTextExpressionControlCharacters(intent.Definition, targetType))
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
                intent.OldDefinition,
                intent.Definition,
                sourceType,
                targetType,
                null,
                strictMode: true,
                supportsQuotedExpressionDefaults: true,
                supportsTextExpressionControlCharacters: true)
                ? StrictSqlModeExpression
                : "FALSE";
        }

        string[]? permitted = null;
        var permittedCount = 0;
        foreach (var characterSet in s_backfillCharacterSets)
        {
            if (CanRepresentAlterColumnBackfill(
                    intent.OldDefinition,
                    intent.Definition,
                    sourceType,
                    targetType,
                    characterSet,
                    strictMode: true,
                    supportsQuotedExpressionDefaults: true,
                    supportsTextExpressionControlCharacters: true))
            {
                // WHY: Register each selected literal immediately, in the old lazy Where/Select
                // order. Delaying registration would change operation-local parameter ordinals.
                permitted ??= new string[s_backfillCharacterSets.Length];
                permitted[permittedCount++] = Literal(characterSet);
            }
        }

        if (permitted is null)
        {
            return "FALSE";
        }

        // WHY: Exact source/target collation checks ensure both domains retain
        // this charset. Unknown encodings cannot authorize a backfill merely
        // because strict mode would eventually reject a lossy UPDATE.
        var charsetMatches = ColumnWithInvariantExists(
            intent.Table,
            intent.Definition.Name,
            $"c.CHARACTER_SET_NAME IN ({string.Join(", ", permitted, 0, permittedCount)})");

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
            || (!supportsQuotedExpressionDefaults && RequiresQuotedAlterExpressionDefault(target, targetType))
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
            "ascii" or "latin1" when ascii => characters,
            "utf8" or "utf8mb3" when basicPlane => Encoding.UTF8.GetByteCount(value),
            "utf8mb4" => Encoding.UTF8.GetByteCount(value),
            "ucs2" when basicPlane => (long)characters * 2,
            "utf16" or "utf16le" => (long)value.Length * 2,
            "utf32" => (long)characters * 4,
            _ => -1,
        };

        return bytes >= 0
            && StringBackfillFits(sourceType, characters, bytes)
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

        return value is string text
            && text.Contains('\'', StringComparison.Ordinal)
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
        return TryGetTextCapacity(targetType, out _)
            && value is string text
            && text.Any(static character => character == '\\' || char.IsControl(character));
    }

    /// <summary>Rejects literal identities that the metadata comparison cannot represent exactly.</summary>
    private static bool HasComparableAlterLiteralDefault(
        ExpectedColumnDefinition definition
    )
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
    ) => TryParseVarcharLength(storeType, out var length)
        ? characters <= length
        : TryGetTextCapacity(storeType, out var capacity) && (ulong)bytes <= capacity;
}
