namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>
/// Composes the ordinary EF Core SQL Server migrations generator with guarded
/// SafeMigrations commands. Ordinary migration operations remain unchanged.
/// </summary>
/// <remarks>
/// Managed migration commands recover only their own acquired IDENTITY_INSERT state
/// after an execution failure. SQL-only clients should generate with
/// <see cref="MigrationsSqlGenerationOptions.Script" /> and must recover or close their
/// session after cancellation or timeout; SQL Server TRY/CATCH cannot handle client attentions.
/// Failed session recovery requires a new context for supported migration and catalog
/// execution. This boundary does not intercept arbitrary EF queries or standalone SQL scripts.
/// </remarks>
public sealed partial class SqlServerSafeMigrationsSqlGenerator : IMigrationsSqlGenerator
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
        // Native CHECK dependency guards, not CLR case folding, admit renames.
        var tableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(
            operations,
            static schema => schema ?? "dbo",
            StringComparer.Ordinal,
            '[',
            foldUnquotedCapturedSqlIdentifiers: false);

        var plans = new SqlServerSafeMigrationRuntimePlan?[operations.Count];
        var safeOperationCount = 0;

        // WHY: Retain each safe plan at its authored ordinal before emitting any commands.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
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
                    SafeMigrationDiagnosticMessages.FormatRuntime(safeOperation,
                        "doka_sm_unsupported", ordinal, plan.UnsupportedCode));
            }

            plans[ordinal] = plan;
            safeOperationCount++;
        }

        var safeOperations = ExtractSafeOperations(operations, safeOperationCount);
        var metadataCommand = _dependencies.CommandBuilderFactory.Create().Build();

        var commands = new List<MigrationCommand>(operations.Count);

        // WHY: Preserve authored ordinals while coalescing uninterrupted ordinary EF runs.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
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

            var baseline = RenderBaseline(safeOperation, plan, model, options, ordinal);
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
                    "A supported SQL Server SafeMigrations operation has no baseline command. "
                    + SafeMigrationDiagnosticMessages.FormatIdentity(
                        SafeMigrationOperationIdentity.Create(safeOperation.Intent))
                    + "; next=Inspect the configured provider baseline generator; no DDL was approved.");
            }

            if (safeOperation.Policy == SafeMigrationPolicy.RepairIfSafe
                && plan.RepairCapability == SafeMigrationRepairCapability.Safe
                && effectiveRepairBaseline.Count == 0)
            {
                throw new InvalidOperationException(
                    "A repairable SQL Server SafeMigrations operation has no repair command. "
                    + SafeMigrationDiagnosticMessages.FormatIdentity(
                        SafeMigrationOperationIdentity.Create(safeOperation.Intent))
                    + "; next=Inspect the configured provider repair baseline; no repair was approved.");
            }

            if (HasTransactionSuppressedCommand(baseline)
                || HasTransactionSuppressedCommand(effectiveRepairBaseline))
            {
                throw new NotSupportedException(
                    SafeMigrationDiagnosticMessages.FormatRuntime(safeOperation,
                        "doka_sm_unsupported", ordinal, "baseline_transaction_suppressed"));
            }

            var guarded = new SqlOperation
            {
                Sql = BuildGuardedSql(safeOperation, plan, baseline, effectiveRepairBaseline,
                    scriptIdentityScope: (options & MigrationsSqlGenerationOptions.Script) != 0,
                    operationOrdinal: ordinal),
            };

            var guardedCommands = _baselineGenerator.Generate([guarded], model, options);
            ValidateGuardCommands(guardedCommands);
            if (safeOperation.Intent is EnsureModelManagedDataIntent data
                && (options & MigrationsSqlGenerationOptions.Script) == 0)
            {
                var table = _sqlGenerationHelper.DelimitIdentifier(data.Table, data.Schema ?? "dbo");
                // WHY: An acquired marker already certifies the original ON.
                // Re-reading metadata after a failure could hide that fact or
                // require rights on a rejected command that never acquired ON.
                var cleanupSql = "SET IDENTITY_INSERT " + table + " OFF;";

                // WHY: Wrap each completed command in provider order without an interface iterator.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < guardedCommands.Count; index++)
                {
                    commands.Add(new SqlServerSafeMigrationIdentityInsertCommand(
                        guardedCommands[index],
                        cleanupSql,
                        _dependencies,
                        analyzer,
                        metadataCommand,
                        safeOperation,
                        ordinal));
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

        // WHY: Fill the pre-counted safe-operation array in original stream order.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
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
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
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
        // WHY: Indexing avoids an interface enumerator while checking each completed provider batch.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index].TransactionSuppressed)
            {
                return true;
            }
        }

        return false;
    }
}
