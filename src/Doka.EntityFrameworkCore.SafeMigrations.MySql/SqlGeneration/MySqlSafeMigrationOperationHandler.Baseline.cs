namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler
{
    internal static void ValidateMultiCommandCleanup(
        IReadOnlyList<MySqlMigrationCommandSpec> commands
    )
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (commands.Count < 2)
        {
            return;
        }

        // WHY: Per-command cleanup is needed before a later provider command,
        // while scope cleanup is needed after failure. Until the provider
        // supplies a once-only cleanup contract, executing both is unsafe.
        if (commands.Any(static command => command.Fragments.Any(static fragment =>
                fragment.Kind == MySqlMigrationCommandFragmentKind.Cleanup)))
        {
            throw new InvalidOperationException(
                "A multi-command MySQL baseline with provider cleanup cannot be guarded safely.");
        }
    }

    private static BaselineBatch GetBaselineBatch(
        IReadOnlyList<MySqlMigrationCommandSpec> baseline
    )
    {
        if (baseline.Count == 0)
        {
            throw new InvalidOperationException(
                "A SafeMigrations operation must render at least one MySQL baseline command boundary.");
        }

        ValidateMultiCommandCleanup(baseline);

        var transactionSuppressed = baseline[0].TransactionSuppressed;
        var commands = new BaselineFragments[baseline.Count];

        // WHY: Validate and store each provider command in its original ordered batch slot.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < baseline.Count; index++)
        {
            var command = baseline[index];
            if (command.TransactionSuppressed != transactionSuppressed)
            {
                throw new InvalidOperationException(
                    "A multi-command SafeMigrations operation requires uniform MySQL transaction suppression.");
            }

            commands[index] = GetBaselineFragments(command);
        }

        return new BaselineBatch(commands, transactionSuppressed);
    }

    private static BaselineFragments GetBaselineFragments(
        MySqlMigrationCommandSpec command
    )
    {
        if (command.Fragments.Count == 0)
        {
            return new BaselineFragments([], NormalizePreparedBody(command.CommandText), []);
        }

        // WHY: Most provider baselines contain only a body; empty setup/cleanup
        // roles must not allocate a list and backing array for every column.
        List<string>? setupCommands = null;
        List<string>? cleanupCommands = null;
        string? bodyCommand = null;

        foreach (var fragment in command.Fragments)
        {
            var commandText = fragment.CommandText.ToString();
            switch (fragment.Kind)
            {
                case MySqlMigrationCommandFragmentKind.Setup:
                    (setupCommands ??= []).Add(commandText);
                    break;
                case MySqlMigrationCommandFragmentKind.Body when bodyCommand is null:
                    bodyCommand = NormalizePreparedBody(commandText);
                    break;
                case MySqlMigrationCommandFragmentKind.Body:
                    throw new InvalidOperationException(
                        "A provider-rendered MySQL baseline contains more than one body fragment.");
                case MySqlMigrationCommandFragmentKind.Cleanup:
                    (cleanupCommands ??= []).Add(commandText);
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

        // WHY: Coalesce through the immutable interface; a List-typed [] fallback would allocate empty lists.
        return new BaselineFragments(
            (IReadOnlyList<string>?)setupCommands ?? Array.Empty<string>(),
            bodyCommand,
            (IReadOnlyList<string>?)cleanupCommands ?? Array.Empty<string>());
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

    private static string BuildGuardedBaselineBody(
        SafeMigrationOperation operation,
        MySqlSafeMigrationRuntimePlan runtimePlan,
        BaselineBatch baseline,
        BaselineBatch? repair,
        IReadOnlyList<string> renderedParameterValues,
        bool preparedInSetup,
        string postconditionSuffix
    )
    {
        if (preparedInSetup)
        {
            // WHY: The common single-command path does not need incremental
            // composition. Direct concatenation allocates the final command
            // once instead of growing a StringBuilder for every operation.
            if (runtimePlan.BinaryDefaultProofs.Count == 0)
            {
                return $"EXECUTE {PreparedStatementName};\n"
                    + PostconditionBodyPrefix
                    + runtimePlan.RenderPreparedExecutionPostcondition(renderedParameterValues)
                    + postconditionSuffix;
            }

            return $"EXECUTE {PreparedStatementName};\n"
                + BuildPostconditionBody(runtimePlan, renderedParameterValues, postconditionSuffix);
        }

        var builder = new StringBuilder();
        // WHY: Keep each prepared command's repair body tied to its provider-command ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < baseline.Commands.Count; index++)
        {
            var command = baseline.Commands[index];
            foreach (var setupCommand in command.SetupCommands)
            {
                AppendCommand(builder, setupCommand);
            }

            // WHY: One EF migration operation can legitimately render an
            // ordered provider sequence, for example a data backfill followed
            // by ALTER TABLE. Keep every command behind the same decision and
            // verify the final state after the full sequence.
            AppendCommand(
                builder,
                BuildPreparedSqlAssignment(
                        command.BodyCommand,
                        GetRepairBody(operation, runtimePlan, repair, command, index))
                    .ToSql());
            AppendCommand(builder, $"PREPARE {PreparedStatementName} FROM @doka_sm_sql;");
            AppendCommand(builder, $"EXECUTE {PreparedStatementName};");
            AppendCommand(builder, $"DEALLOCATE PREPARE {PreparedStatementName};");
        }

        builder.Append(BuildPostconditionBody(runtimePlan, renderedParameterValues, postconditionSuffix));

        return builder.ToString();
    }

    private static string BuildPostconditionBody(
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues,
        string postconditionSuffix
    ) => BuildBinaryDefaultProofEvaluation(
            runtimePlan,
            renderedParameterValues,
            "@doka_sm_action IN ('apply', 'repair')")
        + PostconditionBodyPrefix
        + runtimePlan.RenderPreparedExecutionPostcondition(renderedParameterValues)
        + postconditionSuffix;

    private static string? GetRepairBody(
        SafeMigrationOperation operation,
        MySqlSafeMigrationRuntimePlan runtimePlan,
        BaselineBatch? repair,
        BaselineFragments applyCommand,
        int commandOrdinal
    ) => repair?.Commands[commandOrdinal].BodyCommand
        ?? (operation is { Policy: SafeMigrationPolicy.RepairIfSafe, Intent: AlterColumnIntent }
            && runtimePlan.RepairCapability == SafeMigrationRepairCapability.Safe
                ? applyCommand.BodyCommand
                : null);

    /// <summary>Preserves cleanup order without allocating containers for the common owned-only path.</summary>
    /// <param name="baseline">The admitted original native resource scope.</param>
    /// <param name="hasNullabilityProof">Whether the operation owns a materialized NULL-proof variable.</param>
    /// <param name="hasRequiredColumnAdd">Whether the operation owns catalog-status and resume variables.</param>
    /// <param name="hasNativeSqlModeCleanup">Whether the scope has a certified native acquisition snapshot.</param>
    /// <param name="binaryDefaultProofCount">The number of operation-owned exact-default variables to clear.</param>
    /// <param name="identifierReferenceProofs">The unique native syntax scopes to release independently.</param>
    private static IReadOnlyList<string> BuildCleanupCommands(
        BaselineBatch baseline,
        bool hasNullabilityProof,
        bool hasRequiredColumnAdd = false,
        bool hasNativeSqlModeCleanup = false,
        int binaryDefaultProofCount = 0,
        IReadOnlyList<MySqlSafeMigrationIdentifierReferenceProof>? identifierReferenceProofs = null
    )
    {
        var cleanupCommandCount = 0;
        foreach (var command in baseline.Commands)
        {
            cleanupCommandCount += command.CleanupCommands.Count;
        }

        if (cleanupCommandCount == 0
            && !hasRequiredColumnAdd
            && binaryDefaultProofCount == 0
            && identifierReferenceProofs is not { Count: > 0 })
        {
            // WHY: These immutable SQL templates contain no operation or database state.
            // CreateScoped snapshots and reverses its own copy; it never mutates this input.
            return hasNullabilityProof ? s_nullabilityGuardCleanupCommands : s_guardCleanupCommands;
        }

        var commands = new List<string>(2 + cleanupCommandCount)
        {
            BuildGuardCleanupSql(hasNullabilityProof, hasRequiredColumnAdd, hasNativeSqlModeCleanup)
            + MySqlSafeMigrationBinaryDefaultProof.BuildResetSql(binaryDefaultProofCount, 0, clear: true),
        };

        if (identifierReferenceProofs is not null)
        {
            foreach (var proof in identifierReferenceProofs)
            {
                commands.Add(MySqlSafeMigrationIdentifierReferenceProof.BuildCleanupSql(proof));
            }
        }

        // WHY: CreateScoped reverses cleanup input. Add provider cleanup in
        // reverse scope order and reverse execution order so failure in any
        // command unwinds every acquired provider resource before guard state is reset.
        foreach (var command in baseline.Commands)
        {
            // WHY: Unwind cleanup commands in reverse setup order.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = command.CleanupCommands.Count - 1; index >= 0; index--)
            {
                commands.Add(
                    hasNativeSqlModeCleanup
                        ? BuildConditionalNativeSqlModeRestore(command.CleanupCommands[index])
                        : command.CleanupCommands[index]);
            }
        }

        commands.Add(BuildPreparedStatementCleanupSql());

        return commands;
    }

    private static void AppendCommand(
        StringBuilder builder,
        string command
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        builder.AppendLine(command);
    }

    /// <summary>Clears only proof resources owned by the operation, preserving other SQL paths byte-for-byte.</summary>
    /// <param name="hasNullabilityProof">Whether the operation owns the nullable-column proof variables.</param>
    /// <param name="hasRequiredColumnAdd">Whether the operation owns catalog-status and resume variables.</param>
    /// <param name="hasNativeSqlModeCleanup">Whether final cleanup must clear the native acquisition flag.</param>
    private static string BuildGuardCleanupSql(
        bool hasNullabilityProof,
        bool hasRequiredColumnAdd = false,
        bool hasNativeSqlModeCleanup = false
    )
    {
        const string prefix = "SET @doka_sm_state = NULL, @doka_sm_action = NULL, "
            + "@doka_sm_repair_ok = NULL, @doka_sm_prerequisite_ok = NULL, @doka_sm_sql = NULL, "
            + "@doka_sm_post_ok = NULL, @doka_sm_data_probe_required = NULL, @doka_sm_data_blocked = NULL, "
            + "@doka_sm_diag = NULL; ";

        const string suffix = "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`;";
        var requiredColumnAddCleanup = hasRequiredColumnAdd
            ? "SET @doka_sm_required_add_resume = NULL, @doka_sm_add_status = NULL; "
            : string.Empty;

        var nativeScopeCleanup = hasNativeSqlModeCleanup ? ProviderSetupEnteredCleanup : string.Empty;

        return hasNullabilityProof
            ? prefix
            + "SET @doka_sm_transition_eligible = NULL, @doka_sm_nullability_blocked = NULL, "
            + "@doka_sm_column_repair_eligible = NULL; "
            + requiredColumnAddCleanup
            + nativeScopeCleanup
            + suffix
            : prefix
            + "SET @doka_sm_transition_eligible = NULL; "
            + requiredColumnAddCleanup
            + nativeScopeCleanup
            + suffix;
    }

    private IReadOnlyList<MySqlMigrationCommandSpec> RenderBaseline(
        SafeMigrationOperation operation,
        MySqlSafeMigrationRuntimePlan runtimePlan,
        MySqlMigrationOperationContext context
    )
    {
        if (runtimePlan.IsStaticallyUnsupported)
        {
            return [Command("DO 0;", transactionSuppressed: true)];
        }

        if (operation.Intent is ModelManagedDataIntent modelManagedData)
        {
            return
            [
                Command(
                    runtimePlan.RenderPreparedMutationSql(
                        runtimePlan
                            .ParameterValues
                            .Select(RenderParameterLiteral)
                            .ToArray()),
                    transactionSuppressed: false),
            ];
        }

        if (operation.Intent is EnsureIndexIntent index
            && (index.Definition.Keys.Any(static key =>
                    key.Expression is not null || key.StructuredExpression is not null || key.PrefixLength is not null)
                || index.Definition.Method is not null))
        {
            return [Command(BuildCustomCreateIndexSql(index.Definition), transactionSuppressed: true)];
        }

        var standardOperation = SafeMigrationStandardOperationFactory.Create(
            operation.Intent,
            RenderStructuredExpression,
            static collation => collation.Schema is null ? collation.Name : null);

        if (context.ServerVersion.IsMariaDb)
        {
            // WHY: Required generated values use a catalog-qualified semantic
            // proof. MariaDB has not generated NOT NULL grammar, so the native
            // physical declaration remains nullable without weakening the
            // immutable expected contract or another engine's DDL.
            switch (standardOperation)
            {
                case AddColumnOperation { ComputedColumnSql: not null } generated:
                    generated.IsNullable = true;
                    break;
                case AlterColumnOperation { ComputedColumnSql: not null } generated:
                    generated.IsNullable = true;
                    break;
                case CreateTableOperation table:
                    foreach (var column in table.Columns)
                    {
                        if (column.ComputedColumnSql is not null)
                        {
                            column.IsNullable = true;
                        }
                    }

                    break;
            }
        }

        if (standardOperation is AddColumnOperation addColumn
            && operation.Intent is EnsureColumnIntent ensureIntent
            && MySqlSafeMigrationCatalogSqlBuilder.RequiresUnsupportedMissingRequiredAdd(ensureIntent.Definition))
        {
            // WHY: Excluded unsafe Missing shapes are rejected by catalog state,
            // but exact Matching and ordinary repair still need a renderable
            // native scope. This unused ADD stays nullable and retains metadata;
            // only the separately certified protocol can apply a required ADD.
            addColumn.IsNullable = true;
        }

        if (runtimePlan.NullabilityDataProbe is not null
            && standardOperation is AlterColumnOperation alterColumn
            && operation.Intent is AlterColumnIntent alterIntent
            && !SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(alterIntent.Definition.DefaultValue))
        {
            // WHY: The guarded NULL proof rejects every row requiring a
            // backfill. Suppress provider backfill generation only on this
            // path; explicit non-null defaults retain their existing UPDATE.
            alterColumn.OldColumn.IsNullable = alterColumn.IsNullable;
        }

        return RenderNativeOperation(context, standardOperation, operation.Intent);
    }

    private BaselineBatch? RenderRepairBaseline(
        SafeMigrationOperation operation,
        MySqlSafeMigrationRuntimePlan runtimePlan,
        MySqlMigrationOperationContext context,
        BaselineBatch apply
    )
    {
        if (operation.Policy != SafeMigrationPolicy.RepairIfSafe
            || runtimePlan.RepairCapability != SafeMigrationRepairCapability.Safe
            || operation.Intent is not EnsureColumnIntent intent)
        {
            return null;
        }

        // MySQL and MariaDB MODIFY COLUMN replace the complete column
        // definition. Keeping old nullability equal avoids Doka's data-backfill
        // command; the catalog guard has already proved that no backfill is
        // required before the complete target definition is applied.
        var repairOperation = SafeMigrationStandardOperationFactory.CreateFullDefinitionRepair(
            intent,
            RenderStructuredExpression,
            static collation => collation.Schema is null ? collation.Name : null,
            MySqlSafeMigrationColumnMetadata.CanSafelyConverge);

        var repair = GetBaselineBatch(RenderNativeOperation(context, repairOperation, intent));
        if (repair.TransactionSuppressed != apply.TransactionSuppressed
            || repair.Commands.Count != apply.Commands.Count)
        {
            throw new InvalidOperationException(
                "Provider-rendered apply and repair commands require incompatible MySQL command scopes.");
        }

        // WHY: Compare apply and repair command scopes at the same provider-command ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < apply.Commands.Count; index++)
        {
            var applyCommand = apply.Commands[index];
            var repairCommand = repair.Commands[index];

            if (!applyCommand.SetupCommands.SequenceEqual(repairCommand.SetupCommands, StringComparer.Ordinal)
                || !applyCommand.CleanupCommands.SequenceEqual(repairCommand.CleanupCommands, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Provider-rendered apply and repair commands require incompatible MySQL command scopes.");
            }
        }

        return repair;
    }

    private sealed record BaselineFragments(
        IReadOnlyList<string> SetupCommands,
        string BodyCommand,
        IReadOnlyList<string> CleanupCommands
    );

    private sealed record BaselineBatch(
        IReadOnlyList<BaselineFragments> Commands,
        bool TransactionSuppressed
    );
}
