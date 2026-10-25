namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerGuardRenderingAllocationTests
{
    /// <summary>Independently renders policy gating using the former action string and scalar wrappers.</summary>
    private static StringBuilder OriginalGuardBody(
        SafeMigrationOperation operation,
        SqlServerSafeMigrationRuntimePlan plan,
        IReadOnlyList<MigrationCommand> baseline,
        IReadOnlyList<MigrationCommand> repair,
        int? ordinal = null
    )
    {
        var actionCase = OriginalActionCase(operation, plan.RepairCapability);
        var diagnostic = new SqlServerSafeMigrationRuntimeDiagnostic(operation, ordinal);
        var builder = new StringBuilder(1024);
        if (operation.Intent is EnsureModelManagedDataIntent)
        {
            builder.Append("-- Script clients must recover IDENTITY_INSERT or close this session "
                + "after cancellation/timeout.\n");
        }

        builder.Append("IF COALESCE(")
            .Append("HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION')")
            .Append(", 0) <> 1\nBEGIN\n    ");
        diagnostic.AppendThrow(builder, 51002, "doka_sm_unsupported", "catalog_metadata_not_visible");
        builder.Append("\nEND;\n");

        if (plan.PhysicalTableSupportExpression is { } physical)
        {
            builder.Append("IF COALESCE((").Append(physical)
                .Append("), 0) <> 1\nBEGIN\n    ");
            diagnostic.AppendThrow(builder, 51002, "doka_sm_unsupported",
                SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode);
            builder.Append("\nEND;\n");
        }

        if (plan.ColumnLayoutFailureExpression is { } layout)
        {
            builder.Append("DECLARE @doka_layout_failure nvarchar(64) = (").Append(layout)
                .Append(");\nIF @doka_layout_failure IS NOT NULL\nBEGIN\n")
                .Append("    DECLARE @doka_layout_message nvarchar(2048) = CASE @doka_layout_failure\n")
                .Append("        WHEN N'column_limit' THEN ");
            diagnostic.AppendLiteral(builder, "doka_sm_unsupported", "column_limit");
            builder.Append("\n        WHEN N'column_fixed_row_limit' THEN ");
            diagnostic.AppendLiteral(builder, "doka_sm_unsupported", "column_fixed_row_limit");
            builder.Append("\n        ELSE ");
            diagnostic.AppendLiteral(builder, "doka_sm_unsupported", "column_layout_unproven");
            builder.Append(" END;\n    THROW 51002, @doka_layout_message, 1;\nEND;\n");
        }

        if (plan.ColumnCollationSupportExpression is { } collation)
        {
            builder.Append("IF COALESCE((").Append(collation)
                .Append("), 0) <> 1\nBEGIN\n    ");
            diagnostic.AppendThrow(builder, 51002, "doka_sm_unsupported",
                SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode);
            builder.Append("\nEND;\n");
        }

        if (plan.DefaultValueSupportExpression is { } defaultValue)
        {
            if (plan.DefaultValueSupportRequiresDelayedBinding)
            {
                builder.Append("DECLARE @doka_default_supported int;\n");
                OriginalScalar(builder, defaultValue, "int", "@doka_default_supported", string.Empty);
                defaultValue = "@doka_default_supported";
            }

            builder.Append("IF COALESCE((").Append(defaultValue)
                .Append("), 0) <> 1\nBEGIN\n    ");
            diagnostic.AppendThrow(builder, 51002, "doka_sm_unsupported",
                SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode);
            builder.Append("\nEND;\n");
        }

        if (plan.IndexFilterSupportExpression is { } filter)
        {
            builder.Append("IF COALESCE((").Append(filter)
                .Append("), 0) <> 1\nBEGIN\n    ");
            diagnostic.AppendThrow(builder, 51002, "doka_sm_unsupported",
                SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode);
            builder.Append("\nEND;\n");
        }

        // WHY: Both fixture intents have a null schema, so their original guard
        // requires the dbo admission boundary before evaluating prepared state.
        builder.Append("IF COALESCE(SCHEMA_NAME(), N'') <> N'dbo'\nBEGIN\n    ");
        diagnostic.AppendThrow(builder, 51004, "doka_sm_prerequisite_missing", "default_schema_mismatch");
        builder.Append("\nEND;\n")
            .Append("DECLARE @doka_state nvarchar(32);\n")
            .Append("DECLARE @doka_action nvarchar(32);\n")
            .Append("DECLARE @doka_repair_ok int;\n")
            .Append("IF COALESCE((").Append(plan.PrerequisiteExpression).Append("), 0) <> 1\n")
            .Append("BEGIN\n    SET @doka_state = N'prerequisite_missing';\n")
            .Append("    SET @doka_repair_ok = 0;\nEND\nELSE IF COALESCE((")
            .Append(plan.StateEvaluationGuardExpression)
            .Append("), 0) <> 1\nBEGIN\n")
            .Append("    SET @doka_state = (")
            .Append(plan.StateEvaluationGuardFailureExpression ?? "N'unsupported'")
            .Append(");\n    SET @doka_repair_ok = 0;\nEND\nELSE\nBEGIN\n");

        if (plan.RequiresDelayedBinding || plan.CatalogPreambleSql is not null)
        {
            OriginalScalar(builder, plan.StateExpression, "nvarchar(32)", "@doka_state", "    ",
                plan.CatalogPreambleSql);

        }
        else
        {
            builder.Append("    SET @doka_state = (").Append(plan.StateExpression).Append(");\n");
        }

        if (operation.Policy == SafeMigrationPolicy.RepairIfSafe
            && plan.RepairCapability == SafeMigrationRepairCapability.Safe)
        {
            builder.Append("    IF @doka_state = N'different'\n    BEGIN\n");
            if (plan.RequiresDelayedBinding
                || plan.CatalogPreambleSql is not null)
            {
                OriginalScalar(builder, $"COALESCE(({plan.RepairPrecondition}), 0)", "int",
                    "@doka_repair_ok", "        ");
            }
            else
            {
                builder.Append("        SET @doka_repair_ok = COALESCE((")
                    .Append(plan.RepairPrecondition).Append("), 0);\n");
            }

            builder.Append("    END\n    ELSE\n    BEGIN\n        SET @doka_repair_ok = 0;\n    END;\n");
        }
        else
        {
            builder.Append("    SET @doka_repair_ok = 0;\n");
        }

        builder.Append("END;\n")
            .Append("SET @doka_action = ").Append(actionCase).Append(";\n")
            .Append("IF @doka_action = N'reject_different'\nBEGIN\n    ");
        diagnostic.AppendThrow(builder, 51001, "doka_sm_different");
        builder.Append("\nEND;\nIF @doka_action = N'reject_unsupported'\nBEGIN\n    ");
        diagnostic.AppendThrow(builder, 51002, "doka_sm_unsupported", plan.UnsupportedCode);
        builder.Append("\nEND;\nIF @doka_action = N'reject_data_blocked'\nBEGIN\n    ");
        diagnostic.AppendThrow(builder, 51003, "doka_sm_data_blocked");
        builder.Append("\nEND;\nIF @doka_action = N'reject_prerequisite_missing'\nBEGIN\n    ");
        diagnostic.AppendThrow(builder, 51004, "doka_sm_prerequisite_missing");
        builder.Append("\nEND;\nIF @doka_action = N'apply'\nBEGIN\n");

        OriginalCommands(builder, baseline);
        builder.Append("END\nELSE IF @doka_action = N'repair'\nBEGIN\n");
        OriginalCommands(builder, repair);
        builder.Append("END;\n")
            .Append("IF @doka_action IN (N'apply', N'repair')\nBEGIN\n");
        if (plan.PostApplySql is { Length: > 0 } postApply)
        {
            OriginalDynamic(builder, postApply, "    ");
        }

        if (plan.RequiresDelayedBinding || plan.CatalogPreambleSql is not null)
        {
            builder.Append("    DECLARE @doka_postcondition int;\n");
            OriginalScalar(builder, $"COALESCE(({plan.ExecutionPostcondition ?? plan.Postcondition}), 0)",
                "int", "@doka_postcondition", "    ");

            builder.Append("    IF @doka_postcondition <> 1\n");
        }
        else
        {
            builder.Append("    IF COALESCE((")
                .Append(plan.ExecutionPostcondition ?? plan.Postcondition)
                .Append("), 0) <> 1\n");
        }

        builder.Append("    BEGIN\n        ");
        diagnostic.AppendThrow(builder, 51005, "doka_sm_postcondition");
        builder.Append("\n    END;\nEND;");

        if (operation.Intent is EnsureModelManagedDataIntent)
        {
            builder.Append('\n').Append(SqlServerSafeMigrationIdentityInsertScope.ReadyCompletionSql());
        }

        return builder;
    }

    /// <summary>Retains the original intermediate action string and its complete policy decision mapping.</summary>
    private static string OriginalActionCase(
        SafeMigrationOperation operation,
        SafeMigrationRepairCapability repairCapability
    )
    {
        var builder = new StringBuilder("CASE @doka_state ");
        foreach (var state in s_states)
        {
            var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, state,
                operation.Policy, repairCapability);

            var stateCode = state switch
            {
                SafeMigrationObservedState.Missing => "missing",
                SafeMigrationObservedState.Matching => "matching",
                SafeMigrationObservedState.Different => "different",
                SafeMigrationObservedState.Unsupported => "unsupported",
                SafeMigrationObservedState.DataBlocked => "data_blocked",
                SafeMigrationObservedState.PrerequisiteMissing => "prerequisite_missing",
                SafeMigrationObservedState.TransitionReady => "transition_ready",
                _ => throw new UnreachableException(),
            };

            builder.Append("WHEN N'").Append(stateCode).Append("' THEN ");
            if (decision.Action == SafeMigrationAction.Repair)
            {
                builder.Append("CASE WHEN @doka_repair_ok = 1 THEN N'repair' ")
                    .Append("ELSE N'reject_different' END ");
            }
            else
            {
                var actionCode = decision.Action switch
                {
                    SafeMigrationAction.Apply => "apply",
                    SafeMigrationAction.NoOp => "no_op",
                    SafeMigrationAction.RejectDifferent => "reject_different",
                    SafeMigrationAction.RejectUnsupported => "reject_unsupported",
                    SafeMigrationAction.RejectDataBlocked => "reject_data_blocked",
                    SafeMigrationAction.RejectPrerequisiteMissing => "reject_prerequisite_missing",
                    _ => throw new UnreachableException(),
                };

                builder.Append("N'").Append(actionCode).Append("' ");
            }
        }

        return builder.Append("ELSE N'reject_unsupported' END").ToString();
    }

    /// <summary>
    /// Retains the original scalar interpolation and streamed literal work for allocation comparison.
    /// </summary>
    private static void OriginalScalar(
        StringBuilder builder,
        string expression,
        string type,
        string target,
        string indentation,
        string? preamble = null
    )
    {
        ArgumentNullException.ThrowIfNull(expression);
        builder.Append(indentation).Append("EXEC sys.sp_executesql N'");
        OriginalEscape(builder, preamble.AsSpan());
        builder.Append("\nSET @doka_value = (");
        OriginalEscape(builder, expression.AsSpan());
        builder.Append(");', N'@doka_value ").Append(type)
            .Append(" OUTPUT', @doka_value = ").Append(target).Append(" OUTPUT;\n");
    }

    /// <summary>Retains each deferred DDL scope and the empty-command assignment from the original guard.</summary>
    private static void OriginalCommands(
        StringBuilder builder,
        IReadOnlyList<MigrationCommand> commands
    )
    {
        if (commands.Count == 0)
        {
            builder.Append("    SET @doka_repair_ok = @doka_repair_ok;\n");

            return;
        }

        foreach (var command in commands)
        {
            OriginalDynamic(builder, command.CommandText, "    ");
        }
    }

    /// <summary>Retains the original required dynamic-command validation and literal rendering work.</summary>
    private static void OriginalDynamic(
        StringBuilder builder,
        string sql,
        string indentation
    )
    {
        ArgumentNullException.ThrowIfNull(sql);
        builder.Append(indentation).Append("EXEC sys.sp_executesql N'");
        OriginalEscape(builder, sql.AsSpan());
        builder.Append("';\n");
    }

    /// <summary>Retains streamed escaping so the comparison adds no artificial replaced-string allocations.</summary>
    private static void OriginalEscape(
        StringBuilder builder,
        ReadOnlySpan<char> sql
    )
    {
        int offset;
        while ((offset = sql.IndexOf('\'')) >= 0)
        {
            builder.Append(sql.Slice(0, offset)).Append("''");
            sql = sql.Slice(offset + 1);
        }

        builder.Append(sql);
    }

    /// <summary>Retains the removed complete identifier-body join and streamed growing literal buffer.</summary>
    private static string OriginalIdentifierScope(
        IReadOnlyList<string> statements
    )
    {
        var body = string.Join("\n", statements);
        var builder = new StringBuilder();
        OriginalDynamic(builder, body, string.Empty);

        return builder.ToString();
    }

    /// <summary>Checks final outer quoting independently from the product's exact-size copy implementation.</summary>
    private static string ReferenceScope(
        string body
    ) => "EXEC sys.sp_executesql N'" + body.Replace("'", "''", StringComparison.Ordinal) + "';\n";
}
