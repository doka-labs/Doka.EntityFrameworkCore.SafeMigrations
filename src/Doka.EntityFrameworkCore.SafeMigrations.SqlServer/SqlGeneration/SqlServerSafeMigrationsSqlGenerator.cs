namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>
/// Composes the ordinary EF Core SQL Server migrations generator with guarded
/// SafeMigrations commands. Ordinary migration operations remain unchanged.
/// </summary>
/// <remarks>
/// Generated migration commands recover IDENTITY_INSERT after an execution failure.
/// Consumers executing only the generated SQL text must recover or close their session
/// after cancellation or timeout; SQL Server TRY/CATCH cannot handle client attentions.
/// Failed session recovery requires a new context for supported migration and catalog
/// execution. This boundary does not intercept arbitrary EF queries or standalone SQL scripts.
/// </remarks>
public sealed class SqlServerSafeMigrationsSqlGenerator : IMigrationsSqlGenerator
{
    private const string GuardScopePrefix = "EXEC sys.sp_executesql N'";
    private const string GuardScopeSuffix = "';\n";

    private const string MetadataVisibilityExpression =
        "HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION')";

    private static readonly SafeMigrationObservedState[] s_observedStates =
        Enum.GetValues<SafeMigrationObservedState>();

    private readonly ISqlServerSafeMigrationsBaselineGenerator _baselineGenerator;
    private readonly SqlServerSafeMigrationCatalogSqlBuilder _catalogSqlBuilder;
    private readonly SqlServerSafeMigrationSqlExpressionRenderer _expressionRenderer;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly MigrationsSqlGeneratorDependencies _dependencies;

    /// <summary>Initializes the composed SQL Server generator.</summary>
    /// <param name="baselineGenerator">The generator for ordinary EF Core SQL Server operations.</param>
    /// <param name="typeMappingSource">The SQL Server relational type-mapping service.</param>
    /// <param name="sqlGenerationHelper">The SQL Server identifier-generation service.</param>
    /// <param name="dependencies">The command-building and current-context services.</param>
    public SqlServerSafeMigrationsSqlGenerator(
        ISqlServerSafeMigrationsBaselineGenerator baselineGenerator,
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper,
        MigrationsSqlGeneratorDependencies dependencies
    )
    {
        ArgumentNullException.ThrowIfNull(baselineGenerator);
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);
        ArgumentNullException.ThrowIfNull(dependencies);

        _baselineGenerator = baselineGenerator;
        _catalogSqlBuilder = new SqlServerSafeMigrationCatalogSqlBuilder(typeMappingSource, sqlGenerationHelper);
        _expressionRenderer = new SqlServerSafeMigrationSqlExpressionRenderer(typeMappingSource, sqlGenerationHelper);
        _sqlGenerationHelper = sqlGenerationHelper;
        _dependencies = dependencies;
    }

    /// <inheritdoc />
    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        var analyzer = _dependencies.CurrentContext.Context.GetService<ISafeMigrationProviderAnalyzer>()
            as SqlServerSafeMigrationProviderAnalyzer
            ?? throw new InvalidOperationException("SQL Server SafeMigrations requires its scoped provider analyzer.");

        // WHY: Even an ordinary-only retry through this enabled generator
        // must not reuse an uncertain session from earlier guarded execution.
        analyzer.ThrowIfConnectionQuarantined();

        ValidateTemporalOperationBoundary(operations);
        SafeMigrationExpectedIndexTransitions.Validate(operations, static schema => schema ?? "dbo");

        // WHY: The null-schema runtime guard establishes dbo as the only
        // unqualified physical identity allowed for SafeMigrations commands.
        var tableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(
            operations,
            static schema => schema ?? "dbo");

        var plans = new SqlServerSafeMigrationRuntimePlan?[operations.Count];
        var safeOperationCount = 0;
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal] is not SafeMigrationOperation safeOperation)
            {
                continue;
            }

            var expected = safeOperation.Intent is EnsureTableIntent table
                ? tableConstraints.GetValueOrDefault((table.Definition.Schema, table.Definition.Table))
                : null;

            var plan = _catalogSqlBuilder.Build(
                safeOperation,
                expected);

            // WHY: Returning an ordinary command before a known unsupported
            // intent would let script clients execute a partial migration.
            // Preflight the entire stream before calling any EF baseline.
            if (plan.IsStaticallyUnsupported)
            {
                throw new NotSupportedException(
                    "The SQL Server SafeMigrations contract is unsupported: " + plan.UnsupportedCode + ".");
            }

            plans[ordinal] = plan;
            safeOperationCount++;
        }

        var safeOperations = ExtractSafeOperations(operations, safeOperationCount);
        var metadataCommand = _dependencies.CommandBuilderFactory.Create().Build();

        var commands = new List<MigrationCommand>(operations.Count);
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

                // WHY: EF Core can use the runtime model differ to create
                // migration-history DDL. This scaffolding marker has no
                // runtime database effect.
                continue;
            }

            if (operation is not SafeMigrationOperation safeOperation)
            {
                var ordinary = new List<MigrationOperation>();
                do
                {
                    ordinary.Add(operations[ordinal]);
                    ordinal++;
                }
                while (ordinal < operations.Count
                       && operations[ordinal] is not SafeMigrationOperation
                           and not SafeMigrationDesignTimeServicesRequiredOperation);

                // WHY: SQL Server's baseline generator can inspect adjacent
                // operations (for example, temporal-table DDL). Preserve each
                // uninterrupted ordinary EF sequence as one input batch.
                var ordinaryCommands = _baselineGenerator.Generate(ordinary, model, options);

                // WHY: Commands may be cached before another migration
                // quarantines this context, even in an ordinary-only stream.
                AppendGuardedCommands(commands, ordinaryCommands, _dependencies, analyzer, metadataCommand);
                ordinal--;

                continue;
            }

            var plan = plans[ordinal] ?? throw new UnreachableException();

            var baseline = RenderBaseline(safeOperation, plan, model, options);
            var repairBaseline = RenderRepairBaseline(safeOperation, plan, model, options);
            var effectiveRepairBaseline = repairBaseline.Count > 0
                ? repairBaseline
                : safeOperation is { Policy: SafeMigrationPolicy.RepairIfSafe, Intent: AlterColumnIntent }
                    && plan.RepairCapability == SafeMigrationRepairCapability.Safe
                    ? baseline
                    : [];

            if (!plan.IsStaticallyUnsupported
                && baseline.Count == 0
                && safeOperation.Intent is not EnsureSchemaIntent { Name: "dbo" }
                && !IsUnchangedTableRename(safeOperation.Intent))
            {
                throw new InvalidOperationException(
                    "A supported SQL Server SafeMigrations operation has no baseline command.");
            }

            if (safeOperation.Policy == SafeMigrationPolicy.RepairIfSafe
                && plan.RepairCapability == SafeMigrationRepairCapability.Safe
                && effectiveRepairBaseline.Count == 0)
            {
                throw new InvalidOperationException(
                    "A repairable SQL Server SafeMigrations operation has no repair command.");
            }

            if (HasTransactionSuppressedCommand(baseline)
                || HasTransactionSuppressedCommand(effectiveRepairBaseline))
            {
                throw new NotSupportedException(
                    "A transaction-suppressed SQL Server baseline cannot be guarded atomically.");
            }

            var guarded = new SqlOperation
            {
                Sql = BuildGuardedSql(safeOperation, plan, baseline, effectiveRepairBaseline),
            };

            var guardedCommands = _baselineGenerator.Generate([guarded], model, options);
            ValidateGuardCommands(guardedCommands);
            if (safeOperation.Intent is EnsureModelManagedDataIntent data)
            {
                var table = _sqlGenerationHelper.DelimitIdentifier(data.Table, data.Schema ?? "dbo");
                var tableLiteral = "N'" + table.Replace("'", "''", StringComparison.Ordinal) + "'";
                var columns = string.Join(", ", data.Columns.Select(static column =>
                    "N'" + column.Replace("'", "''", StringComparison.Ordinal) + "'"));

                var cleanupSql = "IF EXISTS (SELECT 1 FROM sys.identity_columns "
                    + "WHERE object_id = OBJECT_ID(" + tableLiteral + ") AND name IN (" + columns + ")) "
                    + "SET IDENTITY_INSERT " + table + " OFF;";

                for (var index = 0; index < guardedCommands.Count; index++)
                {
                    commands.Add(new SqlServerSafeMigrationIdentityInsertCommand(
                        guardedCommands[index],
                        cleanupSql,
                        _dependencies,
                        analyzer,
                        metadataCommand));
                }
            }
            else
            {
                AppendGuardedCommands(commands, guardedCommands, _dependencies, analyzer, metadataCommand);
            }
        }

        if (safeOperations.Length > 0)
        {
            // WHY: The analyzer and script path must use one CATALOG_DEFAULT
            // identity contract. Keep its bounded VALUES fragments in one
            // dynamic scope so attention cannot leave temporary caller state.
            var identifierStatements = SqlServerIdentifierContract.BuildCollisionGuardCommands(
                SqlServerIdentifierContract.Collect(safeOperations),
                throwOnCollision: true);

            var identifierGuard = new SqlOperation
            {
                Sql = BuildIsolatedIdentifierGuardSql(identifierStatements),
            };

            var identifierCommands = _baselineGenerator.Generate([identifierGuard], model, options);
            ValidateGuardCommands(identifierCommands);
            PrependGuardedCommands(commands, identifierCommands, _dependencies, analyzer, metadataCommand);
        }

        return commands.AsReadOnly();
    }

    /// <summary>Extracts the already-counted safe operation references in their original stream order.</summary>
    /// <param name="operations">The preflighted operation stream, which must not be concurrently modified.</param>
    /// <param name="safeOperationCount">The exact count established by the completed preflight.</param>
    /// <returns>One exact-sized array containing the original safe operation references.</returns>
    /// <exception cref="ArgumentNullException">The operation stream is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative or exceeds the stream length.</exception>
    /// <exception cref="InvalidOperationException">The supplied count does not match the operation stream.</exception>
    internal static SafeMigrationOperation[] ExtractSafeOperations(
        IReadOnlyList<MigrationOperation> operations,
        int safeOperationCount
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfNegative(safeOperationCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(safeOperationCount, operations.Count);

        // WHY: Preflight already visits every safe operation. Its exact count
        // avoids the filtering iterator and temporary growth buffers needed by
        // OfType/ToArray while retaining the original objects and their order.
        var safeOperations = safeOperationCount == 0
            ? Array.Empty<SafeMigrationOperation>()
            : new SafeMigrationOperation[safeOperationCount];

        var offset = 0;
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal] is not SafeMigrationOperation safeOperation)
            {
                continue;
            }

            if (offset == safeOperationCount)
            {
                throw new InvalidOperationException("The safe operation preflight count no longer matches the stream.");
            }

            safeOperations[offset++] = safeOperation;
        }

        if (offset != safeOperationCount)
        {
            throw new InvalidOperationException("The safe operation preflight count no longer matches the stream.");
        }

        return safeOperations;
    }

    private static void ValidateGuardCommands(IReadOnlyList<MigrationCommand> commands)
    {
        if (commands.Count == 0)
        {
            throw new InvalidOperationException("A SQL Server SafeMigrations guard has no generated command.");
        }

        if (HasTransactionSuppressedCommand(commands))
        {
            throw new NotSupportedException("A SQL Server SafeMigrations guard cannot suppress its transaction.");
        }
    }

    /// <summary>Appends quarantine wrappers in provider order without selector or enumerator allocations.</summary>
    /// <param name="destination">The operation stream receiving the wrapped commands.</param>
    /// <param name="source">The completed provider command batch.</param>
    /// <param name="dependencies">The command-building and current-context services.</param>
    /// <param name="analyzer">The existing scoped provider quarantine owner.</param>
    /// <param name="metadataCommand">The shared empty metadata command, not a copy of any command SQL.</param>
    internal static void AppendGuardedCommands(
        List<MigrationCommand> destination,
        IReadOnlyList<MigrationCommand> source,
        MigrationsSqlGeneratorDependencies dependencies,
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        IRelationalCommand metadataCommand
    )
    {
        // WHY: Each safe operation normally contributes a small provider list.
        // Index that existing list directly instead of allocating a captured
        // selector and iterator for every operation's wrapping step.
        for (var index = 0; index < source.Count; index++)
        {
            destination.Add(new SqlServerSafeMigrationGuardedCommand(
                source[index], dependencies, analyzer, metadataCommand));
        }
    }

    /// <summary>
    /// Prepends identifier wrappers in provider order without temporary lists or repeated front shifts.
    /// </summary>
    /// <param name="destination">The operation stream receiving the wrapped identifier prefix.</param>
    /// <param name="source">The completed provider identifier command batch.</param>
    /// <param name="dependencies">The command-building and current-context services.</param>
    /// <param name="analyzer">The existing scoped provider quarantine owner.</param>
    /// <param name="metadataCommand">The shared empty metadata command, not a copy of any command SQL.</param>
    internal static void PrependGuardedCommands(
        List<MigrationCommand> destination,
        IReadOnlyList<MigrationCommand> source,
        MigrationsSqlGeneratorDependencies dependencies,
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        IRelationalCommand metadataCommand
    )
    {
        var previousCount = destination.Count;
        AppendGuardedCommands(destination, source, dependencies, analyzer, metadataCommand);

        // WHY: Append and rotate the two ordered regions in place. This keeps
        // identifier guards before every ordinary command without a selector,
        // intermediate wrapper list, or one whole-stream shift per prefix row.
        destination.Reverse(0, previousCount);
        destination.Reverse(previousCount, destination.Count - previousCount);
        destination.Reverse();
    }

    /// <summary>Checks the unchanged transaction boundary directly on a completed provider command list.</summary>
    /// <param name="commands">The provider batch whose suppression flags are inspected.</param>
    /// <returns>Whether any command suppresses its transaction.</returns>
    private static bool HasTransactionSuppressedCommand(
        IReadOnlyList<MigrationCommand> commands
    )
    {
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index].TransactionSuppressed)
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<MigrationCommand> RenderBaseline(
        SafeMigrationOperation operation,
        SqlServerSafeMigrationRuntimePlan plan,
        IModel? model,
        MigrationsSqlGenerationOptions options
    )
    {
        if (plan.IsStaticallyUnsupported)
        {
            return [];
        }

        if (operation.Intent is ModelManagedDataIntent data)
        {
            return _baselineGenerator.Generate(
                [new SqlOperation { Sql = _catalogSqlBuilder.BuildModelManagedDataMutationSql(data) }],
                model,
                options);
        }

        if (IsUnchangedTableRename(operation.Intent))
        {
            // WHY: A repeated identity still requires the runtime object-kind
            // and policy guards, but sp_rename and schema transfer have no
            // work to perform. The guarded command remains non-empty.
            return [];
        }

        var baselineOperation = SafeMigrationStandardOperationFactory.Create(
            operation.Intent,
            _expressionRenderer.Render,
            static collation => collation.Schema is null ? collation.Name : null);

        var generationModel = model;

        if (operation.Intent is RenameTableIntent rename && baselineOperation is RenameTableOperation renameTable)
        {
            // WHY: EF interprets an omitted NewSchema as a transfer to the
            // principal's default schema. A safe rename without a requested
            // transfer must instead preserve its explicitly qualified source.
            renameTable.NewSchema = rename.NewSchema ?? rename.Schema;
        }

        if (operation.Intent is EnsureIndexIntent { Definition.IncludedColumns.Count: > 0 } index
            && baselineOperation is CreateIndexOperation createIndex)
        {
            // WHY: Core's provider-neutral factory cannot carry included columns.
            // SQL Server's EF generator consumes this provider annotation.
            createIndex["SqlServer:Include"] = index.Definition.IncludedColumns.ToArray();
        }

        if (operation.Intent is AlterColumnIntent
            && baselineOperation is AlterColumnOperation alteration)
        {
            // WHY: A safe ALTER owns a complete captured column contract.
            // EF's single-operation generation otherwise derives extra index
            // drops/recreates from the target model, outside the ordered safe
            // operations and their proofs. Dependencies must be explicit.
            generationModel = null;
            if (plan.RepairCapability == SafeMigrationRepairCapability.Safe
                && plan.MayRequireNullabilityDataProof
                && !alteration.IsNullable
                && (alteration.DefaultValue is not null || alteration.DefaultValueSql is not null))
            {
                // WHY: Runtime already rejects any NULL row before ALTER.
                // EF would still emit a zero-row default UPDATE, which fires
                // DML triggers. This renderer-only hint suppresses that write;
                // the original immutable source/null proof remains unchanged.
                alteration.OldColumn.IsNullable = false;
            }
        }

        return _baselineGenerator.Generate([baselineOperation], generationModel, options);
    }

    /// <summary>Identifies the exact safe rename whose guarded contract needs no physical DDL.</summary>
    private static bool IsUnchangedTableRename(SafeMigrationIntent intent)
        => intent is RenameTableIntent rename
            && (rename.NewName ?? rename.Name) == rename.Name
            && (rename.NewSchema ?? rename.Schema ?? "dbo") == (rename.Schema ?? "dbo");

    private IReadOnlyList<MigrationCommand> RenderRepairBaseline(
        SafeMigrationOperation operation,
        SqlServerSafeMigrationRuntimePlan plan,
        IModel? model,
        MigrationsSqlGenerationOptions options
    )
    {
        if (operation.Policy != SafeMigrationPolicy.RepairIfSafe
            || plan.RepairCapability != SafeMigrationRepairCapability.Safe
            || operation.Intent is not EnsureColumnIntent column)
        {
            return [];
        }

        var repairOperation = SafeMigrationStandardOperationFactory.CreateRepair(
            column,
            _expressionRenderer.Render,
            static collation => collation.Schema is null ? collation.Name : null);

        return _baselineGenerator.Generate([repairOperation], model, options);
    }

    /// <summary>Renders one prepared operation inside its private dynamic scope.</summary>
    /// <param name="operation">The safe intent and policy whose action is guarded.</param>
    /// <param name="plan">The prepared catalog and execution expressions.</param>
    /// <param name="baseline">The provider commands for an accepted apply action.</param>
    /// <param name="repairBaseline">The provider commands for an accepted repair action.</param>
    /// <returns>The complete isolated guard with deferred scalar and DDL scopes intact.</returns>
    internal static string BuildGuardedSql(
        SafeMigrationOperation operation,
        SqlServerSafeMigrationRuntimePlan plan,
        IReadOnlyList<MigrationCommand> baseline,
        IReadOnlyList<MigrationCommand> repairBaseline
    )
    {
        var builder = new StringBuilder(1024);

        if (operation.Intent is EnsureModelManagedDataIntent)
        {
            builder.Append("-- Script clients must recover IDENTITY_INSERT or close this session "
                + "after cancellation/timeout.\n");
        }

        // WHY: SQL Server hides catalog metadata the caller cannot view. A
        // missing catalog row is not evidence that the object is absent.
        builder.Append("IF COALESCE(").Append(MetadataVisibilityExpression)
            .Append(", 0) <> 1\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");

        if (plan.PhysicalTableSupportExpression is { } physicalTableSupport)
        {
            // WHY: Engine-specific tables can look structurally ordinary but
            // require different DDL and DML. Reject before any row expression
            // is bound or a caller policy can authorize a baseline command.
            builder.Append("IF COALESCE((").Append(physicalTableSupport)
                .Append("), 0) <> 1\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");
        }

        if (plan.ColumnLayoutFailureExpression is { } columnLayoutFailure)
        {
            // WHY: An absent column consumes a new slot and fixed row storage.
            // Reject an unproven layout using metadata before typed constants,
            // row probes, caller policy, or baseline DDL can bind or execute.
            builder.Append("IF (").Append(columnLayoutFailure)
                .Append(") IS NOT NULL\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");
        }

        if (plan.ColumnCollationSupportExpression is { } columnCollationSupport)
        {
            // WHY: SQL Server binds an authored COLLATE name before a skipped
            // IF branch executes. Prove it exists before delayed conversion
            // or baseline SQL can compile that physical name.
            builder.Append("IF COALESCE((").Append(columnCollationSupport)
                .Append("), 0) <> 1\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");
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

            builder.Append("IF COALESCE((").Append(defaultValueSupport)
                .Append("), 0) <> 1\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");
        }

        if (plan.IndexFilterSupportExpression is { } indexFilterSupport)
        {
            // WHY: A valid predicate shape can still require a forbidden
            // column-side conversion or an invalid constant. Qualify only
            // catalog metadata and constants before binding any row filter.
            builder.Append("IF COALESCE((").Append(indexFilterSupport)
                .Append("), 0) <> 1\nBEGIN\n    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n");
        }

        if (UsesDefaultSchema(operation.Intent))
        {
            // WHY: EF's unqualified DDL targets the caller's default schema.
            // Catalog classification uses dbo, so prohibit a different default
            // before any baseline command can run.
            builder.Append("IF COALESCE(SCHEMA_NAME(), N'') <> N'dbo'\nBEGIN\n"
                + "    THROW 51004, N'doka_sm_prerequisite_missing', 1;\nEND;\n");
        }

        builder.Append("DECLARE @doka_state nvarchar(32);\n")
            .Append("DECLARE @doka_action nvarchar(32);\n")
            .Append("DECLARE @doka_repair_ok int;\n")
            .Append("IF COALESCE((").Append(plan.PrerequisiteExpression).Append("), 0) <> 1\n")
            .Append("BEGIN\n    SET @doka_state = N'prerequisite_missing';\n")
            .Append("    SET @doka_repair_ok = 0;\nEND\nELSE IF COALESCE((")
            .Append(plan.StateEvaluationGuardExpression)
            .Append("), 0) <> 1\nBEGIN\n")
            .Append("    SET @doka_state = (")
            .Append(plan.StateEvaluationGuardFailureExpression ?? "N'unsupported'")
            .Append(");\n    SET @doka_repair_ok = 0;\nEND\n");

        builder.Append("ELSE\nBEGIN\n");

        if (plan.RequiresDelayedBinding || plan.CatalogPreambleSql is not null)
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
            builder.Append("    SET @doka_state = (").Append(plan.StateExpression).Append(");\n");
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
                AppendDelayedScalar(builder, plan.RepairPrecondition ?? string.Empty,
                    "int", "@doka_repair_ok", "        ", coalesce: true);
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

        builder.Append("END;\nSET @doka_action = ");
        AppendActionCase(builder, operation, plan.RepairCapability);

        builder.Append(";\nIF @doka_action = N'reject_different'\nBEGIN\n")
            .Append("    THROW 51001, N'doka_sm_different', 1;\nEND;\n")
            .Append("IF @doka_action = N'reject_unsupported'\nBEGIN\n")
            .Append("    THROW 51002, N'doka_sm_unsupported', 1;\nEND;\n")
            .Append("IF @doka_action = N'reject_data_blocked'\nBEGIN\n")
            .Append("    THROW 51003, N'doka_sm_data_blocked', 1;\nEND;\n")
            .Append("IF @doka_action = N'reject_prerequisite_missing'\nBEGIN\n")
            .Append("    THROW 51004, N'doka_sm_prerequisite_missing', 1;\nEND;\n")
            .Append("IF @doka_action = N'apply'\nBEGIN\n");

        AppendDynamicCommands(builder, baseline);

        builder.Append("END\nELSE IF @doka_action = N'repair'\nBEGIN\n");
        AppendDynamicCommands(builder, repairBaseline);

        builder.Append("END;\n")
            .Append("IF @doka_action IN (N'apply', N'repair')\nBEGIN\n");

        if (plan.PostApplySql is { Length: > 0 } postApplySql)
        {
            // WHY: A provider-owned provenance mark belongs to the newly
            // created physical object. Write it only after DDL succeeds and
            // before the final catalog postcondition validates that object.
            AppendDynamicSql(builder, postApplySql, "    ");
        }

        if (plan.RequiresDelayedBinding || plan.CatalogPreambleSql is not null)
        {
            builder.Append("    DECLARE @doka_postcondition int;\n");

            AppendDelayedScalar(
                builder,
                plan.ExecutionPostcondition ?? plan.Postcondition ?? string.Empty,
                "int",
                "@doka_postcondition",
                "    ",
                coalesce: true);

            builder.Append("    IF @doka_postcondition <> 1\n");
        }
        else
        {
            builder.Append("    IF COALESCE((")
                .Append(plan.ExecutionPostcondition ?? plan.Postcondition)
                .Append("), 0) <> 1\n");
        }

        builder.Append("    BEGIN\n")
            .Append("        THROW 51005, N'doka_sm_postcondition', 1;\n")
            .Append("    END;\nEND;");

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
        return string.Create(length, body, static (destination, source) =>
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

            GuardScopePrefix.AsSpan().CopyTo(destination);
            GuardScopeSuffix.AsSpan().CopyTo(destination.Slice(destination.Length - GuardScopeSuffix.Length));
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
        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index].AsSpan();
            length = checked(length + statement.Length + statement.Count('\''));
        }

        // WHY: The catalog builder already owns complete immutable statements.
        // Joining and then copying them through a growing literal buffer retains
        // two unnecessary full-size intermediates before the final SQL string.
        return string.Create(length, statements, static (destination, source) =>
        {
            GuardScopePrefix.AsSpan().CopyTo(destination);
            var offset = GuardScopePrefix.Length;
            for (var index = 0; index < source.Count; index++)
            {
                if (index > 0)
                {
                    destination[offset++] = '\n';
                }

                // WHY: string.Join treats null statement entries as empty.
                // Preserve that boundary as well as every other UTF-16 code unit.
                var statement = source[index].AsSpan();
                int apostropheOffset;
                while ((apostropheOffset = statement.IndexOf('\'')) >= 0)
                {
                    statement.Slice(0, apostropheOffset).CopyTo(destination.Slice(offset));
                    offset += apostropheOffset;
                    destination[offset++] = '\'';
                    destination[offset++] = '\'';
                    statement = statement.Slice(apostropheOffset + 1);
                }

                statement.CopyTo(destination.Slice(offset));
                offset += statement.Length;
            }

            GuardScopeSuffix.AsSpan().CopyTo(destination.Slice(offset));
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
    /// <exception cref="ArgumentNullException">The required scalar expression is null.</exception>
    private static void AppendDelayedScalar(
        StringBuilder builder,
        string expression,
        string type,
        string targetVariable,
        string indentation,
        string? preambleSql = null,
        bool coalesce = false
    )
    {
        // WHY: Only the optional preamble treats null as empty. A malformed
        // required expression must still fail before any guard can be emitted.
        ArgumentNullException.ThrowIfNull(expression);

        builder.Append(indentation).Append(GuardScopePrefix);
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

        builder.Append(");', N'@doka_value ")
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

        builder.Append(indentation).Append(GuardScopePrefix);
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
        ArgumentNullException.ThrowIfNull(builder);

        // WHY: Delayed classifiers repeat large catalog expressions inside
        // nested SQL literals. Append unchanged spans and doubled apostrophes
        // directly; every other UTF-16 code unit and null-preamble behavior
        // remain identical to the former ordinal string replacement.
        int apostropheOffset;
        while ((apostropheOffset = sql.IndexOf('\'')) >= 0)
        {
            builder.Append(sql.Slice(0, apostropheOffset)).Append("''");
            sql = sql.Slice(apostropheOffset + 1);
        }

        builder.Append(sql);
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

            builder.Append("WHEN N'").Append(StateCode(state)).Append("' THEN ");
            if (action == SafeMigrationAction.Repair)
            {
                builder.Append("CASE WHEN @doka_repair_ok = 1 THEN N'repair' ")
                    .Append("ELSE N'reject_different' END ");
            }
            else
            {
                builder.Append("N'").Append(ActionCode(action)).Append("' ");
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

    private static bool UsesDefaultSchema(
        SafeMigrationIntent intent
    ) => intent switch
    {
        EnsureSchemaIntent or DropSchemaIntent => false,
        EnsureTableIntent value => value.Definition.Schema is null
            || value.Definition.ForeignKeys.Any(static foreignKey => foreignKey.PrincipalSchema is null),
        DropTableIntent value => value.Schema is null,
        RenameTableIntent value => value.Schema is null,
        EnsureColumnIntent value => value.Schema is null,
        DropColumnIntent value => value.Schema is null,
        RenameColumnIntent value => value.Schema is null,
        AlterColumnIntent value => value.Schema is null,
        EnsureIndexIntent value => value.Definition.Schema is null,
        DropIndexIntent value => value.Schema is null,
        RenameIndexIntent value => value.Schema is null,
        EnsurePrimaryKeyIntent value => value.Definition.Schema is null,
        DropPrimaryKeyIntent value => value.Schema is null,
        EnsureUniqueConstraintIntent value => value.Definition.Schema is null,
        DropUniqueConstraintIntent value => value.Schema is null,
        EnsureCheckConstraintIntent value => value.Definition.Schema is null,
        DropCheckConstraintIntent value => value.Schema is null,
        EnsureForeignKeyIntent value => value.Definition.Schema is null
            || value.Definition.PrincipalSchema is null,
        DropForeignKeyIntent value => value.Schema is null,
        ModelManagedDataIntent value => value.Schema is null,
        _ => throw new UnreachableException(),
    };

    private static void ValidateTemporalOperationBoundary(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var hasOrdinaryOperation = false;
        var hasSafeBoundaryAfterOrdinary = false;
        var hasOrdinaryOperationsAcrossBoundary = false;
        var hasTemporalMetadata = false;

        foreach (var operation in operations)
        {
            if (operation is SafeMigrationOperation)
            {
                hasSafeBoundaryAfterOrdinary |= hasOrdinaryOperation;

                continue;
            }

            if (operation is SafeMigrationDesignTimeServicesRequiredOperation)
            {
                continue;
            }

            hasOrdinaryOperationsAcrossBoundary |= hasSafeBoundaryAfterOrdinary;
            hasOrdinaryOperation = true;
            hasTemporalMetadata |= HasTemporalMetadata(operation);
        }

        if (!hasOrdinaryOperationsAcrossBoundary || !hasTemporalMetadata)
        {
            return;
        }

        // WHY: EF Core's SQL Server generator derives temporal-table state
        // from its whole operation list before rewriting column operations.
        // Separate baseline calls cannot preserve that state across a Safe
        // operation inserted between two ordinary EF operation segments.
        throw new NotSupportedException(
            "SQL Server temporal operations cannot cross a SafeMigrations operation boundary.");
    }

    private static bool HasTemporalMetadata(
        MigrationOperation operation
    )
    {
        if (HasTemporalAnnotation(operation.GetAnnotations()))
        {
            return true;
        }

        return operation switch
        {
            AlterTableOperation value => HasTemporalAnnotation(value.OldTable.GetAnnotations()),
            AlterColumnOperation value => HasTemporalAnnotation(value.OldColumn.GetAnnotations()),
            CreateTableOperation value => value.Columns.Any(
                static column => HasTemporalAnnotation(column.GetAnnotations())),
            _ => false,
        };
    }

    private static bool HasTemporalAnnotation(
        IEnumerable<IAnnotation> annotations
    ) => annotations.Any(static annotation => annotation.Name is "SqlServer:IsTemporal"
        || annotation.Name.StartsWith("SqlServer:Temporal", StringComparison.Ordinal));
}
