namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

public sealed partial class SqlServerSafeMigrationsSqlGenerator
{
    /// <summary>Protects a complete command from EF's line and history transformations.</summary>
    /// <param name="sql">The completed isolated command, including nested literals and expressions.</param>
    /// <param name="options">The caller's actual EF generation mode.</param>
    /// <returns>The same command when safe; otherwise a single-line private transport scope.</returns>
    /// <exception cref="ArgumentNullException">The completed command is null.</exception>
    internal static string ProtectCommandSql(
        string sql,
        MigrationsSqlGenerationOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sql);

        if ((options & MigrationsSqlGenerationOptions.Idempotent) == 0 && !ContainsLineSensitiveText(sql.AsSpan()))
        {
            return sql;
        }

        // WHY: EF indents idempotent commands and also normalizes SqlOperation
        // lines and removes backslash continuations in runtime/normal commands.
        // Reconstruct the complete UTF-16 payload on one physical line, not
        // only its DDL, so defaults and catalog comparisons remain identical.
        // The private scope prevents variable collisions across migrations;
        // MAX must be the first operand to avoid 4000-character truncation.
        var builder = new StringBuilder(sql.Length);
        builder.Append("DECLARE @doka_script_payload nvarchar(max) = CAST(N'' AS nvarchar(max)) + N'");
        var remaining = sql.AsSpan();
        int newlineOffset;
        while ((newlineOffset = remaining.IndexOfAny('\r', '\n')) >= 0)
        {
            AppendEscapedSqlLiteral(builder, remaining.Slice(0, newlineOffset));
            builder.Append(remaining[newlineOffset] == '\r' ? "' + NCHAR(13) + N'" : "' + NCHAR(10) + N'");
            remaining = remaining.Slice(newlineOffset + 1);
        }

        AppendEscapedSqlLiteral(builder, remaining);
        builder.Append("'; EXEC sys.sp_executesql @doka_script_payload;");

        return BuildIsolatedGuardSql(builder);
    }

    /// <summary>Finds line shapes EF or a script client can transform without interpreting authored SQL.</summary>
    /// <param name="sql">The completed command whose unchanged reference is preferred when safe.</param>
    /// <returns>Whether a conservative lossless transport is necessary before provider command generation.</returns>
    private static bool ContainsLineSensitiveText(ReadOnlySpan<char> sql)
    {
        // WHY: EF splits CRLF/LF and joins with the platform newline. Preserve
        // every CR conservatively, and any LF on a CRLF platform. The bounded
        // span scan allocates nothing and deliberately does not grow a second
        // SQL lexer merely to identify safe transport, not SQL admissibility.
        if (sql.Contains('\r') || (Environment.NewLine != "\n" && sql.Contains('\n')))
        {
            return true;
        }

        while (!sql.IsEmpty)
        {
            var newline = sql.IndexOf('\n');
            var line = (newline < 0 ? sql : sql.Slice(0, newline)).TrimStart();
            if ((newline > 0 && sql[newline - 1] == '\\')
                || (line.StartsWith("GO", StringComparison.OrdinalIgnoreCase)
                    && (line.Length == 2 || (line.Length > 2 && char.IsWhiteSpace(line[2])))))
            {
                return true;
            }

            if (newline < 0)
            {
                break;
            }

            sql = sql.Slice(newline + 1);
        }

        return false;
    }

    /// <summary>Renders one prepared operation inside its private dynamic scope.</summary>
    /// <param name="operation">The safe intent and policy whose action is guarded.</param>
    /// <param name="plan">The prepared catalog and execution expressions.</param>
    /// <param name="baseline">The provider commands for an accepted apply action.</param>
    /// <param name="repairBaseline">The provider commands for an accepted repair action.</param>
    /// <param name="scriptIdentityScope">
    /// Whether SQL-only execution owns its own marker instead of client bootstrap.
    /// </param>
    /// <param name="operationOrdinal">The known stream ordinal, or null for a prepared-plan rendering caller.</param>
    /// <returns>The complete isolated guard with deferred scalar and DDL scopes intact.</returns>
    internal static string BuildGuardedSql(
        SafeMigrationOperation operation,
        SqlServerSafeMigrationRuntimePlan plan,
        IReadOnlyList<MigrationCommand> baseline,
        IReadOnlyList<MigrationCommand> repairBaseline,
        bool scriptIdentityScope = false,
        int? operationOrdinal = null
    )
    {
        var builder = new StringBuilder(1024);
        var diagnostic = new SqlServerSafeMigrationRuntimeDiagnostic(operation, operationOrdinal);

        if (operation.Intent is EnsureModelManagedDataIntent)
        {
            builder.Append(
                "-- Script clients must recover IDENTITY_INSERT or close this session "
                + "after cancellation/timeout.\n");
        }

        // WHY: SQL Server hides catalog metadata the caller cannot view. A
        // missing catalog row is not evidence that the object is absent.
        builder
            .Append("IF COALESCE(")
            .Append(MetadataVisibilityExpression)
            .Append(", 0) <> 1\nBEGIN\n    ");

        diagnostic.AppendThrow(builder, 51002, "doka_sm_unsupported", "catalog_metadata_not_visible");
        builder.Append("\nEND;\n");

        if (plan.PhysicalTableSupportExpression is { } physicalTableSupport)
        {
            // WHY: Engine-specific tables can look structurally ordinary but
            // require different DDL and DML. Reject before any row expression
            // is bound or a caller policy can authorize a baseline command.
            builder
                .Append("IF COALESCE((")
                .Append(physicalTableSupport)
                .Append("), 0) <> 1\nBEGIN\n    ");
            diagnostic.AppendThrow(
                builder,
                51002,
                "doka_sm_unsupported",
                SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode);
            builder.Append("\nEND;\n");
        }

        if (plan.ColumnLayoutFailureExpression is { } columnLayoutFailure)
        {
            // WHY: An absent column consumes a new slot and fixed row storage.
            // Reject an unproven layout using metadata before typed constants,
            // row probes, caller policy, or baseline DDL can bind or execute.
            // WHY: The existing predicate already computes the exact capacity
            // failure. Capture it once, then select failure-only constant text;
            // diagnostics must neither repeat its catalog read nor claim that
            // a proven capacity limit is merely an unknown layout.
            builder
                .Append("DECLARE @doka_layout_failure nvarchar(64) = (")
                .Append(columnLayoutFailure)
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

        if (plan.ColumnCollationSupportExpression is { } columnCollationSupport)
        {
            // WHY: SQL Server binds an authored COLLATE name before a skipped
            // IF branch executes. Prove it exists before delayed conversion
            // or baseline SQL can compile that physical name.
            builder
                .Append("IF COALESCE((")
                .Append(columnCollationSupport)
                .Append("), 0) <> 1\nBEGIN\n    ");
            diagnostic.AppendThrow(
                builder,
                51002,
                "doka_sm_unsupported",
                SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode);
            builder.Append("\nEND;\n");
        }

        if (plan.DefaultValueSupportExpression is { } defaultValueSupport)
        {
            // WHY: A known temporal/constant expression can still exceed the
            // destination domain. Prove conversion before prerequisites, row
            // binding, policy evaluation, and baseline default creation.
            if (plan.DefaultValueSupportRequiresDelayedBinding)
            {
                builder.Append("DECLARE @doka_default_supported int;\n");
                AppendDelayedScalar(builder, defaultValueSupport, "int", "@doka_default_supported", string.Empty);
                defaultValueSupport = "@doka_default_supported";
            }

            builder
                .Append("IF COALESCE((")
                .Append(defaultValueSupport)
                .Append("), 0) <> 1\nBEGIN\n    ");
            diagnostic.AppendThrow(
                builder,
                51002,
                "doka_sm_unsupported",
                SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode);
            builder.Append("\nEND;\n");
        }

        if (plan.IndexFilterSupportExpression is { } indexFilterSupport)
        {
            // WHY: A valid predicate shape can still require a forbidden
            // column-side conversion or an invalid constant. Qualify only
            // catalog metadata and constants before binding any row filter.
            builder
                .Append("IF COALESCE((")
                .Append(indexFilterSupport)
                .Append("), 0) <> 1\nBEGIN\n    ");
            diagnostic.AppendThrow(
                builder,
                51002,
                "doka_sm_unsupported",
                SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode);
            builder.Append("\nEND;\n");
        }

        if (UsesDefaultSchema(operation.Intent))
        {
            // WHY: EF's unqualified DDL targets the caller's default schema.
            // Catalog classification uses dbo, so prohibit a different default
            // before any baseline command can run.
            builder.Append("IF COALESCE(SCHEMA_NAME(), N'') <> N'dbo'\nBEGIN\n    ");
            diagnostic.AppendThrow(builder, 51004, "doka_sm_prerequisite_missing", "default_schema_mismatch");
            builder.Append("\nEND;\n");
        }

        builder
            .Append("DECLARE @doka_state nvarchar(32);\n")
            .Append("DECLARE @doka_action nvarchar(32);\n")
            .Append("DECLARE @doka_repair_ok int;\n")
            .Append("IF COALESCE((")
            .Append(plan.PrerequisiteExpression)
            .Append("), 0) <> 1\n")
            .Append("BEGIN\n    SET @doka_state = N'prerequisite_missing';\n")
            .Append("    SET @doka_repair_ok = 0;\nEND\nELSE IF COALESCE((")
            .Append(plan.StateEvaluationGuardExpression)
            .Append("), 0) <> 1\nBEGIN\n")
            .Append("    SET @doka_state = (")
            .Append(plan.StateEvaluationGuardFailureExpression ?? "N'unsupported'")
            .Append(");\n    SET @doka_repair_ok = 0;\nEND\n");

        builder.Append("ELSE\nBEGIN\n");

        if (plan.RequiresDelayedBinding
            || plan.CatalogPreambleSql is not null)
        {
            // WHY: A data-row expression can reference a table created earlier
            // in this operation stream. Delay its compilation until the catalog
            // prerequisite has proved the table exists.
            AppendDelayedScalar(
                builder,
                plan.StateExpression,
                "nvarchar(32)",
                "@doka_state",
                "    ",
                plan.CatalogPreambleSql);
        }
        else
        {
            builder
                .Append("    SET @doka_state = (")
                .Append(plan.StateExpression)
                .Append(");\n");
        }

        // WHY: Only Different can consume repair evidence. A Matching replay,
        // a non-repair policy, or a plan without safe repair must not compile
        // and execute another catalog/data scope whose result is discarded.
        // Classification and all prerequisite/support gates remain fresh.
        if (operation.Policy == SafeMigrationPolicy.RepairIfSafe
            && plan.RepairCapability == SafeMigrationRepairCapability.Safe)
        {
            builder.Append("    IF @doka_state = N'different'\n    BEGIN\n");
            if (plan.RequiresDelayedBinding
                || plan.CatalogPreambleSql is not null)
            {
                AppendDelayedScalar(
                    builder,
                    plan.RepairPrecondition,
                    "int",
                    "@doka_repair_ok",
                    "        ",
                    coalesce: true);
            }
            else
            {
                builder
                    .Append("        SET @doka_repair_ok = COALESCE((")
                    .Append(plan.RepairPrecondition)
                    .Append("), 0);\n");
            }

            builder.Append("    END\n    ELSE\n    BEGIN\n        SET @doka_repair_ok = 0;\n    END;\n");
        }
        else
        {
            builder.Append("    SET @doka_repair_ok = 0;\n");
        }

        builder.Append("END;\nSET @doka_action = ");
        AppendActionCase(builder, operation, plan.RepairCapability);

        builder.Append(";\nIF @doka_action = N'reject_different'\nBEGIN\n    ");
        diagnostic.AppendThrow(builder, 51001, "doka_sm_different");
        builder.Append("\nEND;\nIF @doka_action = N'reject_unsupported'\nBEGIN\n    ");
        diagnostic.AppendThrow(builder, 51002, "doka_sm_unsupported", plan.UnsupportedCode);
        builder.Append("\nEND;\nIF @doka_action = N'reject_data_blocked'\nBEGIN\n    ");
        diagnostic.AppendThrow(builder, 51003, "doka_sm_data_blocked");
        builder.Append("\nEND;\nIF @doka_action = N'reject_prerequisite_missing'\nBEGIN\n    ");
        diagnostic.AppendThrow(builder, 51004, "doka_sm_prerequisite_missing");
        builder.Append("\nEND;\nIF @doka_action = N'apply'\nBEGIN\n");

        AppendDynamicCommands(builder, baseline);

        builder.Append("END\nELSE IF @doka_action = N'repair'\nBEGIN\n");
        AppendDynamicCommands(builder, repairBaseline);

        builder
            .Append("END;\n")
            .Append("IF @doka_action IN (N'apply', N'repair')\nBEGIN\n");

        if (plan.PostApplySql is { Length: > 0 } postApplySql)
        {
            // WHY: A provider-owned provenance mark belongs to the newly
            // created physical object. Write it only after DDL succeeds and
            // before the final catalog postcondition validates that object.
            AppendDynamicSql(builder, postApplySql, "    ");
        }

        if (plan.RequiresDelayedBinding
            || plan.CatalogPreambleSql is not null)
        {
            builder.Append("    DECLARE @doka_postcondition int;\n");

            AppendDelayedScalar(
                builder,
                plan.ExecutionPostcondition ?? plan.Postcondition,
                "int",
                "@doka_postcondition",
                "    ",
                plan.PostconditionPreambleSql,
                coalesce: true);

            builder.Append("    IF @doka_postcondition <> 1\n");
        }
        else
        {
            builder
                .Append("    IF COALESCE((")
                .Append(plan.ExecutionPostcondition ?? plan.Postcondition)
                .Append("), 0) <> 1\n");
        }

        builder.Append("    BEGIN\n        ");
        diagnostic.AppendThrow(builder, 51005, "doka_sm_postcondition");
        builder.Append("\n    END;\nEND;");

        if (operation.Intent is EnsureModelManagedDataIntent
            && !scriptIdentityScope)
        {
            builder
                .Append('\n')
                .Append(SqlServerSafeMigrationIdentityInsertScope.ReadyCompletionSql());
        }

        // WHY: BEGIN/END does not scope T-SQL variables. EF script generation
        // can concatenate operations and separate Generate calls in one batch,
        // so the complete guard needs its own dynamic scope, not ordinal names.
        // Existing nested scopes still defer row and DDL binding until their
        // prerequisite gates succeed; exceptions and transactions flow outward.

        return BuildIsolatedGuardSql(builder);
    }

    /// <summary>Copies a completed guard into its exact-sized isolated SQL batch without mutating the source.</summary>
    /// <param name="body">The operation-owned buffer, which must not be concurrently modified.</param>
    /// <returns>The original guard with SQL literal quoting and one private dynamic scope.</returns>
    /// <exception cref="ArgumentNullException">The source buffer is null.</exception>
    /// <exception cref="OverflowException">The isolated batch length exceeds the string length domain.</exception>
    internal static string BuildIsolatedGuardSql(
        StringBuilder body
    )
    {
        ArgumentNullException.ThrowIfNull(body);

        var apostrophes = 0;
        foreach (var chunk in body.GetChunks())
        {
            apostrophes += chunk.Span.Count('\'');
        }

        var length = checked(body.Length + apostrophes + GuardScopePrefix.Length + GuardScopeSuffix.Length);

        // WHY: Replace/Insert expands mutable builder chunks before the final
        // string allocation. Copy once into that final allocation and expand
        // quotes backward, so no unread source character is overwritten and
        // neither an intermediate flattened string nor a rented buffer is needed.
        return string.Create(
            length,
            body,
            static (destination, source) =>
            {
                source.CopyTo(0, destination.Slice(GuardScopePrefix.Length, source.Length), source.Length);
                var sourceOffset = GuardScopePrefix.Length + source.Length;
                var destinationOffset = destination.Length - GuardScopeSuffix.Length;
                while (sourceOffset > GuardScopePrefix.Length)
                {
                    var character = destination[--sourceOffset];
                    destination[--destinationOffset] = character;
                    if (character == '\'')
                    {
                        destination[--destinationOffset] = character;
                    }
                }

                GuardScopePrefix
                    .AsSpan()
                    .CopyTo(destination);
                GuardScopeSuffix
                    .AsSpan()
                    .CopyTo(destination.Slice(destination.Length - GuardScopeSuffix.Length));
            });
    }

    /// <summary>Copies completed identifier statements directly into one exact-sized private SQL scope.</summary>
    /// <param name="statements">The ordered provider-owned statements, which must not be concurrently modified.</param>
    /// <returns>The newline-joined statements with the original literal quoting and outer dynamic scope.</returns>
    /// <exception cref="ArgumentNullException">The statement collection is null.</exception>
    /// <exception cref="OverflowException">The isolated batch length exceeds the string length domain.</exception>
    internal static string BuildIsolatedIdentifierGuardSql(
        IReadOnlyList<string> statements
    )
    {
        ArgumentNullException.ThrowIfNull(statements);

        var length = checked(GuardScopePrefix.Length + GuardScopeSuffix.Length + Math.Max(0, statements.Count - 1));

        // WHY: Indexing keeps the exact-size pass free of interface-enumerator allocations.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index].AsSpan();
            length = checked(length + statement.Length + statement.Count('\''));
        }

        // WHY: The catalog builder already owns complete immutable statements.
        // Joining and then copying them through a growing literal buffer retains
        // two unnecessary full-size intermediates before the final SQL string.
        return string.Create(
            length,
            statements,
            static (destination, source) =>
            {
                GuardScopePrefix
                    .AsSpan()
                    .CopyTo(destination);

                var offset = GuardScopePrefix.Length;
                // WHY: Place newlines only between statements in the exact-sized SQL destination.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < source.Count; index++)
                {
                    if (index > 0)
                    {
                        destination[offset++] = '\n';
                    }

                    // WHY: string.Join treats null statement entries as empty.
                    // Preserve that boundary as well as every other UTF-16 code unit.
                    var statement = source[index]
                        .AsSpan();
                    int apostropheOffset;
                    while ((apostropheOffset = statement.IndexOf('\'')) >= 0)
                    {
                        statement
                            .Slice(0, apostropheOffset)
                            .CopyTo(destination.Slice(offset));

                        offset += apostropheOffset;
                        destination[offset++] = '\'';
                        destination[offset++] = '\'';
                        statement = statement.Slice(apostropheOffset + 1);
                    }

                    statement.CopyTo(destination.Slice(offset));
                    offset += statement.Length;
                }

                GuardScopeSuffix
                    .AsSpan()
                    .CopyTo(destination.Slice(offset));
            });
    }

    /// <summary>Appends a nested scalar scope without binding row expressions before their runtime gates.</summary>
    /// <param name="builder">The operation-owned guard buffer.</param>
    /// <param name="expression">The required scalar SQL expression to evaluate.</param>
    /// <param name="type">The provider-owned scalar output type.</param>
    /// <param name="targetVariable">The caller variable receiving the scalar result.</param>
    /// <param name="indentation">The indentation before the nested scope.</param>
    /// <param name="preambleSql">Optional catalog setup retained in the same scalar scope.</param>
    /// <param name="coalesce">Wraps a repair or postcondition predicate in the existing null-to-zero template.</param>
    /// <exception cref="ArgumentNullException">An unwrapped, required scalar expression is null.</exception>
    private static void AppendDelayedScalar(
        StringBuilder builder,
        string? expression,
        string type,
        string targetVariable,
        string indentation,
        string? preambleSql = null,
        bool coalesce = false
    )
    {
        // WHY: Wrapped predicates historically use interpolation's null-as-empty
        // contract. Preserve those exact bytes without weakening the distinct
        // unwrapped required-expression guard or allocating a replacement string.
        if (!coalesce)
        {
            ArgumentNullException.ThrowIfNull(expression);
        }

        if (SqlServerSqlStringLiteral.RequiresExpression(expression.AsSpan())
            || SqlServerSqlStringLiteral.RequiresExpression(preambleSql.AsSpan()))
        {
            var statement = (preambleSql ?? string.Empty) + "\nSET @doka_value = ("
                + (coalesce ? "COALESCE((" : string.Empty) + expression
                + (coalesce ? "), 0)" : string.Empty) + ");";
            var argument = SqlServerSqlStringLiteral.StatementArgument(
                statement, targetVariable + "_sql", out var declaration);
            builder.Append(indentation).Append(declaration)
                .Append("EXEC sys.sp_executesql ").Append(argument)
                .Append(", N'@doka_value ").Append(type)
                .Append(" OUTPUT', @doka_value = ").Append(targetVariable).Append(" OUTPUT;\n");

            return;
        }

        builder
            .Append(indentation)
            .Append(GuardScopePrefix);
        AppendEscapedSqlLiteral(builder, preambleSql.AsSpan());
        builder.Append("\nSET @doka_value = (");
        if (coalesce)
        {
            // WHY: Repair and postcondition expressions can fill a whole guard.
            // Append their fixed wrapper instead of allocating another complete
            // expression; callers retain interpolation's null-as-empty behavior.
            builder.Append("COALESCE((");
        }

        AppendEscapedSqlLiteral(builder, expression.AsSpan());
        if (coalesce)
        {
            builder.Append("), 0)");
        }

        builder
            .Append(");', N'@doka_value ")
            .Append(type)
            .Append(" OUTPUT', @doka_value = ")
            .Append(targetVariable)
            .Append(" OUTPUT;\n");
    }

    private static void AppendDynamicCommands(
        StringBuilder builder,
        IReadOnlyList<MigrationCommand> commands
    )
    {
        if (commands.Count == 0)
        {
            builder.Append("    SET @doka_repair_ok = @doka_repair_ok;\n");

            return;
        }

        // WHY: Indexing avoids an interface enumerator for every nested dynamic-command batch.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < commands.Count; index++)
        {
            // WHY: Compile each DDL statement only after runtime catalog
            // classification. Otherwise SQL Server can bind absent objects in
            // an IF branch that would never execute.
            AppendDynamicSql(builder, commands[index].CommandText, "    ");
        }
    }

    /// <summary>Appends one required statement in a nested scope that preserves late DDL binding.</summary>
    /// <param name="builder">The operation-owned guard buffer.</param>
    /// <param name="sql">The required provider-generated statement text.</param>
    /// <param name="indentation">The indentation before the nested scope.</param>
    /// <exception cref="ArgumentNullException">The required statement text is null.</exception>
    private static void AppendDynamicSql(
        StringBuilder builder,
        string sql,
        string indentation
    )
    {
        // WHY: Span conversion accepts null as empty, but an extensible baseline
        // returning absent SQL must not silently produce an empty nested command.
        ArgumentNullException.ThrowIfNull(sql);

        // WHY: The outer guard's transport cannot undo SQL Server's parsing
        // of this nested N'...' argument. Reconstruct vulnerable DDL before
        // quoting the nested batch, while retaining the ordinary fast path.
        if (SqlServerSqlStringLiteral.RequiresExpression(sql.AsSpan()))
        {
            sql = ProtectCommandSql(sql, MigrationsSqlGenerationOptions.Default);
        }

        builder
            .Append(indentation)
            .Append(GuardScopePrefix);
        AppendEscapedSqlLiteral(builder, sql.AsSpan());
        builder.Append(GuardScopeSuffix);
    }

    /// <summary>Appends SQL literal escaping directly without allocating a full escaped copy of the input.</summary>
    /// <param name="builder">The operation-owned destination receiving the escaped characters.</param>
    /// <param name="sql">The original literal content; an empty span appends no characters.</param>
    /// <exception cref="ArgumentNullException">The destination builder is null.</exception>
    internal static void AppendEscapedSqlLiteral(
        StringBuilder builder,
        ReadOnlySpan<char> sql
    )
    {
        SqlServerSqlStringLiteral.AppendEscaped(builder, sql);
    }

    /// <summary>Appends policy decisions without an intermediate action-case buffer or string.</summary>
    /// <param name="builder">The operation-owned guard buffer.</param>
    /// <param name="operation">The intent and policy evaluated for every observed state.</param>
    /// <param name="repairCapability">The provider-proven repair boundary.</param>
    private static void AppendActionCase(
        StringBuilder builder,
        SafeMigrationOperation operation,
        SafeMigrationRepairCapability repairCapability
    )
    {
        // WHY: The case has exactly one consumer: this guard. Writing directly
        // avoids retaining a second chunk chain and then copying its final string.
        builder.Append("CASE @doka_state ");
        foreach (var state in s_observedStates)
        {
            var action = SafeMigrationDecisionPlanner.PlanAction(
                operation.Intent.Kind,
                state,
                operation.Policy,
                repairCapability);

            builder
                .Append("WHEN N'")
                .Append(StateCode(state))
                .Append("' THEN ");
            if (action == SafeMigrationAction.Repair)
            {
                builder
                    .Append("CASE WHEN @doka_repair_ok = 1 THEN N'repair' ")
                    .Append("ELSE N'reject_different' END ");
            }
            else
            {
                builder
                    .Append("N'")
                    .Append(ActionCode(action))
                    .Append("' ");
            }
        }

        builder.Append("ELSE N'reject_unsupported' END");
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
}
