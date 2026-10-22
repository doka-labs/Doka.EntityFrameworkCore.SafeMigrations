namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer : ISafeMigrationProviderAnalyzer,
    ISafeMigrationProviderOperationProjection, ISafeMigrationProjectedKeyAnalyzer,
    ISafeMigrationProviderObjectIdentityNormalizer
{
    private readonly MySqlSafeMigrationPlanCapture _planCapture;
    private readonly IMigrationsSqlGenerator _sqlGenerator;
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private string? _currentDatabase;
    private bool _tableNamesIgnoreCase;
    private MySqlGeneratedColumnDependencyContractCatalog? _generatedColumnDependencyContracts;
    private bool _generatedColumnDependencyIsMariaDb;
    private bool _requiresGeneratedDependencyQualification;

    /// <summary>
    /// Uses the independently captured native owner policy without folding mode-zero table identities.
    /// </summary>
    private StringComparer TableIdentifierComparer =>
        _tableNamesIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Canonicalizes an owner only after the server proves case-insensitive physical identity.</summary>
    private string NormalizeTableIdentifier(
        string identifier
    ) => _tableNamesIgnoreCase
        ? SafeMigrationModelManagedKeyIdentity.NormalizeOrdinalIgnoreCase(identifier)
        : identifier;

    public MySqlSafeMigrationProviderAnalyzer(
        IMigrationsSqlGenerator sqlGenerator,
        MySqlSafeMigrationPlanCapture planCapture,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        ArgumentNullException.ThrowIfNull(sqlGenerator);
        ArgumentNullException.ThrowIfNull(planCapture);
        ArgumentNullException.ThrowIfNull(typeMappingSource);

        _sqlGenerator = sqlGenerator;
        _planCapture = planCapture;
        _typeMappingSource = typeMappingSource;
    }

    public string ProviderId => "doka_mysql";

    bool ISafeMigrationProjectedKeyAnalyzer.SharesUniqueConstraintAndIndexIdentity => true;

    StringComparer ISafeMigrationProviderObjectIdentityNormalizer.IdentifierComparer => TableIdentifierComparer;

    StringComparer ISafeMigrationProviderObjectIdentityNormalizer.ColumnIdentifierComparer =>
        StringComparer.OrdinalIgnoreCase;

    /// <inheritdoc />
    char? ISafeMigrationProviderObjectIdentityNormalizer.CapturedSqlIdentifierDelimiter => '`';

    /// <inheritdoc />
    StringComparer ISafeMigrationProviderObjectIdentityNormalizer.IndexIdentifierComparer =>
        StringComparer.OrdinalIgnoreCase;

    StringComparer ISafeMigrationProviderObjectIdentityNormalizer.ForeignKeyIdentifierComparer =>
        StringComparer.OrdinalIgnoreCase;

    string ISafeMigrationProviderObjectIdentityNormalizer.NormalizeIdentifier(
        string identifier
    ) => NormalizeTableIdentifier(identifier);

    /// <inheritdoc />
    string ISafeMigrationProviderObjectIdentityNormalizer.NormalizeColumnIdentifier(
        string identifier
    ) => SafeMigrationModelManagedKeyIdentity.NormalizeOrdinalIgnoreCase(identifier);

    string? ISafeMigrationProviderObjectIdentityNormalizer.NormalizeSchema(
        string? schema
    ) => schema is not null && StringComparer.Ordinal.Equals(schema, _currentDatabase) ? null : schema;

    bool ISafeMigrationProviderObjectIdentityNormalizer.IsObjectIdentityMismatch(
        SafeMigrationProviderAnalysis analysis
    ) => analysis.ObservedState == SafeMigrationObservedState.Unsupported
        && StringComparer.Ordinal.Equals(analysis.Code, "database_qualifier_mismatch");

    bool ISafeMigrationProviderOperationProjection.PreservesExistingTableState(
        MigrationOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        // WHY: Doka 10.3.x maps this operation only to ALTER DATABASE's
        // character-set default. Existing tables, columns, indexes,
        // constraints, and rows retain their catalog state.

        return operation is AlterDatabaseOperation;
    }

    public void ValidateContext(
        DbContext context
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        // Validate the current connection instance rather than the immutable
        // options snapshot. EF may reuse an internal service provider after a
        // consumer replaces the relational connection for the same context
        // type, but no SafeMigrations SQL may escape under invalid settings.
        MySqlSafeMigrationConnectionValidator.Validate(context.Database.GetDbConnection());
    }

    public async Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var serverVersion = MySqlServerVersion.AutoDetect(
                connection,
                MySqlServerVersionCompatibilityMode.AllowUnsupported);

            return new SafeMigrationProviderEnvironment(
                ProviderId,
                serverVersion.IsMariaDb ? "mariadb" : "mysql",
                connection.ServerVersion);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var migrationLock = await context
            .GetService<IHistoryRepository>()
            .AcquireDatabaseLockAsync(cancellationToken);

        return new AnalysisScope(migrationLock);
    }

    public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken = default
    ) => AnalyzeCoreAsync(context, operations, captureRenameSources: true, cancellationToken);

    /// <summary>Analyzes one immutable snapshot without recursively capturing rename-source evidence.</summary>
    private async Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeCoreAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        bool captureRenameSources,
        CancellationToken cancellationToken,
        MySqlBackfillConstraintCatalog? inheritedBackfillConstraints = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);

        if (captureRenameSources)
        {
            _projectedAlterPhysicalSnapshots.Clear();
            _projectedExplicitTimestampDefaults.Clear();
            _projectedColumnSessionEnvironments.Clear();
            _projectedRequiredAddCapsByOperation?.Clear();
            _projectedRequiredAddCapsByAnalysis?.Clear();
            _renamedSourceAnalyses.Clear();
            _renameTargetConflicts.Clear();
            ClearLivePrerequisites();
            _tableNamesIgnoreCase = false;
            _expiredGeneratedDependencyOwners = null;
            _requiresGeneratedDependencyQualification = operations.Any(static operation =>
                operation.Intent is RenameColumnIntent or DropColumnIntent);
        }

        if (operations.Count == 0)
        {
            return [];
        }

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        Exception? analysisFailure = null;
        try
        {
            MySqlSafeMigrationConnectionValidator.Validate(connection);

            var commandTimeout = context.Database.GetCommandTimeout();
            _currentDatabase = null;
            if (operations.Any(static operation =>
                    MySqlSafeMigrationCatalogSqlBuilder.HasDatabaseQualifier(operation.Intent)))
            {
                _currentDatabase = await ReadCurrentDatabaseAsync(connection, commandTimeout, cancellationToken);
            }

            var maximumPayloadBytes = await GetMaximumPayloadBytesAsync(connection, commandTimeout, cancellationToken);
            var backfillConstraints = inheritedBackfillConstraints
                ?? MySqlBackfillConstraintCatalog.Create(operations, _currentDatabase, TableIdentifierComparer);

            var physicalKeyOperations = operations
                .Where(operation => TargetsCurrentDatabase(operation.Intent, _currentDatabase))
                .ToArray();
            var columnSessionEnvironment = await ReadProjectedColumnSessionEnvironmentAsync(
                connection,
                physicalKeyOperations,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            var indexEnvironments = await ReadIndexPhysicalEnvironmentsAsync(
                connection,
                physicalKeyOperations,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken,
                TableIdentifierComparer);

            var alterPhysicalSnapshots = await ReadProjectedAlterPhysicalSnapshotsAsync(
                connection,
                physicalKeyOperations,
                indexEnvironments,
                maximumPayloadBytes,
                commandTimeout,
                cancellationToken);

            var expectedUniqueIndexes = MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes(
                operations,
                _currentDatabase,
                TableIdentifierComparer);

            var expectedTableConstraints = SafeMigrationExpectedTableConstraints.FromOperations(
                operations,
                schema => MySqlTableIdentity.NormalizeDatabase(schema, _currentDatabase),
                TableIdentifierComparer,
                '`',
                false,
                StringComparer.OrdinalIgnoreCase);

            var dependencyOperations = captureRenameSources && _analysisOperationStream is { } fullStream
                ? fullStream
                : operations;

            var generatedCandidates = MySqlGeneratedColumnModelCandidates.CaptureTargets(
                dependencyOperations,
                captureRenameSources ? _analysisTargetModels : null,
                context.Model,
                context.GetService<IModelRuntimeInitializer>());

            _generatedColumnDependencyContracts = MySqlGeneratedColumnDependencyContractCatalog.Create(
                dependencyOperations,
                _currentDatabase,
                TableIdentifierComparer,
                generatedCandidates);
            if (captureRenameSources)
            {
                // WHY: Normalize only after this invocation has read its native
                // owner profile. Rebound rename probes share the analyzer but
                // must not replace the root stream's index lifetime facts.
                PrepareGeneratedIndexIdentityCapture(operations);
            }

            // WHY: MariaDB spills every INFORMATION_SCHEMA subquery of a classification statement to
            // an on-disk temporary table, so the catalog of the connected database is copied once
            // per operation window and classified against instead. MySQL keeps the equivalent work
            // in memory and rejects a second reference to a temporary table in one statement, so it
            // stays on the live views. See D-014.
            var isMariaDb = MySqlServerVersion.AutoDetect(
                    connection,
                    MySqlServerVersionCompatibilityMode.AllowUnsupported)
                .IsMariaDb;
            if (captureRenameSources)
            {
                _generatedColumnDependencyIsMariaDb = isMariaDb;
            }

            var snapshotEligible = isMariaDb;

            var results = new SafeMigrationProviderAnalysis[operations.Count];
            var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
            var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
            var dataProbeCache = new Dictionary<MySqlDataProbeContract, MySqlDataProbeResult>();
            var blockingDataCache = new Dictionary<MySqlDataProbeIdentity, bool>();
            var primaryKeyReplacements = MySqlSafeMigrationPlanCapture.CreatePrimaryKeyReplacementTargets(
                operations,
                _currentDatabase,
                TableIdentifierComparer);

            var operationOffset = 0;
            foreach (var operationWindow in operations.Chunk(
                         SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
            {
                var plans = CapturePlans(
                    operationWindow,
                    context.Model,
                    expectedUniqueIndexes,
                    expectedTableConstraints,
                    backfillConstraints,
                    primaryKeyReplacements: primaryKeyReplacements);

                AttachIndexPhysicalEnvironments(operationWindow, plans, indexEnvironments);
                AttachGeneratedIndexIdentityCapture(operationWindow, plans);
                var shortCircuitStates = await FindPrerequisiteShortCircuitStatesAsync(
                    connection,
                    plans,
                    maximumPayloadBytes,
                    commandTimeout,
                    operationOffset,
                    cancellationToken);

                CaptureGeneratedIndexIdentityFacts(operationWindow, plans);

                // WHY: Explicit qualifiers keep live reads, including a foreign key's principal.
                // Unqualified operations use local catalog rows plus incoming foreign keys owned
                // by other databases; those dependencies must not disappear from the snapshot.
                var bindable = new bool[operationWindow.Length];
                var bindableCount = 0;
                // WHY: Keep window-local plans and result slots aligned with their original operation ordinals.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var ordinal = 0; ordinal < operationWindow.Length; ordinal++)
                {
                    bindable[ordinal] = shortCircuitStates[ordinal] is null
                        && !plans[ordinal].IsStaticallyUnsupported
                        && !MySqlSafeMigrationCatalogSqlBuilder.HasDatabaseQualifier(operationWindow[ordinal].Intent);

                    if (bindable[ordinal])
                    {
                        bindableCount++;
                    }
                }

                // WHY: Copying the catalog costs a fixed set of statements, so it only pays once a
                // window classifies at least a full statement's worth of operations against it. A
                // migration of a handful of operations stays on the live views, where the copy would
                // be pure overhead rather than a saving.
                var useSnapshot = snapshotEligible
                    && bindableCount >= SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;

                await using var catalogSnapshot = useSnapshot
                    ? await MySqlCatalogSnapshot.TryCreateAsync(connection, commandTimeout, cancellationToken)
                    : null;

                if (useSnapshot && catalogSnapshot is null)
                {
                    // WHY: An unavailable snapshot capability applies to this analysis session.
                    // Do not repeat the same failed DDL for every capture window.
                    snapshotEligible = false;
                }

                try
                {
                    if (catalogSnapshot is not null)
                    {
                        // WHY: Keep window-local plans and result slots aligned with their original operation ordinals.
                        // ReSharper disable once ForCanBeConvertedToForeach
                        // ReSharper disable once LoopCanBeConvertedToQuery
                        for (var ordinal = 0; ordinal < plans.Length; ordinal++)
                        {
                            if (bindable[ordinal])
                            {
                                plans[ordinal] = plans[ordinal] with { CatalogRelations = catalogSnapshot.Resolve };
                            }
                        }
                    }

                    await FindRequiredColumnAddFactsAsync(
                        connection,
                        plans,
                        shortCircuitStates,
                        maximumPayloadBytes,
                        commandTimeout,
                        operationOffset,
                        cancellationToken);

                    await FindBinaryDefaultProofsAsync(
                        connection,
                        plans,
                        shortCircuitStates,
                        maximumPayloadBytes,
                        commandTimeout,
                        operationOffset,
                        cancellationToken);

                    await FindIdentifierReferenceProofsAsync(
                        connection,
                        plans,
                        shortCircuitStates,
                        maximumPayloadBytes,
                        commandTimeout,
                        operationOffset,
                        cancellationToken);

                    await FindStateEvaluationGuardStatesAsync(
                        connection,
                        plans,
                        shortCircuitStates,
                        maximumPayloadBytes,
                        commandTimeout,
                        operationOffset,
                        cancellationToken);

                    var dataProbeResults = await ResolveDataProbeResultsAsync(
                        connection,
                        operationWindow,
                        plans,
                        shortCircuitStates,
                        dataProbeCache,
                        blockingDataCache,
                        context.Model,
                        expectedUniqueIndexes,
                        expectedTableConstraints,
                        backfillConstraints,
                        maximumPayloadBytes,
                        commandTimeout,
                        operationOffset,
                        cancellationToken);

                    // WHY: Keep window-local plans and result slots aligned with their original operation ordinals.
                    // ReSharper disable once ForCanBeConvertedToForeach
                    // ReSharper disable once LoopCanBeConvertedToQuery
                    for (var localOrdinal = 0; localOrdinal < operationWindow.Length; localOrdinal++)
                    {
                        if (shortCircuitStates[localOrdinal] is { } shortCircuitState)
                        {
                            results[operationOffset + localOrdinal] = ShortCircuitAnalysis(
                                shortCircuitState,
                                plans[localOrdinal],
                                operationWindow[localOrdinal].Intent);
                        }
                    }

                    // WHY: Local classifications do not divide the immutable live
                    // snapshot. Pack the unresolved work while retaining the
                    // original operation identities for later ordered projection.
                    var workOrder = SafeMigrationCatalogWorkOrder.Create(
                        operationWindow.Length,
                        ordinal => shortCircuitStates[ordinal] is null,
                        cancellationToken);

                    var workIndex = 0;
                    var captureDropCompleteness = plans.Any(static plan =>
                        plan.DropLeavesNoGeneratedColumnsExpression is not null);
                    while (workIndex < workOrder.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);
                        var submittedOrdinals = new List<int>(
                            SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
                            * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch);

                        var batchParameterCount = 0;
                        var batchPayloadBytes = 0;
                        while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                               && workIndex < workOrder.Length)
                        {
                            var command = batch.CreateCommand();
                            var parameterizer = new MySqlCatalogQueryParameterizer(command, _typeMappingSource);
                            var selections = new List<string>(
                                Math.Min(
                                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                                    workOrder.Length - workIndex));

                            var sqlBytes = trailerBytes;
                            while (workIndex < workOrder.Length
                                   && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                            {
                                var ordinal = workOrder[workIndex];
                                var checkpoint = parameterizer.Capture();
                                var plan = plans[ordinal];
                                MySqlDataProbeResult? dataProbeResult = plan.DataProbe is null
                                    ? null
                                    : dataProbeResults[ordinal]
                                    ?? throw new InvalidOperationException(
                                        "The MySQL narrowing probe returned no classification result.");

                                var stateExpression = plan.DataProbe is null
                                    ? plan.RenderStateExpression(parameterizer.Add)
                                    : plan.RenderStateExpression(
                                        parameterizer.Add,
                                        dataProbeResult!.Value.IsBlocked,
                                        dataProbeResult.Value.IsTransitionEligible);

                                var postcondition = plan.RenderPostcondition(parameterizer.Add);
                                var repairPrecondition = plan.DataProbe is null
                                    ? plan.RenderRepairPrecondition(parameterizer.Add)
                                    : plan.RenderRepairPrecondition(
                                        parameterizer.Add,
                                        dataProbeResult!.Value.IsBlocked,
                                        dataProbeResult.Value.IsTransitionEligible);

                                var classificationCode = plan.ClassificationCodeExpression is null
                                    ? "NULL"
                                    : plan.DataProbe is null
                                        ? plan.RenderClassificationCodeExpression(parameterizer.Add)
                                        : plan.RenderClassificationCodeExpression(
                                            parameterizer.Add,
                                            dataProbeResult!.Value.IsBlocked);

                                var rowEvidence = plan.ModelManagedRowEvidenceExpression is null
                                    ? "NULL"
                                    : plan.RenderModelManagedRowEvidenceExpression(parameterizer.Add);

                                var dependencyCounts = plan.ModelManagedDependencyCountsExpression is null
                                    ? "NULL"
                                    : plan.RenderModelManagedDependencyCountsExpression(parameterizer.Add);

                                var diagnosticEvidence = plan.DiagnosticEvidenceExpression is null
                                    ? "NULL"
                                    : plan.RenderDiagnosticEvidenceExpression(parameterizer.Add);

                                var matchedObjectName = plan.MatchedObjectNameExpression is null
                                    ? "NULL"
                                    : plan.RenderMatchedObjectNameExpression(parameterizer.Add);

                                var resultOrdinal = operationOffset + ordinal;
                                var selection = $"SELECT {resultOrdinal.ToString(CultureInfo.InvariantCulture)}, "
                                    + $"({stateExpression}), "
                                    + $"COALESCE(({postcondition}), FALSE), "
                                    + $"COALESCE(({repairPrecondition}), FALSE), "
                                    + $"({classificationCode}), "
                                    + $"({rowEvidence}), "
                                    + $"({dependencyCounts}), "
                                    + $"({diagnosticEvidence}), "
                                    + $"({matchedObjectName})";

                                if (captureDropCompleteness)
                                {
                                    selection += plan.DropLeavesNoGeneratedColumnsExpression is not null
                                        ? ", " + plan.RenderDropLeavesNoGeneratedColumnsExpression(parameterizer.Add)
                                        : ", NULL";
                                }

                                var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                                    + (selections.Count == 0 ? 0 : separatorBytes);

                                var statementPayload = sqlBytes + selectionBytes + parameterizer.Utf8PayloadBytes;
                                var prospectiveBatchParameters = batchParameterCount + parameterizer.Count;
                                var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                                if (SafeMigrationCatalogQueryLimits.Exceeded(
                                        prospectiveBatchParameters,
                                        prospectiveBatchPayload,
                                        maximumPayloadBytes))
                                {
                                    parameterizer.Rollback(checkpoint);
                                    if (selections.Count == 0)
                                    {
                                        if (batch.Count == 1)
                                        {
                                            batch.RemoveLastCommand(command);

                                            throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                                operationOffset + ordinal,
                                                prospectiveBatchParameters,
                                                prospectiveBatchPayload);
                                        }
                                    }

                                    break;
                                }

                                selections.Add(selection);
                                submittedOrdinals.Add(resultOrdinal);
                                sqlBytes += selectionBytes;
                                workIndex++;
                            }

                            if (selections.Count == 0)
                            {
                                batch.RemoveLastCommand(command);

                                break;
                            }

                            command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                                + SafeMigrationCatalogQueryLimits.Trailer;
                            batchParameterCount += parameterizer.Count;
                            batchPayloadBytes += sqlBytes + parameterizer.Utf8PayloadBytes;
                        }

                        await ReadAnalysisAsync(
                            batch,
                            results,
                            plans,
                            operationWindow,
                            dataProbeResults,
                            operationOffset,
                            submittedOrdinals,
                            cancellationToken);
                    }

                    await PopulateColumnDiagnosticsAsync(
                        connection,
                        operationWindow,
                        results,
                        context.Model,
                        expectedUniqueIndexes,
                        expectedTableConstraints,
                        backfillConstraints,
                        maximumPayloadBytes,
                        commandTimeout,
                        operationOffset,
                        cancellationToken);

                    CaptureProjectedRequiredAddPlans(operationWindow, plans);

                    operationOffset += operationWindow.Length;
                }
                catch (Exception exception)
                {
                    if (catalogSnapshot is not null)
                    {
                        // WHY: Normal disposal may fail after a timeout has closed the connection.
                        // Release owned copies without hiding the original analysis failure.
                        await catalogSnapshot.DisposeAfterFailureAsync(exception);
                    }

                    throw;
                }
            }

            if (results.Any(static result => result is null))
            {
                throw new InvalidOperationException(
                    "The MySQL SafeMigrations classifier returned an inconsistent row count.");
            }

            // WHY: Same-type Ensure repair can use its metadata-only branch,
            // which does not establish the transition's dependency guards.
            // Refine after batched diagnostics and before reference-bound
            // snapshots are attached, retaining only actual type expansion.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var ordinal = 0; ordinal < results.Length; ordinal++)
            {
                var analysis = results[ordinal];
                if (analysis.RepairPreservesLiveValueDomain
                    && !analysis.Differences.Any(static difference =>
                        StringComparer.Ordinal.Equals(difference.Facet, "column_store_type")))
                {
                    results[ordinal] = analysis.WithRepairPreservesLiveValueDomain(false);
                }
                else if (analysis.RepairPreservesLiveValueDomain)
                {
                    results[ordinal] = analysis.WithRepairPreservesPhysicalKeys();
                }
            }

            AttachProjectedAlterPhysicalSnapshots(operations, results, alterPhysicalSnapshots);
            AttachProjectedColumnSessionEnvironment(operations, results, columnSessionEnvironment);
            if (captureRenameSources)
            {
                await CaptureLivePrerequisitesAsync(
                    context,
                    operations,
                    results,
                    maximumPayloadBytes,
                    commandTimeout,
                    cancellationToken);

                await CaptureMultiValuedIndexDropProofsAsync(
                    context,
                    operations,
                    results,
                    maximumPayloadBytes,
                    commandTimeout,
                    cancellationToken);

                await CaptureForeignKeyReplacementProofsAsync(
                    context,
                    operations,
                    results,
                    maximumPayloadBytes,
                    commandTimeout,
                    cancellationToken);

                CaptureGeneratedColumnSourceProofs(operations, results, isMariaDb);
                CompleteGeneratedIndexIdentityFacts(operations, results);
            }

            if (captureRenameSources)
            {
                await ReadMemberRenameTargetEvidenceAsync(
                    connection,
                    operations,
                    results,
                    _currentDatabase,
                    maximumPayloadBytes,
                    commandTimeout,
                    cancellationToken);

                await CaptureRenameSourceAnalysesAsync(
                    context,
                    operations,
                    results,
                    backfillConstraints,
                    cancellationToken);
            }

            return Array.AsReadOnly(results);
        }
        catch (Exception exception)
        {
            analysisFailure = exception;
            if (captureRenameSources)
            {
                ClearLivePrerequisites();
                _tableNamesIgnoreCase = false;
            }

            throw;
        }
        finally
        {
            // The full authored prefix is needed only while bounded SQL plans
            // are captured. Accepted dependency projection uses Core's current
            // member view, not these conditional operation references.
            _generatedColumnDependencyContracts = null;

            if (openedHere)
            {
                try
                {
                    await connection.CloseAsync();
                }
                catch (Exception exception) when (analysisFailure is not null)
                {
                    // WHY: Closing a failed or canceled session can itself fail. Preserve the
                    // primary analysis exception, while keeping the close failure diagnosable.
                    analysisFailure.Data["SafeMigrations.AnalysisConnectionCloseException"] = exception;
                }
            }
        }
    }

    private MySqlSafeMigrationRuntimePlan[] CapturePlans(
        IReadOnlyList<SafeMigrationOperation> operations,
        IModel model,
        MySqlExpectedUniqueIndexCatalog expectedUniqueIndexes,
        IReadOnlyDictionary<(string? Schema, string Table), SafeMigrationExpectedTableConstraints>
            expectedTableConstraints,
        MySqlBackfillConstraintCatalog backfillConstraints,
        bool includeAnalysisEvidence = false,
        bool includeTransitionEvidence = false,
        IReadOnlySet<SafeMigrationOperation>? primaryKeyReplacements = null
    )
    {
        // Doka returns one placeholder command per captured operation. The
        // caller supplies a bounded window while this complete catalog keeps
        // cross-window unique-index semantics intact.
        using var capture = _planCapture.Begin(
            operations,
            expectedUniqueIndexes,
            expectedTableConstraints,
            includeAnalysisEvidence,
            includeTransitionEvidence,
            backfillConstraints,
            _generatedColumnDependencyContracts,
            primaryKeyReplacements);

        _ = _sqlGenerator.Generate(operations, model);

        return capture.Complete();
    }

    private static SafeMigrationObservedState ParseState(
        string state
    ) => state switch
    {
        "missing" => SafeMigrationObservedState.Missing,
        "matching" => SafeMigrationObservedState.Matching,
        "different" => SafeMigrationObservedState.Different,
        "unsupported" => SafeMigrationObservedState.Unsupported,
        "data_blocked" => SafeMigrationObservedState.DataBlocked,
        "prerequisite_missing" => SafeMigrationObservedState.PrerequisiteMissing,
        "transition_ready" => SafeMigrationObservedState.TransitionReady,
        _ => throw new InvalidOperationException("The MySQL SafeMigrations classifier returned an unknown state."),
    };

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

    private const string MariaDbImplicitJsonCheckInventoryJoins = """
                                                                  LEFT JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
                                                                    ON cc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
                                                                   AND cc.TABLE_NAME = tc.TABLE_NAME
                                                                   AND cc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                                                                  LEFT JOIN INFORMATION_SCHEMA.COLUMNS json_column
                                                                    ON json_column.TABLE_SCHEMA = tc.CONSTRAINT_SCHEMA
                                                                   AND json_column.TABLE_NAME = tc.TABLE_NAME
                                                                   AND json_column.COLUMN_NAME = tc.CONSTRAINT_NAME
                                                                  """;

    private const string MariaDbImplicitJsonCheckInventoryExpression = """
                                                                       CASE WHEN tc.CONSTRAINT_TYPE = 'CHECK'
                                                                         AND json_column.DATA_TYPE = 'longtext'
                                                                         AND LOWER(json_column.COLLATION_NAME) = 'utf8mb4_bin'
                                                                         AND LOWER(REPLACE(REPLACE(REPLACE(REPLACE(
                                                                               cc.CHECK_CLAUSE, '`', ''), ' ', ''), '(', ''), ')', ''))
                                                                             = CONCAT('json_valid', LOWER(json_column.COLUMN_NAME))
                                                                       THEN TRUE ELSE FALSE END
                                                                       """;

    private sealed class AnalysisScope : IAsyncDisposable
    {
        private IDisposable? _migrationLock;

        public AnalysisScope(
            IDisposable migrationLock
        )
        {
            _migrationLock = migrationLock;
        }

        public ValueTask DisposeAsync()
        {
            _migrationLock?.Dispose();
            _migrationLock = null;
            return ValueTask.CompletedTask;
        }
    }
}
