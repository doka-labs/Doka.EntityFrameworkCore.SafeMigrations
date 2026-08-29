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
        foreach (var operation in operations)
        {
            if (operation is not SafeMigrationOperation safeOperation)
            {
                commands.AddRange(_baselineGenerator.Generate([operation], model, options));
                continue;
            }

            var runtimePlan = _catalogSqlBuilder.Build(safeOperation);
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
        if (runtimePlan.UnsupportedCode is not null)
        {
            return [];
        }

        if (operation.Intent is EnsureIndexIntent index
            && RequiresCustomIndexSql(index.Definition))
        {
            var sqlOperation = new SqlOperation { Sql = BuildCustomCreateIndexSql(index.Definition) };
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

        var repairOperation = SafeMigrationStandardOperationFactory.CreateRepair(
            intent,
            _expressionRenderer.Render,
            static collation => collation.Schema is null ? collation.Name : null);

        return _baselineGenerator.Generate([repairOperation], model, options);
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
        var baselineSql = BuildBaselineSql(baseline);
        var repairSql = BuildBaselineSql(repairBaseline);
        var stateEvaluationGuardBranch = runtimePlan.StateEvaluationGuardFailureExpression is null
            ? string.Empty
            : "    ELSIF NOT COALESCE(("
            + runtimePlan.StateEvaluationGuardExpression
            + "), FALSE) THEN\n"
            + "        doka_state := ("
            + runtimePlan.StateEvaluationGuardFailureExpression
            + ");\n"
            + "        doka_repair_ok := FALSE;\n";

        var tag = SelectDollarTag(
            baselineSql,
            repairSql,
            runtimePlan.PrerequisiteExpression,
            runtimePlan.StateEvaluationGuardExpression,
            runtimePlan.StateEvaluationGuardFailureExpression ?? string.Empty,
            runtimePlan.StateExpression,
            runtimePlan.RepairPrecondition,
            runtimePlan.Postcondition);

        // The selected dollar tag cannot occur in embedded SQL, so provider
        // output cannot terminate the anonymous block accidentally.
        var builder = new StringBuilder()
            .Append("DO ")
            .Append(tag)
            .Append("\nDECLARE\n")
            .Append("    doka_state text;\n")
            .Append("    doka_action text;\n")
            .Append("    doka_repair_ok boolean;\n")
            .Append("BEGIN\n")
            .Append("    IF NOT COALESCE((")
            .Append(runtimePlan.PrerequisiteExpression)
            .Append("), FALSE) THEN\n")
            .Append("        doka_state := 'prerequisite_missing';\n")
            .Append("        doka_repair_ok := FALSE;\n")
            .Append(stateEvaluationGuardBranch)
            .Append("    ELSE\n")
            .Append("        doka_state := (")
            .Append(runtimePlan.StateExpression)
            .Append(");\n")
            .Append("        doka_repair_ok := COALESCE((")
            .Append(runtimePlan.RepairPrecondition)
            .Append("), FALSE);\n")
            .Append("    END IF;\n")
            .Append("    doka_action := ")
            .Append(BuildActionCase(operation, runtimePlan.RepairCapability))
            .Append(";\n")
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

        AppendActionSql(builder, baselineSql, indentation: "            ");

        builder.Append("        ELSE\n");
        AppendActionSql(builder, repairSql, indentation: "            ");

        builder
            .Append("        END IF;\n")
            .Append("        IF NOT COALESCE((\n")
            .Append("            ")
            .Append(runtimePlan.Postcondition)
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

    private static string BuildBaselineSql(
        IReadOnlyList<MigrationCommand> baseline
    )
    {
        var builder = new StringBuilder();
        for (var index = 0; index < baseline.Count; index++)
        {
            if (index > 0)
            {
                builder.Append('\n');
            }

            builder.Append(EnsureTerminated(baseline[index].CommandText));
        }

        return builder.ToString();
    }

    private static void AppendActionSql(
        StringBuilder builder,
        string sql,
        string indentation
    )
    {
        if (sql.Length == 0)
        {
            builder
                .Append(indentation)
                .Append("NULL;\n");

            return;
        }

        AppendIndentedLines(builder, sql, indentation);
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

    private static string EnsureTerminated(
        string sql
    ) => sql
        .TrimEnd()
        .EndsWith(';')
        ? sql.TrimEnd()
        : $"{sql.TrimEnd()};";

    private static string SelectDollarTag(
        params ReadOnlySpan<string> sqlParts
    )
    {
        for (var suffix = 0; ; suffix++)
        {
            var tag = suffix == 0 ? "$doka_safe_migration$" : $"$doka_safe_migration_{suffix}$";
            var collision = false;
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

    private static void AppendIndentedLines(
        StringBuilder builder,
        string sql,
        string indentation
    )
    {
        var start = 0;
        while (start <= sql.Length)
        {
            var newline = sql.IndexOf('\n', start);
            var length = newline < 0 ? sql.Length - start : newline - start;

            builder
                .Append(indentation)
                .Append(sql, start, length)
                .Append('\n');

            if (newline < 0)
            {
                return;
            }

            start = newline + 1;
        }
    }
}
