namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>
/// Generates guarded PostgreSQL SQL for the exact SafeMigrations envelope and
/// delegates every standard EF Core operation to Npgsql unchanged.
/// </summary>
public sealed partial class PostgreSqlSafeMigrationsSqlGenerator : IMigrationsSqlGenerator
{
    private readonly PostgreSqlSafeMigrationCatalogSqlBuilder _catalogSqlBuilder;
    private readonly IPostgreSqlSafeMigrationsBaselineGenerator _baselineGenerator;
    private readonly PostgreSqlSafeMigrationSqlExpressionRenderer _expressionRenderer;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly IRelationalTypeMappingSource _typeMappingSource;

    /// <summary>Initializes the composed SafeMigrations generator.</summary>
    /// <param name="baselineGenerator">The configured standard PostgreSQL migrations SQL generator.</param>
    /// <param name="typeMappingSource">The provider relational type-mapping service.</param>
    /// <param name="sqlGenerationHelper">The provider SQL identifier-generation service.</param>
    public PostgreSqlSafeMigrationsSqlGenerator(
        IPostgreSqlSafeMigrationsBaselineGenerator baselineGenerator,
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(baselineGenerator);
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _baselineGenerator = baselineGenerator;
        _sqlGenerationHelper = sqlGenerationHelper;
        _typeMappingSource = typeMappingSource;
        _expressionRenderer = new PostgreSqlSafeMigrationSqlExpressionRenderer(typeMappingSource, sqlGenerationHelper);
        _catalogSqlBuilder = new PostgreSqlSafeMigrationCatalogSqlBuilder(typeMappingSource, sqlGenerationHelper);
    }

    /// <inheritdoc />
    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        var commands = new List<MigrationCommand>();
        SafeMigrationExpectedIndexTransitions.Validate(operations);
        var expectedTableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(operations);

        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            var operation = operations[ordinal];
            if (operation is SafeMigrationDesignTimeServicesRequiredOperation)
            {
                if (ordinal != 0)
                {
                    throw new InvalidOperationException(
                        "The SafeMigrations design-time-services guard must be the first migration operation.");
                }

                // WHY: EF Core also uses the runtime model differ to create
                // migration-history DDL. The marker must reach scaffolding but
                // has no runtime database effect.
                continue;
            }

            if (operation is not SafeMigrationOperation safeOperation)
            {
                commands.AddRange(_baselineGenerator.Generate([operation], model, options));
                continue;
            }

            var tableConstraints = safeOperation.Intent is EnsureTableIntent tableIntent
                ? expectedTableConstraints.GetValueOrDefault(
                    (tableIntent.Definition.Schema, tableIntent.Definition.Table))
                : null;

            var runtimePlan = _catalogSqlBuilder.Build(
                safeOperation,
                tableConstraints,
                includeAnalysisEvidence: false,
                includeTransitionEvidence: true);
            var baseline = RenderBaseline(safeOperation, runtimePlan, model, options);
            var repairBaseline = RenderRepairBaseline(safeOperation, runtimePlan, model, options);

            // AlterColumn already carries a reviewed old definition and uses
            // its baseline for both apply and repair. EnsureColumn requires the
            // separately synthesized repair operation for existing drift.
            var effectiveRepairBaseline = repairBaseline.Count > 0
                ? repairBaseline
                : safeOperation is { Policy: SafeMigrationPolicy.RepairIfSafe, Intent: AlterColumnIntent }
                && runtimePlan.RepairCapability == SafeMigrationRepairCapability.Safe
                    ? baseline
                    : [];

            if (baseline.Any(static command => command.TransactionSuppressed)
                || effectiveRepairBaseline.Any(static command => command.TransactionSuppressed))
            {
                throw new NotSupportedException(
                    "A transaction-suppressed PostgreSQL baseline cannot be guarded inside a DO block.");
            }

            var guardedSql = BuildGuardedSql(safeOperation, runtimePlan, baseline, effectiveRepairBaseline);
            var sqlOperation = new SqlOperation { Sql = guardedSql };
            commands.AddRange(_baselineGenerator.Generate([sqlOperation], model, options));
        }

        return commands.AsReadOnly();
    }

    private IReadOnlyList<MigrationCommand> RenderBaseline(
        SafeMigrationOperation operation,
        PostgreSqlSafeMigrationRuntimePlan runtimePlan,
        IModel? model,
        MigrationsSqlGenerationOptions options
    )
    {
        if (runtimePlan.IsStaticallyUnsupported)
        {
            return [];
        }

        if (operation.Intent is EnsureIndexIntent index
            && RequiresCustomIndexSql(index.Definition))
        {
            var sqlOperation = new SqlOperation { Sql = BuildCustomCreateIndexSql(index.Definition) };
            return _baselineGenerator.Generate([sqlOperation], model, options);
        }

        if (operation.Intent is ModelManagedDataIntent modelManagedData)
        {
            var sqlOperation = new SqlOperation
            {
                Sql = _catalogSqlBuilder.BuildModelManagedDataMutationSql(modelManagedData),
            };

            return _baselineGenerator.Generate([sqlOperation], model, options);
        }

        var standardOperation = SafeMigrationStandardOperationFactory.Create(
            operation.Intent,
            _expressionRenderer.Render,
            static collation => collation.Schema is null ? collation.Name : null);

        var operations = new List<MigrationOperation> { standardOperation };
        foreach (var (table, schema, definition) in QualifiedColumnCollations(operation.Intent))
        {
            operations.Add(
                new SqlOperation
                {
                    Sql = BuildQualifiedColumnCollationSql(table, schema, definition),
                });
        }

        return _baselineGenerator.Generate(operations, model, options);
    }

    private IReadOnlyList<MigrationCommand> RenderRepairBaseline(
        SafeMigrationOperation operation,
        PostgreSqlSafeMigrationRuntimePlan runtimePlan,
        IModel? model,
        MigrationsSqlGenerationOptions options
    )
    {
        if (operation.Policy != SafeMigrationPolicy.RepairIfSafe
            || runtimePlan.RepairCapability != SafeMigrationRepairCapability.Safe
            || operation.Intent is not EnsureColumnIntent intent)
        {
            return [];
        }

        var repairOperation = SafeMigrationStandardOperationFactory.CreateStoreTypeRepair(
            intent,
            "text",
            _expressionRenderer.Render,
            static collation => collation.Schema is null ? collation.Name : null,
            PostgreSqlSafeMigrationColumnMetadata.CanSafelyConverge);

        if (intent.Definition.Collation?.Schema is null)
        {
            return _baselineGenerator.Generate([repairOperation], model, options);
        }

        // WHY: ALTER TYPE without COLLATE resets PostgreSQL's collation.
        // Npgsql renders unqualified identities itself; the qualified
        // repair needs the same companion statement as an ordinary apply.
        var collationOperation = new SqlOperation
        {
            Sql = BuildQualifiedColumnCollationSql(intent.Table, intent.Schema, intent.Definition),
        };

        return _baselineGenerator.Generate([repairOperation, collationOperation], model, options);
    }

    private static IEnumerable<(string Table, string? Schema, ExpectedColumnDefinition Definition)>
        QualifiedColumnCollations(
            SafeMigrationIntent intent
        ) => intent switch
        {
            EnsureTableIntent value => value
                .Definition
                .Columns
                .Where(static definition => definition.Collation?.Schema is not null)
                .Select(definition => (value.Definition.Table, value.Definition.Schema, definition)),
            EnsureColumnIntent { Definition.Collation.Schema: not null } value =>
            [
                (value.Table, value.Schema, value.Definition)
            ],
            AlterColumnIntent { Definition.Collation.Schema: not null } value =>
            [
                (value.Table, value.Schema, value.Definition)
            ],
            _ => [],
        };

    private string BuildQualifiedColumnCollationSql(
        string table,
        string? schema,
        ExpectedColumnDefinition definition
    )
    {
        var mapping = _typeMappingSource.FindMapping(
                definition.ClrType,
                definition.StoreType,
                keyOrIndex: false,
                unicode: definition.IsUnicode,
                size: definition.MaxLength,
                rowVersion: definition.IsRowVersion,
                fixedLength: definition.IsFixedLength,
                precision: definition.Precision,
                scale: definition.Scale)
            ?? throw new NotSupportedException($"PostgreSQL has no type mapping for column '{definition.Name}'.");

        var storeType = definition.StoreType ?? mapping.StoreType;

        return "ALTER TABLE "
            + Qualified(table, schema)
            + " ALTER COLUMN "
            + _sqlGenerationHelper.DelimitIdentifier(definition.Name)
            + " TYPE "
            + storeType
            + " COLLATE "
            + Delimited(definition.Collation!)
            + ";";
    }

    private static string BuildGuardedSql(
        SafeMigrationOperation operation,
        PostgreSqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<MigrationCommand> baseline,
        IReadOnlyList<MigrationCommand> repairBaseline
    )
    {
        const string dataBlockedExpression = "doka_data_blocked";
        const string transitionEligibleExpression = "doka_transition_eligible";
        const string declarations = "\nDECLARE\n"
            + "    doka_state text;\n"
            + "    doka_action text;\n"
            + "    doka_repair_ok boolean;\n";

        // WHY: Tag selection and the guarded row statement consume the same
        // immutable SQL text. Build it once without reusing observed evidence.
        var blockedExpression = runtimePlan.DataProbe?.BuildBlockedExpression() ?? string.Empty;
        var proofTable = runtimePlan.DataProbe?.QualifiedTable ?? runtimePlan.NullabilityDataProbe?.QualifiedTable;
        var evaluationIndentation = proofTable is null ? "    " : "        ";
        var evaluationBodyIndentation = evaluationIndentation + "    ";
        var classificationIndentation = proofTable is null
            ? evaluationBodyIndentation
            : evaluationBodyIndentation + "    ";

        var tag = SelectDollarTag(
            baseline,
            repairBaseline,
            runtimePlan.PrerequisiteExpression,
            runtimePlan.StateEvaluationGuardExpression,
            runtimePlan.StateEvaluationGuardFailureExpression ?? string.Empty,
            runtimePlan.StateExpression,
            runtimePlan.RepairPrecondition,
            runtimePlan.DataProbe?.TransitionInvariantExpression ?? string.Empty,
            runtimePlan.DataProbe?.NarrowingExpression ?? string.Empty,
            blockedExpression,
            runtimePlan.DataProbe?.QualifiedTable ?? string.Empty,
            runtimePlan.NullabilityDataProbe?.NotNullContractExpression ?? string.Empty,
            runtimePlan.NullabilityDataProbe?.RepairInvariantExpression ?? string.Empty,
            runtimePlan.NullabilityDataProbe?.BlockedExpression ?? string.Empty,
            runtimePlan.NullabilityDataProbe?.QualifiedTable ?? string.Empty,
            runtimePlan.Postcondition,
            runtimePlan.ExecutionPostcondition ?? runtimePlan.Postcondition);

        // The selected dollar tag cannot occur in embedded SQL, so provider
        // output cannot terminate the anonymous block accidentally.
        // WHY: The unconditional header has an exact known length. Cover it
        // in the first buffer instead of growing several initial chunks;
        // the variable SQL body still grows normally without a size guess.
        var builder = new StringBuilder(checked("DO ".Length + tag.Length + declarations.Length))
            .Append("DO ")
            .Append(tag)
            .Append(declarations)
            .Append(proofTable is null
                ? string.Empty
                : "    doka_target_matches boolean := FALSE;\n")
            .Append(runtimePlan.DataProbe is null
                ? string.Empty
                : "    doka_data_probe_required boolean := FALSE;\n"
                + "    doka_data_blocked boolean := FALSE;\n"
                + "    doka_transition_eligible boolean := FALSE;\n")
            .Append(runtimePlan.NullabilityDataProbe is null
                ? string.Empty
                : "    doka_nullability_blocked boolean := FALSE;\n"
                + "    doka_nullability_repair_eligible boolean := FALSE;\n"
                + "    doka_complete_not_null boolean := FALSE;\n")
            .Append("BEGIN\n");

        if (proofTable is not null)
        {
            // WHY: Pass one avoids taking an ACCESS EXCLUSIVE lock for no-op
            // and rejected operations. Only a planned Repair takes the lock;
            // pass two then executes this same complete classifier under it.
            builder.Append("    FOR doka_evaluation_pass IN 1..2 LOOP\n");
        }

        builder
            .Append(evaluationIndentation)
            .Append("IF NOT COALESCE((")
            .Append(runtimePlan.PrerequisiteExpression)
            .Append("), FALSE) THEN\n")
            .Append(evaluationBodyIndentation)
            .Append("doka_state := 'prerequisite_missing';\n")
            .Append(evaluationBodyIndentation)
            .Append("doka_repair_ok := FALSE;\n");

        if (runtimePlan.StateEvaluationGuardFailureExpression is not null)
        {
            builder
                .Append(evaluationIndentation)
                .Append("ELSIF NOT COALESCE((")
                .Append(runtimePlan.StateEvaluationGuardExpression)
                .Append("), FALSE) THEN\n")
                .Append(evaluationBodyIndentation)
                .Append("doka_state := (")
                .Append(runtimePlan.StateEvaluationGuardFailureExpression)
                .Append(");\n")
                .Append(evaluationBodyIndentation)
                .Append("doka_repair_ok := FALSE;\n");
        }

        builder
            .Append(evaluationIndentation)
            .Append("ELSE\n");

        if (proofTable is not null)
        {
            // WHY: Exact target and complete NULL metadata are fresh in each
            // classifier pass, including the locked recheck. Matching leaves
            // need no repair qualification; parent-only NOT NULL still does.
            builder
                .Append(evaluationBodyIndentation)
                .Append("doka_target_matches := COALESCE((")
                .Append(runtimePlan.Postcondition)
                .Append("), FALSE);\n");

            if (runtimePlan.NullabilityDataProbe is not null)
            {
                builder
                    .Append(evaluationBodyIndentation)
                    .Append("doka_complete_not_null := COALESCE((")
                    .Append(runtimePlan.NullabilityDataProbe.NotNullContractExpression)
                    .Append("), FALSE);\n");
            }

            builder
                .Append(evaluationBodyIndentation)
                .Append("IF doka_target_matches")
                .Append(runtimePlan.NullabilityDataProbe is null ? string.Empty : " AND doka_complete_not_null")
                .Append(" THEN\n")
                .Append(classificationIndentation)
                .Append("doka_state := 'matching';\n")
                .Append(classificationIndentation)
                .Append("doka_repair_ok := FALSE;\n")
                .Append(evaluationBodyIndentation)
                .Append("ELSE\n");
        }

        if (runtimePlan.DataProbe is not null)
        {
            AppendDataProbeEvaluationSql(
                builder, runtimePlan.DataProbe, blockedExpression, classificationIndentation);
        }

        if (runtimePlan.NullabilityDataProbe is not null)
        {
            AppendNullabilityDataProbeEvaluationSql(
                builder, runtimePlan, classificationIndentation, "doka_complete_not_null");
        }

        builder
            .Append(classificationIndentation)
            .Append("doka_state := (");

        runtimePlan.AppendStateExpression(
            builder,
            dataBlockedExpression,
            transitionEligibleExpression,
            proofTable is null ? null : runtimePlan.NullabilityDataProbe?.MatchingRequiresSourceContractProof != true
                ? "doka_target_matches"
                : "(doka_target_matches AND (doka_complete_not_null OR doka_nullability_repair_eligible))");

        builder
            .Append(");\n")
            .Append(classificationIndentation)
            .Append("doka_repair_ok := COALESCE((");

        runtimePlan.AppendRepairPrecondition(
            builder,
            dataBlockedExpression,
            transitionEligibleExpression);

        builder.Append("), FALSE);\n");
        if (proofTable is not null)
        {
            builder
                .Append(evaluationBodyIndentation)
                .Append("END IF;\n");
        }

        builder
            .Append(evaluationIndentation)
            .Append("END IF;\n")
            .Append(evaluationIndentation)
            .Append("doka_action := ");

        AppendActionCase(builder, operation, runtimePlan.RepairCapability);
        builder.Append(";\n");

        if (proofTable is not null)
        {
            builder
                .Append(evaluationIndentation)
                .Append("EXIT WHEN doka_action <> 'repair' OR doka_evaluation_pass = 2;\n")
                .Append(evaluationIndentation)
                .Append("LOCK TABLE ")
                .Append(proofTable)
                .Append(" IN ACCESS EXCLUSIVE MODE;\n")
                .Append("    END LOOP;\n");
        }

        builder
            .Append("    IF doka_action = 'reject_different' THEN\n")
            .Append("        RAISE EXCEPTION USING ERRCODE = 'P1001', MESSAGE = 'doka_sm_different';\n")
            .Append("    ELSIF doka_action = 'reject_unsupported' THEN\n")
            .Append("        RAISE EXCEPTION USING ERRCODE = 'P1002', MESSAGE = 'doka_sm_unsupported';\n")
            .Append("    ELSIF doka_action = 'reject_data_blocked' THEN\n")
            .Append("        RAISE EXCEPTION USING ERRCODE = 'P1003', MESSAGE = 'doka_sm_data_blocked';\n")
            .Append("    ELSIF doka_action = 'reject_prerequisite_missing' THEN\n")
            .Append("        RAISE EXCEPTION USING ERRCODE = 'P1004', MESSAGE = 'doka_sm_prerequisite_missing';\n")
            .Append("    ELSIF doka_action IN ('apply', 'repair') THEN\n")
            .Append("        IF doka_action = 'apply' THEN\n");

        AppendActionSql(builder, baseline, indentation: "            ");

        builder.Append("        ELSE\n");
        AppendActionSql(builder, repairBaseline, indentation: "            ");

        builder
            .Append("        END IF;\n")
            .Append("        IF NOT COALESCE((\n")
            .Append("            ")
            .Append(runtimePlan.ExecutionPostcondition ?? runtimePlan.Postcondition)
            .Append('\n')
            .Append("        ), FALSE) THEN\n")
            .Append("            RAISE EXCEPTION USING ERRCODE = 'P1005', MESSAGE = 'doka_sm_postcondition';\n")
            .Append("        END IF;\n")
            .Append("    END IF;\n")
            .Append("END\n")
            .Append(tag)
            .Append(';');

        return builder.ToString();
    }

    /// <summary>Appends freshly evaluated narrowing evidence using the operation's already-rendered row SQL.</summary>
    /// <param name="builder">The operation-owned final guard buffer.</param>
    /// <param name="dataProbe">The catalog-qualified narrowing proof definition.</param>
    /// <param name="blockedExpression">The immutable SQL also inspected during dollar-tag selection.</param>
    /// <param name="indentation">The unchanged indentation inside the current classifier pass.</param>
    private static void AppendDataProbeEvaluationSql(
        StringBuilder builder,
        PostgreSqlSafeMigrationDataProbe dataProbe,
        string blockedExpression,
        string indentation
    )
    {
        var transitionInvariant = dataProbe.TransitionInvariantExpression
            ?? throw new InvalidOperationException("The runtime plan has no transition evidence.");

        // WHY: Resolve the catalog-only transition first. PostgreSQL must not
        // plan a row query against a missing or unrelated target relation.
        builder
            .Append(indentation)
            .Append("doka_transition_eligible := COALESCE((")
            .Append(transitionInvariant)
            .Append("), FALSE);\n")
            .Append(indentation)
            .Append("doka_data_probe_required := doka_transition_eligible AND COALESCE((")
            .Append(dataProbe.NarrowingExpression)
            .Append("), FALSE);\n")
            .Append(indentation)
            .Append("doka_data_blocked := FALSE;\n")
            .Append(indentation)
            .Append("IF doka_data_probe_required THEN\n")
            .Append(indentation)
            .Append("    doka_data_blocked := COALESCE((")
            .Append(blockedExpression)
            .Append("), FALSE);\n")
            .Append(indentation)
            .Append("END IF;\n");
    }

    /// <summary>Materializes one fresh, catalog-qualified NULL proof for both runtime decisions.</summary>
    /// <param name="builder">The operation-owned final guard buffer.</param>
    /// <param name="runtimePlan">The reviewed plan containing the fresh source and row proofs.</param>
    /// <param name="indentation">The unchanged indentation for this classifier branch.</param>
    /// <param name="notNullContractExpression">The complete NOT NULL verdict evaluated in this pass.</param>
    private static void AppendNullabilityDataProbeEvaluationSql(
        StringBuilder builder,
        PostgreSqlSafeMigrationRuntimePlan runtimePlan,
        string indentation,
        string notNullContractExpression
    )
    {
        var proof = runtimePlan.NullabilityDataProbe
            ?? throw new InvalidOperationException("The runtime plan has no nullability data probe.");

        // WHY: attnotnull can be unvalidated on PostgreSQL 18, and a parent
        // declaration alone need not cover descendants. Only a fresh proof
        // suppresses the row query; a second evaluation runs under the lock.
        builder
            .Append(indentation)
            .Append("doka_nullability_repair_eligible := COALESCE((");

        runtimePlan.AppendNullabilityRepairInvariantExpression(builder);

        builder
            .Append("), FALSE);\n")
            .Append(indentation)
            .Append("doka_nullability_blocked := FALSE;\n")
            .Append(indentation)
            .Append("IF doka_nullability_repair_eligible AND NOT COALESCE((")
            .Append(notNullContractExpression)
            .Append("), FALSE) THEN\n")
            .Append(indentation)
            .Append("    doka_nullability_blocked := COALESCE((")
            .Append(proof.BlockedExpression)
            .Append("), FALSE);\n")
            .Append(indentation)
            .Append("END IF;\n");
    }

    /// <summary>Appends provider baselines with the existing termination and indentation contract.</summary>
    /// <param name="builder">The operation-owned final guard buffer.</param>
    /// <param name="commands">The ordered provider commands for one action branch.</param>
    /// <param name="indentation">The unchanged indentation for every rendered command line.</param>
    private static void AppendActionSql(
        StringBuilder builder,
        IReadOnlyList<MigrationCommand> commands,
        string indentation
    )
    {
        if (commands.Count == 0)
        {
            builder
                .Append(indentation)
                .Append("NULL;\n");

            return;
        }

        // WHY: Flattening both baseline lists creates two chunk chains and
        // complete SQL strings. Each command can preserve the same bytes
        // directly: trim trailing whitespace, terminate, then indent lines.
        for (var index = 0; index < commands.Count; index++)
        {
            AppendIndentedLines(builder, commands[index].CommandText.AsSpan().TrimEnd(), indentation);
        }
    }

    /// <summary>Appends every canonical policy decision without a second action-case buffer or string.</summary>
    /// <param name="builder">The operation-owned final guard buffer.</param>
    /// <param name="operation">The intent and policy evaluated for every observed state.</param>
    /// <param name="repairCapability">The provider-proven repair boundary.</param>
    private static void AppendActionCase(
        StringBuilder builder,
        SafeMigrationOperation operation,
        SafeMigrationRepairCapability repairCapability
    )
    {
        builder.Append("CASE doka_state ");
        foreach (var state in Enum.GetValues<SafeMigrationObservedState>())
        {
            var action = SafeMigrationDecisionPlanner.PlanAction(
                operation.Intent.Kind,
                state,
                operation.Policy,
                repairCapability);

            builder
                .Append("WHEN '")
                .Append(StateCode(state))
                .Append("' THEN ");

            if (action == SafeMigrationAction.Repair)
            {
                builder
                    .Append("CASE WHEN doka_repair_ok THEN 'repair' ")
                    .Append("ELSE 'reject_different' END ");
            }
            else
            {
                builder
                    .Append('\'')
                    .Append(ActionCode(action))
                    .Append("' ");
            }
        }

        builder.Append("ELSE 'reject_unsupported' END");
    }

    private static string StateCode(
        SafeMigrationObservedState state
    ) => state switch
    {
        SafeMigrationObservedState.Missing => "missing",
        SafeMigrationObservedState.Matching => "matching",
        SafeMigrationObservedState.Different => "different",
        SafeMigrationObservedState.Unsupported => "unsupported",
        SafeMigrationObservedState.DataBlocked => "data_blocked",
        SafeMigrationObservedState.PrerequisiteMissing => "prerequisite_missing",
        SafeMigrationObservedState.TransitionReady => "transition_ready",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string ActionCode(
        SafeMigrationAction action
    ) => action switch
    {
        SafeMigrationAction.Apply => "apply",
        SafeMigrationAction.NoOp => "no_op",
        SafeMigrationAction.Repair => "repair",
        SafeMigrationAction.RejectDifferent => "reject_different",
        SafeMigrationAction.RejectUnsupported => "reject_unsupported",
        SafeMigrationAction.RejectDataBlocked => "reject_data_blocked",
        SafeMigrationAction.RejectPrerequisiteMissing => "reject_prerequisite_missing",
        SafeMigrationAction.ValidateAtRuntime => throw new InvalidOperationException(
            "A report-only runtime-validation action cannot generate migration SQL."),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Selects a delimiter for guards that embed expressions but no baseline command lists.</summary>
    /// <param name="sqlParts">The SQL expressions embedded into the anonymous block.</param>
    /// <returns>The first legacy candidate without an embedded-text collision.</returns>
    private static string SelectDollarTag(
        params ReadOnlySpan<string> sqlParts
    ) => SelectDollarTag([], [], sqlParts);

    /// <summary>Selects a delimiter absent from every original provider command and guard expression.</summary>
    /// <param name="baseline">The ordered commands for the apply branch.</param>
    /// <param name="repairBaseline">The ordered commands for the repair branch.</param>
    /// <param name="sqlParts">The catalog and row-proof text embedded into the anonymous block.</param>
    /// <returns>The first legacy candidate without a rendered-text collision.</returns>
    private static string SelectDollarTag(
        IReadOnlyList<MigrationCommand> baseline,
        IReadOnlyList<MigrationCommand> repairBaseline,
        params ReadOnlySpan<string> sqlParts
    )
    {
        for (var suffix = 0; ; suffix++)
        {
            var tag = suffix == 0 ? "$doka_safe_migration$" : $"$doka_safe_migration_{suffix}$";

            // WHY: Command separators are newlines and added semicolons;
            // neither can form a dollar tag across command boundaries.
            // Raw command scans cover every tag in their trimmed output.
            var collision = HasDollarTagCollision(baseline, tag)
                || HasDollarTagCollision(repairBaseline, tag);

            foreach (var sql in sqlParts)
            {
                if (sql.Contains(tag, StringComparison.Ordinal))
                {
                    collision = true;
                    break;
                }
            }

            if (!collision)
            {
                return tag;
            }
        }
    }

    /// <summary>Checks original provider text without flattening either action's command list.</summary>
    /// <param name="commands">The ordered provider commands embedded into the guard.</param>
    /// <param name="tag">The candidate delimiter for the anonymous block.</param>
    /// <returns>Whether a provider command contains the candidate tag.</returns>
    private static bool HasDollarTagCollision(
        IReadOnlyList<MigrationCommand> commands,
        string tag
    )
    {
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index].CommandText.Contains(tag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Appends one trimmed command while retaining interior CRLF and existing terminators.</summary>
    /// <param name="builder">The operation-owned final guard buffer.</param>
    /// <param name="sql">The provider command with trailing whitespace already removed.</param>
    /// <param name="indentation">The indentation appended before every original line.</param>
    private static void AppendIndentedLines(
        StringBuilder builder,
        ReadOnlySpan<char> sql,
        string indentation
    )
    {
        var start = 0;
        while (start <= sql.Length)
        {
            var relativeNewline = sql[start..].IndexOf('\n');
            var newline = relativeNewline < 0 ? -1 : start + relativeNewline;
            var length = newline < 0 ? sql.Length - start : newline - start;

            builder
                .Append(indentation)
                .Append(sql.Slice(start, length));

            if (newline < 0 && (sql.IsEmpty || sql[^1] != ';'))
            {
                builder.Append(';');
            }

            builder.Append('\n');

            if (newline < 0)
            {
                return;
            }

            start = newline + 1;
        }
    }
}
