namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>
/// Generates guarded PostgreSQL SQL for the exact SafeMigrations envelope and
/// delegates every standard EF Core operation to Npgsql unchanged.
/// </summary>
public sealed partial class PostgreSqlSafeMigrationsSqlGenerator : IMigrationsSqlGenerator
{
    private readonly PostgreSqlSafeMigrationCatalogSqlBuilder _catalogSqlBuilder;
    private readonly NpgsqlMigrationsSqlGenerator _npgsqlGenerator;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;

    /// <summary>Initializes the composed SafeMigrations generator.</summary>
    public PostgreSqlSafeMigrationsSqlGenerator(
        NpgsqlMigrationsSqlGenerator npgsqlGenerator,
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(npgsqlGenerator);
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);
        _npgsqlGenerator = npgsqlGenerator;
        _sqlGenerationHelper = sqlGenerationHelper;
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
        foreach (var operation in operations)
        {
            if (operation is not SafeMigrationOperation safeOperation)
            {
                commands.AddRange(_npgsqlGenerator.Generate([operation], model, options));
                continue;
            }

            var runtimePlan = _catalogSqlBuilder.Build(safeOperation);
            var baseline = RenderBaseline(safeOperation.Intent, runtimePlan, model, options);
            if (baseline.Any(static command => command.TransactionSuppressed))
            {
                throw new NotSupportedException(
                    "A transaction-suppressed PostgreSQL baseline cannot be guarded inside a DO block.");
            }

            var guardedSql = BuildGuardedSql(safeOperation, runtimePlan, baseline);
            var sqlOperation = new SqlOperation { Sql = guardedSql };
            commands.AddRange(_npgsqlGenerator.Generate([sqlOperation], model, options));
        }

        return commands.AsReadOnly();
    }

    private IReadOnlyList<MigrationCommand> RenderBaseline(
        SafeMigrationIntent intent,
        PostgreSqlSafeMigrationRuntimePlan runtimePlan,
        IModel? model,
        MigrationsSqlGenerationOptions options
    )
    {
        if (runtimePlan.UnsupportedCode is not null)
        {
            return [];
        }

        if (intent is EnsureIndexIntent index
            && RequiresCustomIndexSql(index.Definition))
        {
            var operation = new SqlOperation { Sql = BuildCustomCreateIndexSql(index.Definition) };
            return _npgsqlGenerator.Generate([operation], model, options);
        }

        var standardOperation = SafeMigrationStandardOperationFactory.Create(intent);

        return _npgsqlGenerator.Generate([standardOperation], model, options);
    }

    private static string BuildGuardedSql(
        SafeMigrationOperation operation,
        PostgreSqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<MigrationCommand> baseline
    )
    {
        var baselineSql = string.Join(
            Environment.NewLine,
            baseline.Select(static command => EnsureTerminated(command.CommandText)));

        var tag = SelectDollarTag(
            baselineSql + runtimePlan.StateExpression + runtimePlan.RepairPrecondition + runtimePlan.Postcondition);

        var builder = new StringBuilder()
            .Append("DO ")
            .Append(tag)
            .AppendLine()
            .AppendLine("DECLARE")
            .AppendLine("    doka_state text;")
            .AppendLine("    doka_action text;")
            .AppendLine("    doka_repair_ok boolean;")
            .AppendLine("BEGIN")
            .Append("    doka_state := (")
            .Append(runtimePlan.StateExpression)
            .AppendLine(");")
            .Append("    doka_repair_ok := COALESCE((")
            .Append(runtimePlan.RepairPrecondition)
            .AppendLine("), FALSE);")
            .Append("    doka_action := ")
            .Append(BuildActionCase(operation, runtimePlan.RepairCapability))
            .AppendLine(";")
            .AppendLine("    IF doka_action = 'reject_different' THEN")
            .AppendLine("        RAISE EXCEPTION USING ERRCODE = 'P1001', MESSAGE = 'doka_sm_different';")
            .AppendLine("    ELSIF doka_action = 'reject_unsupported' THEN")
            .AppendLine("        RAISE EXCEPTION USING ERRCODE = 'P1002', MESSAGE = 'doka_sm_unsupported';")
            .AppendLine("    ELSIF doka_action = 'reject_data_blocked' THEN")
            .AppendLine("        RAISE EXCEPTION USING ERRCODE = 'P1003', MESSAGE = 'doka_sm_data_blocked';")
            .AppendLine("    ELSIF doka_action IN ('apply', 'repair') THEN");

        if (baseline.Count == 0)
        {
            builder.AppendLine("        NULL;");
        }
        else
        {
            foreach (var line in baselineSql.Split(Environment.NewLine, StringSplitOptions.None))
            {
                builder
                    .Append("        ")
                    .AppendLine(line);
            }
        }

        builder
            .AppendLine("        IF NOT COALESCE((")
            .Append("            ")
            .Append(runtimePlan.Postcondition)
            .AppendLine()
            .AppendLine("        ), FALSE) THEN")
            .AppendLine("            RAISE EXCEPTION USING ERRCODE = 'P1004', MESSAGE = 'doka_sm_postcondition';")
            .AppendLine("        END IF;")
            .AppendLine("    END IF;")
            .AppendLine("END")
            .Append(tag)
            .Append(';');

        return builder.ToString();
    }

    private static string BuildActionCase(
        SafeMigrationOperation operation,
        SafeMigrationRepairCapability repairCapability
    )
    {
        var builder = new StringBuilder("CASE doka_state ");
        foreach (var state in Enum.GetValues<SafeMigrationObservedState>())
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
                    .Append("CASE WHEN doka_repair_ok THEN 'repair' ")
                    .Append("ELSE 'reject_different' END ");
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
            .Append("ELSE 'reject_unsupported' END")
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

    private static string EnsureTerminated(
        string sql
    ) => sql
        .TrimEnd()
        .EndsWith(';')
        ? sql.TrimEnd()
        : $"{sql.TrimEnd()};";

    private static string SelectDollarTag(
        string sql
    )
    {
        for (var suffix = 0;; suffix++)
        {
            var tag = suffix == 0 ? "$doka_safe_migration$" : $"$doka_safe_migration_{suffix}$";
            if (!sql.Contains(tag, StringComparison.Ordinal))
            {
                return tag;
            }
        }
    }
}
