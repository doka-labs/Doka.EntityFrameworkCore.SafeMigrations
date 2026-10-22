namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler : IMySqlMigrationOperationHandler
{
    private const string HandlerIdentifier = "Doka.EntityFrameworkCore.SafeMigrations.MySql.SafeMigrationOperation";
    private const string IndexPrefixLengthAnnotation = "Doka:MySql:IndexPrefixLength";
    private const string PreparedStatementName = "doka_sm_statement";
    private const string PrepareStatementSql = "PREPARE " + PreparedStatementName + " FROM @doka_sm_sql;";
    private const string EvaluatePreparedStatementSql = PrepareStatementSql
        + "EXECUTE " + PreparedStatementName + ";DEALLOCATE PREPARE " + PreparedStatementName + ";";

    private const string PreparedApplySqlPrefix =
        "SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' THEN CONVERT(0x";

    private const string PreparedRepairSqlPrefix =
        " USING utf8mb4) WHEN @doka_sm_action = 'repair' THEN CONVERT(0x";

    private const string PreparedSqlAssignmentSuffix = " USING utf8mb4) ELSE 'DO 0' END;";
    private const string PostconditionBodyPrefix = "SET @doka_sm_post_ok = CASE "
        + "WHEN @doka_sm_action IN ('apply', 'repair') THEN COALESCE((";

    private const string PostconditionAssertionSql = "INSERT INTO `__doka_sm_assert` "
        + "SELECT 4, 5, 6, 7, @doka_sm_diag WHERE NOT COALESCE(@doka_sm_post_ok, FALSE);\n";

    private const string PostconditionAssignmentSuffix = "), FALSE) ELSE TRUE END;\n";
    private const string PostconditionBodySuffix = PostconditionAssignmentSuffix + PostconditionAssertionSql;
    private const string DecisionAssertionSql = "INSERT INTO `__doka_sm_assert` "
        + "SELECT @doka_sm_diag, @doka_sm_diag, @doka_sm_diag, @doka_sm_diag, @doka_sm_diag "
        + "UNION ALL SELECT @doka_sm_diag, 2, 2, 2, 2 WHERE @doka_sm_action = 'reject_different' "
        + "UNION ALL SELECT 2, @doka_sm_diag, 3, 3, 3 WHERE @doka_sm_action = 'reject_unsupported' "
        + "UNION ALL SELECT 3, 4, "
        + "CASE WHEN @doka_sm_action = 'reject_data_blocked' THEN @doka_sm_diag ELSE 5 END, "
        + "CASE WHEN @doka_sm_action = 'reject_prerequisite_missing' THEN @doka_sm_diag ELSE 5 END, 4 "
        + "WHERE @doka_sm_action IN ('reject_data_blocked', 'reject_prerequisite_missing');";

    private const string MatchingStatePrefix =
        "SET @doka_sm_state = CASE WHEN @doka_sm_state IS NULL THEN CASE WHEN COALESCE((";

    private const string MatchingStateSuffix =
        "), FALSE) THEN 'matching' ELSE NULL END ELSE @doka_sm_state END;";

    private static readonly IReadOnlyList<ExpectedIndexDefinition> s_emptyUniqueIndexes = [];
    private static readonly IReadOnlyList<string> s_guardCleanupCommands = Array.AsReadOnly<string>(
        [BuildGuardCleanupSql(hasNullabilityProof: false), BuildPreparedStatementCleanupSql()]);

    private static readonly IReadOnlyList<string> s_nullabilityGuardCleanupCommands = Array.AsReadOnly<string>(
        [BuildGuardCleanupSql(hasNullabilityProof: true), BuildPreparedStatementCleanupSql()]);

    private readonly MySqlSafeMigrationCatalogSqlBuilder _catalogSqlBuilder;
    private readonly IModel _designTimeModel;
    private readonly MySqlSafeMigrationPlanCapture _planCapture;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly MySqlSafeMigrationSqlExpressionRenderer _expressionRenderer;
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly RelationalTypeMapping _stringMapping;
    private Dictionary<ModelTableKey, IReadOnlyList<ExpectedIndexDefinition>>? _modelUniqueIndexes;
    private IModel? _modelUniqueIndexSource;
    private ActionAssignmentCacheEntry? _actionAssignmentCache;

    /// <summary>Retains exact generated/CHECK text bytes without relying on session quote interpretation.</summary>
    private string RenderStructuredExpression(SafeMigrationSqlExpression expression) =>
        MySqlCapturedExpressionContract.RenderExecutable(expression, _expressionRenderer);

    public MySqlSafeMigrationOperationHandler(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper,
        MySqlSafeMigrationPlanCapture planCapture,
        IDesignTimeModel designTimeModel
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);
        ArgumentNullException.ThrowIfNull(planCapture);
        ArgumentNullException.ThrowIfNull(designTimeModel);

        _catalogSqlBuilder = new MySqlSafeMigrationCatalogSqlBuilder(typeMappingSource, sqlGenerationHelper);
        _designTimeModel = designTimeModel.Model;
        _planCapture = planCapture;
        _sqlGenerationHelper = sqlGenerationHelper;
        _expressionRenderer = new MySqlSafeMigrationSqlExpressionRenderer(typeMappingSource, sqlGenerationHelper);
        _typeMappingSource = typeMappingSource;
        _stringMapping = typeMappingSource.FindMapping(typeof(string))
            ?? throw new InvalidOperationException("The MySQL provider has no string type mapping.");
    }

    public string HandlerId => HandlerIdentifier;

    public Type OperationType => typeof(SafeMigrationOperation);

    public MySqlMigrationOperationResult Generate(
        MySqlMigrationOperationContext context
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var operation = context.Operation as SafeMigrationOperation
            ?? throw new ArgumentException(
                "The MySQL SafeMigrations handler received an unexpected operation type.",
                nameof(context));

        var expectedUniqueIndexes = operation.Intent is EnsureTableIntent table
            ? GetExpectedUniqueIndexes(table)
            : null;

        var expectedTableConstraints = operation.Intent is EnsureTableIntent tableIntent
            ? GetExpectedTableConstraints(tableIntent, context.Model)
            : null;

        var hasGeneratedDependencyContract = operation.Intent is RenameColumnIntent or DropColumnIntent
            && (_planCapture.IsActive || _planCapture.HasGenerationContract);

        var runtimePlan = _catalogSqlBuilder.Build(
            operation,
            context,
            expectedUniqueIndexes,
            expectedTableConstraints,
            includeAnalysisEvidence: _planCapture.IncludeAnalysisEvidence,
            includeTransitionEvidence: !_planCapture.IsActive || _planCapture.IncludeTransitionEvidence,
            parameterizeValues: _planCapture.IsActive,
            requiredDatabaseQualifiers: _planCapture.HasGenerationContract
                ? _planCapture.GenerationDatabaseQualifiers
                : null,
            requiresNullFreeBackfill: _planCapture.RequiresNullFreeBackfill(operation),
            nativeTableAlternative: operation.Intent is EnsureTableIntent nativeTable
                ? _planCapture.GetGenerationTableAlternative(nativeTable)
                : null,
            nativeNullFreeBackfillCondition: _planCapture.GetGenerationNullFreeBackfillCondition(operation),
            generatedColumnDependency: hasGeneratedDependencyContract
                ? _planCapture.GetGenerationGeneratedColumnDependencyContract(operation) : null,
            foldedGeneratedColumnDependency: hasGeneratedDependencyContract
                ? _planCapture.GetGenerationFoldedGeneratedColumnDependencyContract(operation) : null,
            bindFoldedGeneratedColumnDependency: _planCapture.HasGenerationContract,
            generatedColumnModelCandidate: hasGeneratedDependencyContract
                ? _planCapture.GetGenerationGeneratedColumnModelCandidate(operation) : null,
            foldedGeneratedColumnModelCandidate: hasGeneratedDependencyContract
                ? _planCapture.GetGenerationFoldedGeneratedColumnModelCandidate(operation) : null,
            requiresPrimaryKeyReplacementRows: _planCapture.RequiresPrimaryKeyReplacementRows(operation));

        if (_planCapture.IsActive)
        {
            _planCapture.Record(context.OperationOrdinal, operation, runtimePlan);

            return MySqlMigrationOperationResult.Generated(
                [Command("DO 0;", transactionSuppressed: true)],
                "safe_catalog_plan_capture");
        }

        var diagnostic = new MySqlSafeMigrationRuntimeDiagnostic(operation, context.OperationOrdinal, runtimePlan);
        var scriptDiagnostics = (context.Options & MigrationsSqlGenerationOptions.Script) != 0;
        if (!scriptDiagnostics)
        {
            _planCapture.RecordRuntimeDiagnostic(diagnostic);
        }

        var postconditionSuffix = BuildPostconditionSuffix(diagnostic, scriptDiagnostics);
        runtimePlan = BindRuntimeBinaryDefaults(runtimePlan);
        runtimePlan = BindRuntimeIdentifierReferences(runtimePlan);

        if (runtimePlan.RequiredColumnAddPlan is { } requiredAddProtocol)
        {
            // WHY: One completed catalog statement owns this operation-local
            // status. All later eligibility renders consume that same fact,
            // while capture retains the pure expression without session state.
            runtimePlan = runtimePlan with
            {
                RequiredColumnAddPlan = requiredAddProtocol with { StatusBinding = "@doka_sm_add_status", },
            };
        }

        var requiredColumnAdd = RenderRequiredColumnAddBaseline(operation, runtimePlan, context);
        var baseline = requiredColumnAdd?.Baseline ?? GetBaselineBatch(RenderBaseline(operation, runtimePlan, context));
        var repair = requiredColumnAdd is null
            ? RenderRepairBaseline(operation, runtimePlan, context, baseline)
            : null;
        var hasNativeSqlModeCleanup = HasCertifiedNativeSqlModeCleanup(baseline);

        // WHY: Runtime plans normally contain no captured parameters. Avoid a per-operation
        // LINQ iterator and instance delegate even when the result would be the shared empty array.
        var renderedParameterValues = runtimePlan.ParameterValues.Length == 0
            ? Array.Empty<string>()
            : new string[runtimePlan.ParameterValues.Length];

        // WHY: Render parameter literals into the slots consumed by their original parameter ordinals.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < renderedParameterValues.Length; index++)
        {
            renderedParameterValues[index] = RenderParameterLiteral(runtimePlan.ParameterValues[index]);
        }

        // A connection-local temporary table turns rejected decisions and a
        // failed postcondition into deterministic server errors without a
        // persistent stored routine or shared database object.
        // WHY: These capacities cover the owned fragments, including the qualified lazy path.
        // Opaque provider setup grows on demand instead of reserving its space for every operation.
        var setupCommands = new List<MySqlSafeMigrationSetupFragment>(runtimePlan.RequiresLazyStateEvaluation ? 13 : 5)
        {
            MySqlSafeMigrationSetupFragment.Compose(diagnostic.Marker,
                hasNativeSqlModeCleanup ? ProviderSetupEnteredReset : string.Empty, BuildAssertionSetupSql()),
        };

        var fusedSetupFragmentCount = 0;
        if (runtimePlan.RequiresLazyStateEvaluation)
        {
            if (runtimePlan.CurrentDatabaseQualificationExpression is not null)
            {
                setupCommands.Add(MySqlSafeMigrationSetupFragment.Compose(
                    "SET @doka_sm_state = CASE WHEN COALESCE((",
                    runtimePlan.RenderPreparedCurrentDatabaseQualificationExpression(renderedParameterValues),
                    "), FALSE) THEN NULL ELSE 'unsupported' END;"));

                setupCommands.Add(MySqlSafeMigrationSetupFragment.Compose(
                    "SET @doka_sm_prerequisite_ok = CASE WHEN @doka_sm_state IS NULL THEN COALESCE((",
                    runtimePlan.RenderPreparedPrerequisiteExpression(renderedParameterValues),
                    "), FALSE) ELSE FALSE END;"));
            }
            else
            {
                // WHY: Only qualified operations need an earlier state that the prerequisite phase preserves.
                setupCommands.Add(MySqlSafeMigrationSetupFragment.Compose(
                    "SET @doka_sm_prerequisite_ok = COALESCE((",
                    runtimePlan.RenderPreparedPrerequisiteExpression(renderedParameterValues),
                    "), FALSE);"));
            }

            setupCommands.Add(BuildInitialLazyStateAssignment(
                preserveExistingState: runtimePlan.CurrentDatabaseQualificationExpression is not null));
            if (runtimePlan.RequiredColumnAddPlan is not null)
            {
                setupCommands.Add(BuildRequiredColumnAddStatusAssignment(runtimePlan, renderedParameterValues));
                setupCommands.Add("SET @doka_sm_state = CASE WHEN @doka_sm_add_status = 'complete' "
                    + "THEN 'matching' ELSE @doka_sm_state END;");
            }

            if (runtimePlan.IdentifierReferenceProofs.Count > 0)
            {
                setupCommands.Add(BuildIdentifierReferenceProofEvaluation(runtimePlan));
            }

            if (runtimePlan.StateEvaluationGuardFailureExpression is not null)
            {
                setupCommands.Add(BuildGuardEvaluationAssignment(runtimePlan, renderedParameterValues));
                fusedSetupFragmentCount += 3;
            }

            if (runtimePlan.BinaryDefaultProofs.Count > 0)
            {
                setupCommands.Add(BuildBinaryDefaultProofEvaluation(
                    runtimePlan, renderedParameterValues, "@doka_sm_state IS NULL"));
            }

            // WHY: An exact match needs no repair proof. Its classification belongs to the first
            // existing evaluation fragment after identity, existence and guard decisions; it is
            // not an extra provider fragment and is never retained across operations or their DDL.
            var matchingPrecondition = runtimePlan.RequiredColumnAddPlan is null
                && operation.Intent is EnsureColumnIntent or AlterColumnIntent
                && runtimePlan.RepairCapability == SafeMigrationRepairCapability.Safe
                    ? runtimePlan.RenderPreparedExecutionPostcondition(renderedParameterValues)
                    : null;

            if (runtimePlan.DataProbe is not null)
            {
                setupCommands.Add(BuildTransitionEligibilityAssignment(
                    runtimePlan, renderedParameterValues, matchingPrecondition));
                matchingPrecondition = null;
                setupCommands.Add(BuildDataProbeRequiredAssignment(runtimePlan, renderedParameterValues));
                setupCommands.Add(BuildDataProbeEvaluationAssignment(runtimePlan, renderedParameterValues));
                fusedSetupFragmentCount += 3;
            }

            if (runtimePlan.NullabilityDataProbe is not null)
            {
                setupCommands.Add(BuildNullabilityDataProbeEvaluationAssignment(
                    runtimePlan, renderedParameterValues, matchingPrecondition));
                matchingPrecondition = null;
                fusedSetupFragmentCount += 4;
            }

            setupCommands.Add(BuildStateEvaluationAssignment(
                runtimePlan, renderedParameterValues, matchingPrecondition));
            fusedSetupFragmentCount += 3;
        }
        else
        {
            if (runtimePlan.RepairCapability == SafeMigrationRepairCapability.Safe)
            {
                // WHY: State must be assigned by a completed statement before repair reads it.
                setupCommands.Add(MySqlSafeMigrationSetupFragment.Compose(
                    "SET @doka_sm_state = (",
                    runtimePlan.RenderPreparedStateExpression(renderedParameterValues),
                    ");SET @doka_sm_repair_ok = CASE WHEN @doka_sm_state = 'different' THEN COALESCE((",
                    runtimePlan.RenderPreparedRepairPrecondition(renderedParameterValues),
                    "), FALSE) ELSE FALSE END;"));
            }
            else
            {
                setupCommands.Add(MySqlSafeMigrationSetupFragment.Compose(
                    "SET @doka_sm_state = (",
                    runtimePlan.RenderPreparedStateExpression(renderedParameterValues),
                    "), @doka_sm_repair_ok = (",
                    runtimePlan.RenderPreparedRepairPrecondition(renderedParameterValues),
                    ");"));
            }
        }

        // WHY: Both right-hand sides consume already completed state/proof
        // variables, never each other's assignment. One existing SET retains
        // native fragment admission without undefined user-variable ordering.
        setupCommands.Add(MySqlSafeMigrationSetupFragment.Compose(
            BuildActionAssignment(operation.Intent.Kind, operation.Policy, runtimePlan.RepairCapability,
                terminateStatement: false),
            ", @doka_sm_diag = CONCAT('", diagnostic.Correlation, "-', ",
            diagnostic.BuildReasonSlotExpression(), ");"));
        setupCommands.Add(BuildDecisionAssertionSql(diagnostic, scriptDiagnostics));

        if (requiredColumnAdd is not null)
        {
            setupCommands.Add(BuildRequiredColumnAddResumeAssignment(runtimePlan, renderedParameterValues));
        }

        var prepareBaselineInSetup = requiredColumnAdd is null && baseline.Commands.Count == 1;
        var providerSetupOffset = setupCommands.Count;
        var providerSetupCount = 0;
        if (prepareBaselineInSetup || requiredColumnAdd is not null)
        {
            var command = baseline.Commands[0];

            // WHY: Preserve the established single-command hot path. Only
            // genuine provider sequences need command-local preparation in the body.
            providerSetupCount = command.SetupCommands.Count;
            // WHY: Preserve first-setup identity and provider order while capturing the native SQL-mode snapshot.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < command.SetupCommands.Count; index++)
            {
                setupCommands.Add(hasNativeSqlModeCleanup && index == 0
                    ? BuildAcquiredNativeSqlModeSnapshot(command.SetupCommands[index])
                    : command.SetupCommands[index]);
            }

            if (prepareBaselineInSetup)
            {
                setupCommands.Add(
                    BuildPreparedSqlAssignment(
                        command.BodyCommand,
                        GetRepairBody(operation, runtimePlan, repair, command, commandOrdinal: 0),
                        prepareInSetup: true));
                fusedSetupFragmentCount++;
            }
        }

        var bodyCommand = requiredColumnAdd is not null
            ? BuildRequiredColumnAddBody(requiredColumnAdd, runtimePlan, renderedParameterValues, postconditionSuffix)
            : BuildGuardedBaselineBody(operation, runtimePlan, baseline, repair,
                renderedParameterValues, prepareBaselineInSetup, postconditionSuffix);

        var cleanupCommands = BuildCleanupCommands(baseline, runtimePlan.NullabilityDataProbe is not null,
            runtimePlan.RequiredColumnAddPlan is not null, hasNativeSqlModeCleanup,
            runtimePlan.BinaryDefaultProofs.Count, runtimePlan.IdentifierReferenceProofs);

        // WHY: Reduce owned setup transport without joining opaque provider setup, the guarded
        // body or independent cleanup. Fresh checks and exact script text remain operation-local.
        var compactedSetup = MySqlSafeMigrationSetupCommandCompactor.Compact(
            setupCommands,
            bodyCommand,
            cleanupCommands,
            providerSetupOffset,
            providerSetupCount,
            fusedSetupFragmentCount);

        var scopedCommand = MySqlMigrationCommandSpec.CreateScoped(
            compactedSetup,
            bodyCommand,
            cleanupCommands,
            baseline.TransactionSuppressed);

        return MySqlMigrationOperationResult.Generated([scopedCommand], "safe_guarded_operation");
    }

    /// <summary>Retains one pure decision assignment without retaining an operation or live evidence.</summary>
    /// <param name="Kind">The canonical planner operation kind.</param>
    /// <param name="Policy">The canonical planner policy.</param>
    /// <param name="RepairCapability">The provider's structural repair capability.</param>
    /// <param name="Sql">The complete immutable assignment for this exact tuple.</param>
    /// <param name="UnterminatedSql">The same immutable assignment before its final statement separator.</param>
    private sealed record ActionAssignmentCacheEntry(
        SafeMigrationOperationKind Kind,
        SafeMigrationPolicy Policy,
        SafeMigrationRepairCapability RepairCapability,
        string Sql,
        string UnterminatedSql
    );

    /// <summary>Renders the canonical decision table, reusing only an exact pure-input assignment.</summary>
    /// <param name="kind">The canonical planner operation family.</param>
    /// <param name="policy">The canonical conflict policy.</param>
    /// <param name="repairCapability">The structural repair capability, not a cached row-safety result.</param>
    /// <param name="terminateStatement">Whether the caller needs the complete standalone statement.</param>
    /// <returns>The immutable decision assignment for the exact tuple.</returns>
    internal string BuildActionAssignment(
        SafeMigrationOperationKind kind,
        SafeMigrationPolicy policy,
        SafeMigrationRepairCapability repairCapability,
        bool terminateStatement = true
    )
    {
        var cached = Volatile.Read(ref _actionAssignmentCache);
        if (cached is not null
            && cached.Kind == kind
            && cached.Policy == policy
            && cached.RepairCapability == repairCapability)
        {
            return terminateStatement ? cached.Sql : cached.UnterminatedSql;
        }

        ReadOnlySpan<SafeMigrationObservedState> states =
        [
            SafeMigrationObservedState.Missing,
            SafeMigrationObservedState.Matching,
            SafeMigrationObservedState.Different,
            SafeMigrationObservedState.Unsupported,
            SafeMigrationObservedState.DataBlocked,
            SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationObservedState.TransitionReady,
        ];

        var builder = new StringBuilder("SET @doka_sm_action = CASE @doka_sm_state ");

        // Materialize every planner state into SQL so runtime behavior stays
        // coupled to the provider-neutral decision table.
        foreach (var state in states)
        {
            var action = SafeMigrationDecisionPlanner.PlanAction(
                kind,
                state,
                policy,
                repairCapability);

            builder
                .Append("WHEN '")
                .Append(StateCode(state))
                .Append("' THEN ");

            if (action == SafeMigrationAction.Repair)
            {
                builder
                    .Append("CASE WHEN COALESCE(@doka_sm_repair_ok, FALSE) ")
                    .Append("THEN 'repair' ELSE 'reject_different' END ");
            }
            else
            {
                builder
                    .Append('\'')
                    .Append(ActionCode(action))
                    .Append("' ");
            }
        }

        var sql = builder
            .Append("ELSE 'reject_unsupported' END;")
            .ToString();

        // WHY: Adjacent columns repeatedly render the same planner tuple. Keep one immutable entry,
        // not a growing cache or any live safety evidence; atomic publication cannot mix keys and SQL.
        var unterminatedSql = sql[..^1];
        Volatile.Write(ref _actionAssignmentCache,
            new ActionAssignmentCacheEntry(kind, policy, repairCapability, sql, unterminatedSql));

        return terminateStatement ? sql : unterminatedSql;
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

    private static string BuildAssertionSetupSql() => "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`; "
        + "CREATE TEMPORARY TABLE `__doka_sm_assert` ("
        + "`different_code` VARCHAR(40) NOT NULL, "
        + "`unsupported_code` VARCHAR(40) NOT NULL, "
        + "`data_blocked_code` VARCHAR(40) NOT NULL, "
        + "`prerequisite_missing_code` VARCHAR(40) NOT NULL, "
        + "`postcondition_code` VARCHAR(40) NOT NULL, "
        + "CONSTRAINT `doka_sm_different` UNIQUE (`different_code`), "
        + "CONSTRAINT `doka_sm_unsupported` UNIQUE (`unsupported_code`), "
        + "CONSTRAINT `doka_sm_data_blocked` UNIQUE (`data_blocked_code`), "
        + "CONSTRAINT `doka_sm_prerequisite_missing` UNIQUE (`prerequisite_missing_code`), "
        + "CONSTRAINT `doka_sm_postcondition` UNIQUE (`postcondition_code`)"
        + ") CHARACTER SET ascii COLLATE ascii_bin;";

    /// <summary>Seeds and rejects in the same existing assertion statement without another transport exchange.</summary>
    private static string BuildDecisionAssertionSql(MySqlSafeMigrationRuntimeDiagnostic diagnostic, bool script)
    {
        // WHY: One bounded ASCII value carries both correlation and reason.
        // VARCHAR avoids mode-dependent CHAR padding, and the original single
        // column unique names preserve native guard identities. Shared SQL does
        // not copy a nonce or wider composite-key definition for every branch.
        // WHY: This private temporary table is created with exactly these five
        // columns in this order. Positional inserts remove repeated column-list
        // copies; a changed owned shape is rejected by the shape contract test.
        return script ? BuildDiagnosticResultSql(diagnostic, postcondition: false) + DecisionAssertionSql
            : DecisionAssertionSql;
    }

    /// <summary>Emits actionable script evidence immediately before an owned postcondition assertion.</summary>
    private static string BuildPostconditionSuffix(MySqlSafeMigrationRuntimeDiagnostic diagnostic, bool script) =>
        script ? PostconditionAssignmentSuffix + BuildDiagnosticResultSql(diagnostic, postcondition: true)
            + PostconditionAssertionSql : PostconditionBodySuffix;

    private string Literal(
        string value
    ) => _stringMapping.GenerateSqlLiteral(value);

    private string RenderParameterLiteral(
        MySqlCatalogParameterValue parameter
    )
    {
        if (parameter.StoreType is null)
        {
            return Literal((string)parameter.Value!);
        }

        var mapping = MySqlCatalogTypeMapping.Resolve(
            _typeMappingSource,
            parameter.Value,
            parameter.StoreType);

        return mapping.GenerateSqlLiteral(parameter.Value);
    }

    private static MySqlMigrationCommandSpec Command(
        string sql,
        bool transactionSuppressed
    ) => MySqlMigrationCommandSpec.Create(sql, transactionSuppressed);
}
