namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler : IMySqlMigrationOperationHandler
{
    private const string HandlerIdentifier = "Doka.EntityFrameworkCore.SafeMigrations.MySql.SafeMigrationOperation";
    private const string PreparedStatementName = "doka_sm_statement";
    private readonly MySqlSafeMigrationCatalogSqlBuilder _catalogSqlBuilder;
    private readonly MySqlSafeMigrationPlanCapture _planCapture;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly MySqlSafeMigrationSqlExpressionRenderer _expressionRenderer;
    private readonly RelationalTypeMapping _stringMapping;

    public MySqlSafeMigrationOperationHandler(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper,
        MySqlSafeMigrationPlanCapture planCapture
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);
        ArgumentNullException.ThrowIfNull(planCapture);

        _catalogSqlBuilder = new MySqlSafeMigrationCatalogSqlBuilder(typeMappingSource, sqlGenerationHelper);
        _planCapture = planCapture;
        _sqlGenerationHelper = sqlGenerationHelper;
        _expressionRenderer = new MySqlSafeMigrationSqlExpressionRenderer(typeMappingSource, sqlGenerationHelper);
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

        var runtimePlan = _catalogSqlBuilder.Build(operation, context);
        if (_planCapture.IsActive)
        {
            _planCapture.Record(context.OperationOrdinal, operation, runtimePlan);

            return MySqlMigrationOperationResult.Generated(
                [Command("DO 0;", transactionSuppressed: true)],
                "safe_catalog_plan_capture");
        }

        var baseline = RenderBaseline(operation.Intent, runtimePlan, context);
        var baselineCommand = GetSingleBaselineCommand(baseline);
        var baselineFragments = GetBaselineFragments(baselineCommand);
        var defaultSuppression = baselineCommand.TransactionSuppressed;
        var renderedParameterValues = runtimePlan
            .ParameterValues
            .Select(Literal)
            .ToArray();

        // A connection-local temporary table turns rejected decisions and a
        // failed postcondition into deterministic server errors without a
        // persistent stored routine or shared database object.
        var setupCommands = new List<string>(12 + baselineFragments.SetupCommands.Count)
        {
            BuildAssertionSetupSql(),
        };

        if (runtimePlan.RequiresLazyStateEvaluation)
        {
            setupCommands.Add(
                $"SET @doka_sm_prerequisite_ok = COALESCE(("
                + $"{runtimePlan.RenderPreparedPrerequisiteExpression(renderedParameterValues)}), FALSE);");
            setupCommands.Add(
                "SET @doka_sm_state = CASE WHEN @doka_sm_prerequisite_ok "
                + "THEN NULL ELSE 'prerequisite_missing' END, @doka_sm_repair_ok = FALSE;");
            setupCommands.Add(BuildStateEvaluationAssignment(runtimePlan, renderedParameterValues));
            setupCommands.Add($"PREPARE {PreparedStatementName} FROM @doka_sm_sql;");
            setupCommands.Add($"EXECUTE {PreparedStatementName};");
            setupCommands.Add($"DEALLOCATE PREPARE {PreparedStatementName};");
        }
        else
        {
            setupCommands.Add(
                $"SET @doka_sm_state = ({runtimePlan.RenderPreparedStateExpression(renderedParameterValues)}), "
                + $"@doka_sm_repair_ok = ({runtimePlan.RenderPreparedRepairPrecondition(renderedParameterValues)});");
        }

        setupCommands.Add(BuildActionAssignment(operation, runtimePlan.RepairCapability));
        setupCommands.Add(BuildDecisionAssertionSql());
        setupCommands.AddRange(baselineFragments.SetupCommands);

        // PREPARE selects the real DDL only for apply or repair; every no-op
        // path executes the harmless placeholder on the same guarded path.
        setupCommands.Add(BuildPreparedSqlAssignment(baselineFragments.BodyCommand));
        setupCommands.Add($"PREPARE {PreparedStatementName} FROM @doka_sm_sql;");

        var bodyCommand = $"EXECUTE {PreparedStatementName};\n"
            + "SET @doka_sm_post_ok = CASE "
            + "WHEN @doka_sm_action IN ('apply', 'repair') "
            + $"THEN COALESCE(({runtimePlan.RenderPreparedPostcondition(renderedParameterValues)}), FALSE) "
            + "ELSE TRUE END;\n"
            + "INSERT INTO `__doka_sm_assert` "
            + "(`different_code`, `unsupported_code`, `data_blocked_code`, "
            + "`prerequisite_missing_code`, `postcondition_code`) "
            + "SELECT 4, 5, 6, 7, 1 WHERE NOT COALESCE(@doka_sm_post_ok, FALSE);";

        var cleanupCommands = new List<string>(3 + baselineFragments.CleanupCommands.Count)
        {
            BuildGuardCleanupSql(),
        };

        // CreateScoped reverses cleanup input at the public boundary. Doka's
        // provider-rendered cleanup fragments already describe execution
        // order, so add them in reverse before the prepared-statement cleanup.
        for (var index = baselineFragments.CleanupCommands.Count - 1; index >= 0; index--)
        {
            cleanupCommands.Add(baselineFragments.CleanupCommands[index]);
        }

        cleanupCommands.Add(BuildPreparedStatementCleanupSql());

        var scopedCommand = MySqlMigrationCommandSpec.CreateScoped(
            setupCommands,
            bodyCommand,
            cleanupCommands,
            defaultSuppression);

        return MySqlMigrationOperationResult.Generated([scopedCommand], "safe_guarded_operation");
    }

    private static string BuildStateEvaluationAssignment(
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues
    )
    {
        var statement = "SELECT ("
            + runtimePlan.RenderPreparedStateExpression(renderedParameterValues)
            + "), COALESCE(("
            + runtimePlan.RenderPreparedRepairPrecondition(renderedParameterValues)
            + "), FALSE) INTO @doka_sm_state, @doka_sm_repair_ok";

        return "SET @doka_sm_sql = CASE WHEN @doka_sm_prerequisite_ok "
            + $"THEN CONVERT(0x{Convert.ToHexString(Encoding.UTF8.GetBytes(statement))} USING utf8mb4) "
            + "ELSE 'DO 0' END;";
    }

    private static string BuildPreparedSqlAssignment(
        string ddl
    ) => "SET @doka_sm_sql = CASE "
        + "WHEN @doka_sm_action IN ('apply', 'repair') "
        + $"THEN CONVERT(0x{Convert.ToHexString(Encoding.UTF8.GetBytes(ddl))} USING utf8mb4) "
        + "ELSE 'DO 0' END;";

    private static MySqlMigrationCommandSpec GetSingleBaselineCommand(
        IReadOnlyList<MySqlMigrationCommandSpec> baseline
    )
    {
        if (baseline.Count != 1)
        {
            throw new InvalidOperationException(
                "A SafeMigrations operation must render exactly one MySQL baseline command boundary.");
        }

        return baseline[0];
    }

    private static BaselineFragments GetBaselineFragments(
        MySqlMigrationCommandSpec command
    )
    {
        if (command.Fragments.Count == 0)
        {
            return new BaselineFragments([], NormalizePreparedBody(command.CommandText), []);
        }

        var setupCommands = new List<string>(command.Fragments.Count);
        var cleanupCommands = new List<string>(command.Fragments.Count);
        string? bodyCommand = null;

        foreach (var fragment in command.Fragments)
        {
            var commandText = fragment.CommandText.ToString();
            switch (fragment.Kind)
            {
                case MySqlMigrationCommandFragmentKind.Setup:
                    setupCommands.Add(commandText);
                    break;
                case MySqlMigrationCommandFragmentKind.Body when bodyCommand is null:
                    bodyCommand = NormalizePreparedBody(commandText);
                    break;
                case MySqlMigrationCommandFragmentKind.Body:
                    throw new InvalidOperationException(
                        "A provider-rendered MySQL baseline contains more than one body fragment.");
                case MySqlMigrationCommandFragmentKind.Cleanup:
                    cleanupCommands.Add(commandText);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"A provider-rendered MySQL baseline contains the unsupported fragment kind "
                        + $"'{fragment.Kind}'.");
            }
        }

        if (bodyCommand is null)
        {
            throw new InvalidOperationException("A provider-rendered MySQL baseline does not contain a body fragment.");
        }

        return new BaselineFragments(setupCommands, bodyCommand, cleanupCommands);
    }

    private static string NormalizePreparedBody(
        string commandText
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        var body = commandText
            .AsSpan()
            .Trim();

        if (body[^1] == ';')
        {
            body = body[..^1]
                .TrimEnd();
        }

        return body.IsEmpty
            ? throw new InvalidOperationException("The MySQL baseline body is empty.")
            : body.ToString();
    }

    private static string BuildPreparedStatementCleanupSql() => $"PREPARE {PreparedStatementName} FROM 'DO 0'; "
        + $"DEALLOCATE PREPARE {PreparedStatementName};";

    private static string BuildGuardCleanupSql() => "SET @doka_sm_state = NULL, @doka_sm_action = NULL, "
        + "@doka_sm_repair_ok = NULL, @doka_sm_prerequisite_ok = NULL, @doka_sm_sql = NULL, "
        + "@doka_sm_post_ok = NULL; "
        + "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`;";

    private IReadOnlyList<MySqlMigrationCommandSpec> RenderBaseline(
        SafeMigrationIntent intent,
        MySqlSafeMigrationRuntimePlan runtimePlan,
        MySqlMigrationOperationContext context
    )
    {
        if (runtimePlan.UnsupportedCode is not null)
        {
            return [Command("DO 0;", transactionSuppressed: true)];
        }

        if (intent is EnsureIndexIntent index
            && (index.Definition.Keys.Any(static key =>
                    key.Expression is not null || key.StructuredExpression is not null || key.PrefixLength is not null)
                || index.Definition.Method is not null))
        {
            return [Command(BuildCustomCreateIndexSql(index.Definition), transactionSuppressed: true)];
        }

        return context.RenderStandardOperation(
            SafeMigrationStandardOperationFactory.Create(
                intent,
                _expressionRenderer.Render,
                static collation => collation.Schema is null ? collation.Name : null));
    }

    private static string BuildActionAssignment(
        SafeMigrationOperation operation,
        SafeMigrationRepairCapability repairCapability
    )
    {
        ReadOnlySpan<SafeMigrationObservedState> states =
        [
            SafeMigrationObservedState.Missing,
            SafeMigrationObservedState.Matching,
            SafeMigrationObservedState.Different,
            SafeMigrationObservedState.Unsupported,
            SafeMigrationObservedState.DataBlocked,
            SafeMigrationObservedState.PrerequisiteMissing,
        ];

        var builder = new StringBuilder("SET @doka_sm_action = CASE @doka_sm_state ");

        // Materialize every planner state into SQL so runtime behavior stays
        // coupled to the provider-neutral decision table.
        foreach (var state in states)
        {
            var decision = SafeMigrationDecisionPlanner.Plan(
                operation.Intent.Kind,
                state,
                operation.Policy,
                repairCapability);

            builder
                .Append("WHEN '")
                .Append(StateCode(state))
                .Append("' THEN ");

            if (decision.Action == SafeMigrationAction.Repair)
            {
                builder
                    .Append("CASE WHEN COALESCE(@doka_sm_repair_ok, FALSE) ")
                    .Append("THEN 'repair' ELSE 'reject_different' END ");
            }
            else
            {
                builder
                    .Append('\'')
                    .Append(ActionCode(decision.Action))
                    .Append("' ");
            }
        }

        return builder
            .Append("ELSE 'reject_unsupported' END;")
            .ToString();
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
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static string BuildAssertionSetupSql() => "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`; "
        + "CREATE TEMPORARY TABLE `__doka_sm_assert` ("
        + "`different_code` TINYINT NOT NULL, "
        + "`unsupported_code` TINYINT NOT NULL, "
        + "`data_blocked_code` TINYINT NOT NULL, "
        + "`prerequisite_missing_code` TINYINT NOT NULL, "
        + "`postcondition_code` TINYINT NOT NULL, "
        + "CONSTRAINT `doka_sm_different` UNIQUE (`different_code`), "
        + "CONSTRAINT `doka_sm_unsupported` UNIQUE (`unsupported_code`), "
        + "CONSTRAINT `doka_sm_data_blocked` UNIQUE (`data_blocked_code`), "
        + "CONSTRAINT `doka_sm_prerequisite_missing` UNIQUE (`prerequisite_missing_code`), "
        + "CONSTRAINT `doka_sm_postcondition` UNIQUE (`postcondition_code`)"
        + "); "
        + "INSERT INTO `__doka_sm_assert` "
        + "(`different_code`, `unsupported_code`, `data_blocked_code`, "
        + "`prerequisite_missing_code`, `postcondition_code`) "
        + "VALUES (1, 1, 1, 1, 1);";

    private static string BuildDecisionAssertionSql() => "INSERT INTO `__doka_sm_assert` "
        + "(`different_code`, `unsupported_code`, `data_blocked_code`, "
        + "`prerequisite_missing_code`, `postcondition_code`) "
        + "SELECT 1, 2, 2, 2, 2 WHERE @doka_sm_action = 'reject_different' "
        + "UNION ALL SELECT 2, 1, 3, 3, 3 WHERE @doka_sm_action = 'reject_unsupported' "
        + "UNION ALL SELECT 3, 4, "
        + "CASE WHEN @doka_sm_action = 'reject_data_blocked' THEN 1 ELSE 5 END, "
        + "CASE WHEN @doka_sm_action = 'reject_prerequisite_missing' THEN 1 ELSE 5 END, 4 "
        + "WHERE @doka_sm_action IN ('reject_data_blocked', 'reject_prerequisite_missing');";

    private string Literal(
        string value
    ) => _stringMapping.GenerateSqlLiteral(value);

    private static MySqlMigrationCommandSpec Command(
        string sql,
        bool transactionSuppressed
    ) => MySqlMigrationCommandSpec.Create(sql, transactionSuppressed);

    private sealed record BaselineFragments(
        IReadOnlyList<string> SetupCommands,
        string BodyCommand,
        IReadOnlyList<string> CleanupCommands
    );
}
