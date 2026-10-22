namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private const string StrictSqlModeExpression = "FIND_IN_SET('STRICT_TRANS_TABLES', @@SESSION.sql_mode) > 0 "
        + "OR FIND_IN_SET('STRICT_ALL_TABLES', @@SESSION.sql_mode) > 0";

    private static readonly SearchValues<char> s_modeDependentDefaultCharacters =
        SearchValues.Create("\\'\0\n\r\u001A");

    private MySqlSafeMigrationRuntimePlan BuildEnsureColumn(
        EnsureColumnIntent intent,
        bool isMariaDb,
        bool repairRequested,
        bool includeAnalysisEvidence,
        bool includeTransitionEvidence,
        bool supportsRequiredColumnAddCheck
    )
    {
        var unsafeAdd = !SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition);
        var nativeOwnerQualified = RequiresNativeRequiredColumnAddOwnerQualification(intent.Definition);
        var tableExists = nativeOwnerQualified
            ? BuildRequiredColumnAddBaseTableExists(intent.Table)
            : BaseTableExists(intent.Table);

        var columnExists = nativeOwnerQualified
            ? BuildRequiredColumnAddColumnExists(intent.Table, intent.Definition.Name)
            : ColumnExists(intent.Table, intent.Definition.Name);

        var matching = BuildColumnMatches(
            intent.Table,
            intent.Definition,
            isMariaDb,
            requireNativeOwnerIdentity: nativeOwnerQualified);

        var repairCapability = repairRequested && MySqlSafeMigrationColumnMetadata.CanSafelyConverge(intent.Definition)
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
            ? $"({BuildColumnRepairInvariantMatches(intent.Table, intent.Definition, isMariaDb,
                requireNativeOwnerIdentity: nativeOwnerQualified)}) "
            + $"OR ({transition.InvariantExpression})"
            : "FALSE";

        if (repairCapability == SafeMigrationRepairCapability.Safe
            && RequiresExplicitTimestampDefaults(intent.Definition))
        {
            // WHY: Native TIMESTAMP MODIFY can replace NULL with current time
            // when this setting is disabled, even with an approved default.
            repairInvariant = $"({repairInvariant}) AND @@SESSION.explicit_defaults_for_timestamp = 1";
        }

        if (_renderValuesInline && repairCapability == SafeMigrationRepairCapability.Safe)
        {
            // WHY: Ensure and model convergence share this repair path.
            // Fresh dependency feasibility qualifies both ordinary and type
            // repairs, never an exact match or a guarded missing-column add.
            var dependencyInvariant = intent.Definition.IsNullable
                ? BuildNoPrimaryKeyColumn(intent.Table, intent.Definition.Name)
                : BuildNoOutgoingSetNullForeignKey(intent.Table, intent.Definition.Name);

            repairInvariant = $"({repairInvariant}) AND ({dependencyInvariant})";
        }

        var nullableColumn = repairCapability == SafeMigrationRepairCapability.Safe && !intent.Definition.IsNullable
            ? nativeOwnerQualified
                ? BuildRequiredColumnAddColumnExists(intent.Table, intent.Definition.Name, "c.IS_NULLABLE = 'YES'")
                : ColumnWithInvariantExists(intent.Table, intent.Definition.Name, "c.IS_NULLABLE = 'YES'")
            : null;

        if (nullableColumn is not null)
        {
            // WHY: This repair emits MODIFY without a backfill UPDATE. Strict
            // mode rejects a NULL inserted after the proof instead of coercing
            // it. Already-required columns need no nullability conversion.
            repairInvariant = $"({repairInvariant}) AND " + $"(NOT ({nullableColumn}) OR ({StrictSqlModeExpression}))";
        }

        var dataBlocked = unsafeAdd
            ? $"EXISTS (SELECT 1 FROM {Delimited(intent.Table, intent.Schema)} LIMIT 1)"
            : "FALSE";

        // WHY: Classification and repair need the same NULL proof. Runtime
        // materializes it once after physical eligibility is established;
        // columns that are already NOT NULL never need a row scan.
        var nullabilityProbe = nullableColumn is not null
            ? new MySqlSafeMigrationNullabilityDataProbe(
                nullableColumn,
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
        var plan =
            Plan(
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
                PrerequisiteExpression = tableExists,
                ClassificationCodeExpression = transition.HasDataProbe
                    ? $"CASE WHEN {dataTransitionBlocked} THEN 'varchar_narrowing_value_too_long' ELSE NULL END"
                    : null,
                RepairOperationalImpact = transition.OperationalImpact,
                DataProbe = transition.DataProbe,
                NullabilityDataProbe = nullabilityProbe,
                MayRequireNullabilityDataProof = repairCapability == SafeMigrationRepairCapability.Safe
                    && !intent.Definition.IsNullable,
                DiagnosticEvidenceExpression = includeAnalysisEvidence
                    ? BuildColumnDiagnosticEvidence(intent.Table, intent.Definition, isMariaDb)
                    : null,
            };

        // The data probe for nullability tightening can mention the target
        // column only after the catalog proves that the column exists. Missing
        // remains an applicable state rather than a missing prerequisite.
        if (repairCapability == SafeMigrationRepairCapability.Safe
            && (!intent.Definition.IsNullable || transition.HasDataProbe))
        {
            plan = plan with
            {
                StateEvaluationGuardExpression = columnExists,
                StateEvaluationGuardFailureExpression = unsafeAdd
                    ? $"CASE WHEN {dataBlocked} THEN 'data_blocked' ELSE 'missing' END"
                    : "'missing'",
            };
        }

        if (RequiresRequiredSqlDefaultAddQualification(intent.Definition))
        {
            return QualifyRequiredSqlDefaultColumnAdd(intent, plan);
        }

        var qualified = unsafeAdd
            ? ApplyRequiredColumnAddProtocol(intent, isMariaDb, supportsRequiredColumnAddCheck, plan)
            : plan;

        qualified = RequiresMariaDbGeneratedNonNullProof(intent.Definition, isMariaDb)
            ? QualifyMariaDbGeneratedColumnAdd(intent, qualified)
            : qualified;

        return intent.Definition.ComputedExpression is not null
            ? QualifyGeneratedColumnDependencies(intent, qualified)
            : qualified;
    }

    /// <summary>Prevents implicit values from a required SQL default while retaining existing-column paths.</summary>
    /// <param name="intent">The immutable owner and SQL-default target.</param>
    /// <param name="plan">The ordinary matching and repair plan.</param>
    /// <returns>The plan whose missing state requires transactional strict native conversion.</returns>
    private MySqlSafeMigrationRuntimePlan QualifyRequiredSqlDefaultColumnAdd(
        EnsureColumnIntent intent,
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        var tableExists = BuildRequiredColumnAddBaseTableExists(intent.Table);
        var columnExists = BuildRequiredColumnAddColumnExists(intent.Table, intent.Definition.Name);
        var qualification = "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES sql_default_table "
            + "WHERE sql_default_table.TABLE_SCHEMA = DATABASE() "
            + $"AND sql_default_table.TABLE_NAME = {Literal(intent.Table)} "
            + $"AND {BuildRequiredColumnAddOwnerMatches("sql_default_table.TABLE_SCHEMA", "DATABASE()")} "
            + $"AND {BuildRequiredColumnAddOwnerMatches("sql_default_table.TABLE_NAME", Literal(intent.Table))} "
            + "AND sql_default_table.TABLE_TYPE = 'BASE TABLE' AND UPPER(sql_default_table.ENGINE) = 'INNODB') "
            + $"AND ({StrictSqlModeExpression})"
            + (RequiresExplicitTimestampDefaults(intent.Definition)
                ? " AND @@SESSION.explicit_defaults_for_timestamp = 1"
                : string.Empty);

        var ordinaryCode = plan.ClassificationCodeExpression ?? "NULL";

        // WHY: Without repair, the ordinary state has no lazy guard failure.
        // Installing one here must retain the known-NULL populated-row proof.
        var ordinaryMissing = plan.StateEvaluationGuardFailureExpression
            ?? (SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(intent.Definition)
                ? "'missing'"
                : $"CASE WHEN EXISTS (SELECT 1 FROM {Delimited(intent.Table, intent.Schema)} LIMIT 1) "
                + "THEN 'data_blocked' ELSE 'missing' END");

        // WHY: SQL defaults can evaluate to NULL or require coercion. A strict
        // InnoDB ADD rejects those rows atomically instead of fabricating data.
        // The lazy missing branch must enforce the same qualification as state.
        return plan with
        {
            StateExpression =
            $"CASE WHEN NOT {tableExists} THEN 'prerequisite_missing' "
            + $"WHEN NOT {columnExists} AND NOT ({qualification}) THEN 'unsupported' "
            + $"ELSE ({plan.StateExpression}) END",
            StateEvaluationGuardExpression = columnExists,
            StateEvaluationGuardFailureExpression =
            $"CASE WHEN ({qualification}) THEN ({ordinaryMissing}) " + "ELSE 'unsupported' END",
            ClassificationCodeExpression = $"CASE WHEN {tableExists} AND NOT {columnExists} "
            + $"AND NOT ({qualification}) THEN 'column_required_sql_add_unavailable' "
            + $"ELSE ({ordinaryCode}) END",
        };
    }

    private MySqlSafeMigrationRuntimePlan BuildDropColumn(
        DropColumnIntent intent,
        bool isMariaDb,
        MySqlGeneratedColumnDependencyContract? generatedContract,
        MySqlGeneratedColumnDependencyContract? foldedGeneratedContract,
        bool bindFoldedGeneratedContract,
        MySqlGeneratedColumnDependencyContract? modelCandidate,
        MySqlGeneratedColumnDependencyContract? foldedModelCandidate
    )
    {
        var objectExists = TableExists(intent.Table);
        var tableExists = BaseTableExists(intent.Table);
        // WHY: MariaDB catalog collation can equate accent-distinct physical
        // columns. The same exact native lookup must govern missing-state
        // classification, dependency qualification and replay postconditions.
        var columnExists = isMariaDb
            ? BuildRequiredColumnAddColumnExists(intent.Table, intent.Name,
                BuildNativeCaseInsensitiveIdentifierMatches("c.COLUMN_NAME", Literal(intent.Name)))
            : ColumnExists(intent.Table, intent.Name);

        var plan = Plan(
            $"CASE WHEN NOT {objectExists} THEN 'missing' "
            + $"WHEN NOT {tableExists} THEN 'different' "
            + $"WHEN NOT {columnExists} THEN 'missing' ELSE 'matching' END",
            $"NOT {columnExists}");

        return QualifyGeneratedColumnDependency(
            intent.Table,
            intent.Name,
            plan,
            columnExists,
            isMariaDb,
            generatedContract,
            foldedGeneratedContract,
            bindFoldedGeneratedContract,
            isDrop: true,
            modelCandidate,
            foldedModelCandidate);
    }

    private MySqlSafeMigrationRuntimePlan BuildRenameColumn(
        RenameColumnIntent intent,
        bool isMariaDb,
        MySqlGeneratedColumnDependencyContract? generatedContract,
        MySqlGeneratedColumnDependencyContract? foldedGeneratedContract,
        bool bindFoldedGeneratedContract,
        MySqlGeneratedColumnDependencyContract? modelCandidate,
        MySqlGeneratedColumnDependencyContract? foldedModelCandidate
    )
    {
        var tableExists = BaseTableExists(intent.Table);
        var sourceExists = ColumnExists(intent.Table, intent.Name);
        var targetExists = ColumnExists(intent.Table, intent.NewName);

        var plan = Plan(
            $"CASE WHEN NOT {sourceExists} THEN 'missing' "
            + $"WHEN NOT {tableExists} THEN 'different' "
            + $"WHEN {targetExists} THEN 'different' ELSE 'matching' END",
            $"NOT {sourceExists}");

        return isMariaDb
            ? plan
            : QualifyGeneratedColumnDependency(
                intent.Table,
                intent.Name,
                plan,
                sourceExists,
                isMariaDb,
                generatedContract,
                foldedGeneratedContract,
                bindFoldedGeneratedContract,
                isDrop: false,
                modelCandidate,
                foldedModelCandidate);
    }

    private const string ColumnMatchPrefix = "EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
        + "WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = ";

    private const string ColumnMatchNamePrefix = " AND c.COLUMN_NAME = ";

    private string ColumnExists(
        string table,
        string column
    ) => $"EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
        + $"WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = {Literal(table)} "
        + $"AND c.COLUMN_NAME = {Literal(column)})";
}
