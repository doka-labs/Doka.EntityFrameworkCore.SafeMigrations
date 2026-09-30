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
        }

        var safeOperations = operations.OfType<SafeMigrationOperation>().ToArray();
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
                commands.AddRange(ordinaryCommands.Select(command =>
                    new SqlServerSafeMigrationGuardedCommand(command, _dependencies, analyzer, metadataCommand)));
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
                && safeOperation.Intent is not EnsureSchemaIntent { Name: "dbo" })
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

            if (baseline.Any(static command => command.TransactionSuppressed)
                || effectiveRepairBaseline.Any(static command => command.TransactionSuppressed))
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

                commands.AddRange(guardedCommands.Select(command =>
                    new SqlServerSafeMigrationIdentityInsertCommand(
                        command,
                        cleanupSql,
                        _dependencies,
                        analyzer,
                        metadataCommand)));
            }
            else
            {
                commands.AddRange(guardedCommands.Select(command =>
                    new SqlServerSafeMigrationGuardedCommand(command, _dependencies, analyzer, metadataCommand)));
            }
        }

        if (safeOperations.Length > 0)
        {
            // WHY: The analyzer and script path must use one CATALOG_DEFAULT
            // identity contract. Keep its bounded VALUES fragments in one
            // dynamic scope so attention cannot leave temporary caller state.
            var guardSql = string.Join("\n", SqlServerIdentifierContract.BuildCollisionGuardCommands(
                SqlServerIdentifierContract.Collect(safeOperations),
                throwOnCollision: true));

            var guardBuilder = new StringBuilder();
            AppendDynamicSql(guardBuilder, guardSql, string.Empty);
            var identifierGuard = new SqlOperation
            {
                Sql = guardBuilder.ToString(),
            };

            var identifierCommands = _baselineGenerator.Generate([identifierGuard], model, options);
            ValidateGuardCommands(identifierCommands);
            commands.InsertRange(0, identifierCommands.Select(command =>
                new SqlServerSafeMigrationGuardedCommand(command, _dependencies, analyzer, metadataCommand)));
        }

        return commands.AsReadOnly();
    }

    private static void ValidateGuardCommands(IReadOnlyList<MigrationCommand> commands)
    {
        if (commands.Count == 0)
        {
            throw new InvalidOperationException("A SQL Server SafeMigrations guard has no generated command.");
        }

        if (commands.Any(static command => command.TransactionSuppressed))
        {
            throw new NotSupportedException("A SQL Server SafeMigrations guard cannot suppress its transaction.");
        }
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

        var baselineOperation = SafeMigrationStandardOperationFactory.Create(
            operation.Intent,
            _expressionRenderer.Render,
            static collation => collation.Schema is null ? collation.Name : null);

        if (operation.Intent is EnsureIndexIntent { Definition.IncludedColumns.Count: > 0 } index
            && baselineOperation is CreateIndexOperation createIndex)
        {
            // WHY: Core's provider-neutral factory cannot carry included columns.
            // SQL Server's EF generator consumes this provider annotation.
            createIndex["SqlServer:Include"] = index.Definition.IncludedColumns.ToArray();
        }

        return _baselineGenerator.Generate([baselineOperation], model, options);
    }

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

    private static string BuildGuardedSql(
        SafeMigrationOperation operation,
        SqlServerSafeMigrationRuntimePlan plan,
        IReadOnlyList<MigrationCommand> baseline,
        IReadOnlyList<MigrationCommand> repairBaseline
    )
    {
        var actionCase = BuildActionCase(operation, plan.RepairCapability);
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
            builder.Append("IF COALESCE(SCHEMA_NAME(), N'') <> N'dbo'\nBEGIN\n")
                .Append("    THROW 51004, N'doka_sm_prerequisite_missing', 1;\nEND;\n");
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
            .Append(");\n    SET @doka_repair_ok = 0;\nEND\nELSE\nBEGIN\n");

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

            AppendDelayedScalar(
                builder,
                $"COALESCE(({plan.RepairPrecondition}), 0)",
                "int",
                "@doka_repair_ok",
                "    ");
        }
        else
        {
            builder.Append("    SET @doka_state = (").Append(plan.StateExpression).Append(");\n")
                .Append("    SET @doka_repair_ok = COALESCE((")
                .Append(plan.RepairPrecondition).Append("), 0);\n");
        }

        builder.Append("END;\n")
            .Append("SET @doka_action = ").Append(actionCase).Append(";\n")
            .Append("IF @doka_action = N'reject_different'\nBEGIN\n")
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
                $"COALESCE(({plan.ExecutionPostcondition ?? plan.Postcondition}), 0)",
                "int",
                "@doka_postcondition",
                "    ");

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

        return builder.ToString();
    }

    private static void AppendDelayedScalar(
        StringBuilder builder,
        string expression,
        string type,
        string targetVariable,
        string indentation,
        string? preambleSql = null
    )
    {
        builder.Append(indentation)
            .Append("EXEC sys.sp_executesql N'")
            .Append(preambleSql?.Replace("'", "''", StringComparison.Ordinal))
            .Append("\nSET @doka_value = (")
            .Append(expression.Replace("'", "''", StringComparison.Ordinal))
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

        foreach (var command in commands)
        {
            // WHY: Compile each DDL statement only after runtime catalog
            // classification. Otherwise SQL Server can bind absent objects in
            // an IF branch that would never execute.
            AppendDynamicSql(builder, command.CommandText, "    ");
        }
    }

    private static void AppendDynamicSql(
        StringBuilder builder,
        string sql,
        string indentation
    ) => builder.Append(indentation)
        .Append("EXEC sys.sp_executesql N'")
        .Append(sql.Replace("'", "''", StringComparison.Ordinal))
        .Append("';\n");

    private static string BuildActionCase(
        SafeMigrationOperation operation,
        SafeMigrationRepairCapability repairCapability
    )
    {
        var builder = new StringBuilder("CASE @doka_state ");
        foreach (var state in s_observedStates)
        {
            var decision = SafeMigrationDecisionPlanner.Plan(
                operation.Intent.Kind,
                state,
                operation.Policy,
                repairCapability);

            builder.Append("WHEN N'").Append(StateCode(state)).Append("' THEN ");
            if (decision.Action == SafeMigrationAction.Repair)
            {
                builder.Append("CASE WHEN @doka_repair_ok = 1 THEN N'repair' ")
                    .Append("ELSE N'reject_different' END ");
            }
            else
            {
                builder.Append("N'").Append(ActionCode(decision.Action)).Append("' ");
            }
        }

        return builder.Append("ELSE N'reject_unsupported' END").ToString();
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
