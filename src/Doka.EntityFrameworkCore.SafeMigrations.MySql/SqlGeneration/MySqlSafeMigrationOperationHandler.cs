namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler : IMySqlMigrationOperationHandler
{
    private const string HandlerIdentifier = "Doka.EntityFrameworkCore.SafeMigrations.MySql.SafeMigrationOperation";
    private const string PreparedStatementName = "doka_sm_statement";
    private readonly MySqlSafeMigrationCatalogSqlBuilder _catalogSqlBuilder;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly RelationalTypeMapping _stringMapping;

    public MySqlSafeMigrationOperationHandler(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _catalogSqlBuilder = new MySqlSafeMigrationCatalogSqlBuilder(typeMappingSource, sqlGenerationHelper);
        _sqlGenerationHelper = sqlGenerationHelper;
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
        var baseline = RenderBaseline(operation.Intent, runtimePlan, context);
        var defaultSuppression = baseline[0].TransactionSuppressed;
        var commands = new List<MySqlMigrationCommandSpec>(24 + (baseline.Count * 4))
        {
            Command("DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`;", defaultSuppression),
            Command(CreateAssertionTableSql(), defaultSuppression),
            Command(
                "INSERT INTO `__doka_sm_assert` "
                + "(`different_code`, `unsupported_code`, `data_blocked_code`, `postcondition_code`) "
                + "VALUES (1, 1, 1, 1);",
                defaultSuppression),
            Command($"SET @doka_sm_state = ({runtimePlan.StateExpression});", defaultSuppression),
            Command($"SET @doka_sm_repair_ok = ({runtimePlan.RepairPrecondition});", defaultSuppression),
            Command(BuildActionAssignment(operation, runtimePlan.RepairCapability), defaultSuppression),
            Command(BuildAssertionSql("reject_different", "1, 2, 2, 2"), defaultSuppression),
            Command(BuildAssertionSql("reject_unsupported", "2, 1, 3, 3"), defaultSuppression),
            Command(BuildAssertionSql("reject_data_blocked", "3, 4, 1, 4"), defaultSuppression),
        };

        foreach (var command in baseline)
        {
            var ddl = MySqlPreparedStatementText.NormalizeProviderCommand(
                command.CommandText,
                HasBackslashDdlComment(operation.Intent));

            commands.Add(Command(BuildPreparedSqlAssignment(ddl), command.TransactionSuppressed));
            commands.Add(Command($"PREPARE {PreparedStatementName} FROM @doka_sm_sql;", command.TransactionSuppressed));
            commands.Add(Command($"EXECUTE {PreparedStatementName};", command.TransactionSuppressed));
            commands.Add(Command($"DEALLOCATE PREPARE {PreparedStatementName};", command.TransactionSuppressed));
        }

        commands.Add(
            Command($"SET @doka_sm_observed_postcondition = ({runtimePlan.Postcondition});", defaultSuppression));
        commands.Add(
            Command(
                "SET @doka_sm_post_ok = CASE "
                + "WHEN @doka_sm_action IN ('apply', 'repair') "
                + "THEN @doka_sm_observed_postcondition ELSE TRUE END;",
                defaultSuppression));
        commands.Add(
            Command(
                "INSERT INTO `__doka_sm_assert` "
                + "(`different_code`, `unsupported_code`, `data_blocked_code`, `postcondition_code`) "
                + "SELECT 4, 5, 6, 1 WHERE NOT COALESCE(@doka_sm_post_ok, FALSE);",
                defaultSuppression));
        commands.Add(Command("DROP TEMPORARY TABLE `__doka_sm_assert`;", defaultSuppression));
        commands.Add(
            Command(
                "SET @doka_sm_state = NULL, @doka_sm_action = NULL, "
                + "@doka_sm_repair_ok = NULL, @doka_sm_sql = NULL, "
                + "@doka_sm_observed_postcondition = NULL, @doka_sm_post_ok = NULL;",
                defaultSuppression));

        return MySqlMigrationOperationResult.Generated(commands, "safe_guarded_operation");
    }

    private string BuildPreparedSqlAssignment(
        PreparedStatementText ddl
    )
    {
        var selectedSql = ddl.IsSqlModeSensitive
            ? "CASE WHEN FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0 "
            + $"THEN {Literal(ddl.NoBackslashEscapesSql)} "
            + $"ELSE {Literal(ddl.DefaultSqlModeSql)} END"
            : Literal(ddl.NoBackslashEscapesSql);

        return "SET @doka_sm_sql = CASE "
            + "WHEN @doka_sm_action IN ('apply', 'repair') "
            + $"THEN {selectedSql} ELSE 'DO 0' END;";
    }

    private static bool HasBackslashDdlComment(
        SafeMigrationIntent intent
    ) => intent switch
    {
        EnsureTableIntent value => value.Definition.Comment?.Contains('\\') == true
            || value.Definition.Columns.Any(static column => column.Comment?.Contains('\\') == true),
        EnsureColumnIntent value => value.Definition.Comment?.Contains('\\') == true,
        AlterColumnIntent value => value.Definition.Comment?.Contains('\\') == true,
        _ => false,
    };

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
            && (index.Definition.Keys.Any(static key => key.Expression is not null || key.PrefixLength is not null)
                || index.Definition.Method is not null))
        {
            return [Command(BuildCustomCreateIndexSql(index.Definition), transactionSuppressed: true)];
        }

        return context.RenderStandardOperation(SafeMigrationStandardOperationFactory.Create(intent));
    }

    private static string BuildActionAssignment(
        SafeMigrationOperation operation,
        SafeMigrationRepairCapability repairCapability
    )
    {
        var states = new[]
        {
            SafeMigrationObservedState.Missing,
            SafeMigrationObservedState.Matching,
            SafeMigrationObservedState.Different,
            SafeMigrationObservedState.Unsupported,
            SafeMigrationObservedState.DataBlocked,
        };

        var builder = new StringBuilder("SET @doka_sm_action = CASE @doka_sm_state ");
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
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static string CreateAssertionTableSql() => "CREATE TEMPORARY TABLE `__doka_sm_assert` ("
        + "`different_code` TINYINT NOT NULL, "
        + "`unsupported_code` TINYINT NOT NULL, "
        + "`data_blocked_code` TINYINT NOT NULL, "
        + "`postcondition_code` TINYINT NOT NULL, "
        + "CONSTRAINT `doka_sm_different` UNIQUE (`different_code`), "
        + "CONSTRAINT `doka_sm_unsupported` UNIQUE (`unsupported_code`), "
        + "CONSTRAINT `doka_sm_data_blocked` UNIQUE (`data_blocked_code`), "
        + "CONSTRAINT `doka_sm_postcondition` UNIQUE (`postcondition_code`)"
        + ");";

    private static string BuildAssertionSql(
        string action,
        string values
    ) => "INSERT INTO `__doka_sm_assert` "
        + "(`different_code`, `unsupported_code`, `data_blocked_code`, `postcondition_code`) "
        + $"SELECT {values} WHERE @doka_sm_action = '{action}';";

    private string Literal(
        string value
    ) => _stringMapping.GenerateSqlLiteral(value);

    private static MySqlMigrationCommandSpec Command(
        string sql,
        bool transactionSuppressed
    ) => MySqlMigrationCommandSpec.Create(sql, transactionSuppressed);
}
