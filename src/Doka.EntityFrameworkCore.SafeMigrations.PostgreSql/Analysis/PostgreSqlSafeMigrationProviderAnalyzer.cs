namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationProviderAnalyzer : ISafeMigrationProviderAnalyzer,
    ISafeMigrationIndexPrerequisiteSource, ISafeMigrationProjectedColumnAnalyzer,
    ISafeMigrationProjectedDependencyAnalyzer, ISafeMigrationProviderOperationProjection,
    ISafeMigrationProviderObjectIdentityNormalizer, ISafeMigrationProviderTargetModelAnalyzer,
    ISafeMigrationLivePrerequisiteSource
{
    // PostgreSQL advisory locks are already local to the current database. A
    // fixed signed bigint therefore avoids coercing the database's unsigned OID
    // into an integer while retaining one package-owned analysis lock domain.
    internal const string AnalysisAdvisoryLockSql = "SELECT pg_catalog.pg_advisory_xact_lock(1397574913::bigint);";

    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly PostgreSqlSafeMigrationCatalogSqlBuilder _catalogSqlBuilder;

    /// <summary>Shares native mapping and identifier services across invocation-local analysis phases.</summary>
    /// <param name="typeMappingSource">The active provider's type mapping source.</param>
    /// <param name="sqlGenerationHelper">The active provider's identifier delimiter.</param>
    public PostgreSqlSafeMigrationProviderAnalyzer(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
        _catalogSqlBuilder = new PostgreSqlSafeMigrationCatalogSqlBuilder(typeMappingSource, sqlGenerationHelper);
    }

    /// <inheritdoc />
    public string ProviderId => "npgsql_postgresql";

    /// <summary>Shares provider-certified index dependencies with ordered Core state projection.</summary>
    /// <param name="intent">The immutable authored index operation.</param>
    /// <returns>The same exact physical prerequisite columns used by runtime catalog validation.</returns>
    public IReadOnlyList<string> GetIndexPrerequisiteColumns(
        EnsureIndexIntent intent
    ) => _catalogSqlBuilder.IndexPrerequisiteColumns(intent);

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(
        AlterColumnIntent intent,
        ExpectedColumnDefinition source,
        SafeMigrationProjectedAlterColumnContext context,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis projectedAnalysis
    )
    {
        if (projectedAnalysis.RepairCapability == SafeMigrationRepairCapability.Safe
            || !context.ProvesEmpty
            || !context.HasCompleteTable
            || !_catalogSqlBuilder.SupportsProjectedAlterColumn(intent))
        {
            return projectedAnalysis;
        }

        // WHY: An accepted complete empty table has no values to truncate.
        // PostgreSQL supports VARCHAR length changes with its ordinary indexes
        // and foreign keys; the static provider gate still excludes other types,
        // collation changes and generation. No, absent live row fact is reused.
        return new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe,
            postconditionSatisfied: false,
            "projected_column_transition",
            SafeMigrationOperationalImpact.TableRewritePossible,
            differences: null);
    }

    /// <inheritdoc />
    public void ValidateContext(
        DbContext context
    ) => ArgumentNullException.ThrowIfNull(context);

    /// <inheritdoc />
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
            return new SafeMigrationProviderEnvironment(ProviderId, "postgresql", connection.ServerVersion);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        IDbContextTransaction? transaction = null;
        try
        {
            var currentTransaction = context.Database.CurrentTransaction;
            if (currentTransaction is null)
            {
                transaction = await context.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.RepeatableRead,
                    cancellationToken);
                _ = await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY;", cancellationToken);
            }
            else
            {
                await ValidateCallerOwnedTransactionAsync(
                    currentTransaction,
                    context.Database.GetCommandTimeout(),
                    cancellationToken);
            }

            await using var command = context
                .Database
                .GetDbConnection()
                .CreateCommand();

            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            ApplyCommandTimeout(command, context.Database.GetCommandTimeout());
            command.CommandText = AnalysisAdvisoryLockSql;
            _ = await command.ExecuteScalarAsync(cancellationToken);

            return new AnalysisScope(transaction);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            throw;
        }
    }

    private static async Task ValidateCallerOwnedTransactionAsync(
        IDbContextTransaction transaction,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var dbTransaction = transaction.GetDbTransaction();
        if (dbTransaction.IsolationLevel is not (System.Data.IsolationLevel.RepeatableRead
            or System.Data.IsolationLevel.Serializable))
        {
            throw new InvalidOperationException(
                "PostgreSQL SafeMigrations analysis requires a caller-owned transaction "
                + "to use RepeatableRead or Serializable isolation.");
        }

        var connection = dbTransaction.Connection
            ?? throw new InvalidOperationException(
                "The caller-owned PostgreSQL analysis transaction has no active connection.");

        await using var command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText = "SHOW transaction_read_only;";
        var readOnly = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        if (!StringComparer.OrdinalIgnoreCase.Equals(readOnly, "on"))
        {
            throw new InvalidOperationException(
                "PostgreSQL SafeMigrations analysis requires a caller-owned transaction to be read-only.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);

        ResetProjectedEventTriggerEffects();
        ResetLivePrerequisiteEvidence();
        _columnPreservingIndexDrops = null;
        _currentSchema = null;
        _schemaIdentityMismatchOrdinals.Clear();
        _schemaIdentityOriginOrdinal = null;

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

        try
        {
            var commandTimeout = context.Database.GetCommandTimeout();
            await CaptureCurrentSchemaIdentityAsync(connection, operations, commandTimeout, cancellationToken);
            await CaptureProjectedEventTriggerEffectsAsync(connection, commandTimeout, cancellationToken);
            var shortCircuitStates = await FindShortCircuitStatesAsync(
                connection,
                operations,
                commandTimeout,
                cancellationToken);

            var expectedTableConstraints = PostgreSqlSafeMigrationAnalysisConstraintCatalog.Create(
                _analysisOperationStream ?? operations,
                _schemaIdentityMismatchOrdinals,
                _currentSchema);

            var dataProbeResults = await ResolveDataProbeResultsAsync(
                connection,
                operations,
                shortCircuitStates,
                commandTimeout,
                cancellationToken);

            var results = new SafeMigrationProviderAnalysis[operations.Count];
            var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
            var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
            var operationOffset = 0;
            foreach (var operationWindow in operations.Chunk(
                         SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // WHY: Map window-local ordinals back to their original analysis slots.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var localOrdinal = 0; localOrdinal < operationWindow.Length; localOrdinal++)
                {
                    if (shortCircuitStates[operationOffset + localOrdinal] is { } shortCircuitState)
                    {
                        results[operationOffset + localOrdinal] = ShortCircuitAnalysis(
                            operationOffset + localOrdinal, shortCircuitState, operationWindow[localOrdinal].Intent);
                    }
                }

                // WHY: Keep expensive runtime plans capture-bounded rather
                // than retaining the complete migration's SQL. The full
                // transition catalogs and data proofs above remain shared.
                var plans = new PostgreSqlSafeMigrationRuntimePlan?[operationWindow.Length];
                var workOrder = SafeMigrationCatalogWorkOrder.Create(
                    operationWindow.Length,
                    ordinal => shortCircuitStates[operationOffset + ordinal] is null,
                    cancellationToken);

                var workIndex = 0;
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
                        var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
                        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                            _typeMappingSource,
                            _sqlGenerationHelper,
                            parameters.AddString,
                            parameters.Add);

                        var selections = new List<string>(
                            Math.Min(
                                SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                                workOrder.Length - workIndex));

                        var sqlBytes = trailerBytes;
                        while (workIndex < workOrder.Length
                               && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                        {
                            var localOrdinal = workOrder[workIndex];
                            var ordinal = operationOffset + localOrdinal;
                            var operation = operationWindow[localOrdinal]
                                ?? throw new ArgumentException(
                                    "The operation batch cannot contain null entries.",
                                    nameof(operations));

                            var checkpoint = parameters.Capture();
                            var plan = builder.Build(
                                operation,
                                expectedTableConstraints.Find(ordinal, operation),
                                includeRuntimeIndexInputGuard: false,
                                includeIndexDropColumnProof: _indexDropCatalogReadable);

                            plans[localOrdinal] = plan;
                            PostgreSqlDataProbeResult? dataProbeResult = plan.DataProbe is null
                                ? null
                                : dataProbeResults[ordinal]
                                    ?? throw new InvalidOperationException(
                                        "The PostgreSQL narrowing probe returned no classification result.");

                            var stateExpression = plan.DataProbe is null
                                ? plan.RenderStateExpression()
                                : plan.RenderStateExpression(
                                    dataProbeResult!.Value.IsBlocked,
                                    dataProbeResult.Value.IsTransitionEligible);

                            var repairPrecondition = plan.DataProbe is null
                                ? plan.RenderRepairPrecondition()
                                : plan.RenderRepairPrecondition(
                                    dataProbeResult!.Value.IsBlocked,
                                    dataProbeResult.Value.IsTransitionEligible);

                            var nullabilityProofRequired = plan.DataProbe is null
                                ? plan.RenderNullabilityDataProbeRequiredExpression()
                                : plan.RenderNullabilityDataProbeRequiredExpression(
                                    dataProbeResult!.Value.IsTransitionEligible);

                            var classificationCode = plan.DataProbe is null
                                ? plan.RenderClassificationCodeExpression() ?? "NULL"
                                : plan.RenderClassificationCodeExpression(dataProbeResult!.Value.IsBlocked) ?? "NULL";

                            var postcondition = plan.Postcondition;
                            if (plan.PrerequisiteExpression == "TRUE" && plan.ObjectIdentityGuardExpression != "TRUE")
                            {
                                // WHY: These plans evaluate catalog metadata only. Fold the
                                // owner proof into their existing classification instead of
                                // introducing a prerequisite round trip for a pruned constant.
                                // Plans which read rows retain their earlier identity-first
                                // prerequisite query before any data probe is submitted.
                                var rejectedIdentity = "NOT COALESCE((" + plan.ObjectIdentityGuardExpression
                                    + "), FALSE)";

                                stateExpression = $"CASE WHEN {rejectedIdentity} THEN 'unsupported' "
                                    + $"ELSE ({stateExpression}) END";
                                classificationCode = $"CASE WHEN {rejectedIdentity} "
                                    + $"THEN '{SchemaIdentityMismatchCode}' ELSE ({classificationCode}) END";
                                postcondition = $"CASE WHEN {rejectedIdentity} THEN FALSE "
                                    + $"ELSE ({postcondition}) END";
                                repairPrecondition = $"CASE WHEN {rejectedIdentity} THEN FALSE "
                                    + $"ELSE ({repairPrecondition}) END";
                            }

                            var rowEvidence = plan.ModelManagedRowEvidenceExpression ?? "NULL";
                            var dependencyCounts = plan.ModelManagedDependencyCountsExpression ?? "NULL";
                            var diagnosticEvidence = plan.DiagnosticEvidenceExpression ?? "NULL";
                            var matchedObjectName = plan.MatchedObjectNameExpression ?? "NULL";
                            var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, "
                                + $"({stateExpression})::text, "
                                + $"COALESCE(({postcondition}), FALSE), "
                                + $"COALESCE(({repairPrecondition}), FALSE), "
                                + $"({classificationCode}), "
                                + $"({rowEvidence}), "
                                + $"({dependencyCounts}), "
                                + $"({diagnosticEvidence}), "
                                + $"({matchedObjectName}), "
                                + $"COALESCE(({nullabilityProofRequired}), FALSE)";

                            var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                                + (selections.Count == 0 ? 0 : separatorBytes);

                            var statementPayload = sqlBytes + selectionBytes + parameters.Utf8PayloadBytes;
                            var prospectiveBatchParameters = batchParameterCount + parameters.Count;
                            var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                            if (SafeMigrationCatalogQueryLimits.Exceeded(
                                    prospectiveBatchParameters,
                                    prospectiveBatchPayload,
                                    SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                            {
                                parameters.Rollback(checkpoint);
                                if (selections.Count == 0)
                                {
                                    if (batch.Count == 1)
                                    {
                                        batch.RemoveLastCommand(command);

                                        throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                            ordinal,
                                            prospectiveBatchParameters,
                                            prospectiveBatchPayload);
                                    }

                                    break;
                                }

                                break;
                            }

                            selections.Add(selection);
                            submittedOrdinals.Add(ordinal);
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
                        batchParameterCount += parameters.Count;
                        batchPayloadBytes += sqlBytes + parameters.Utf8PayloadBytes;
                    }

                    await ReadAnalysisAsync(
                        batch,
                        results,
                        plans,
                        dataProbeResults,
                        operations,
                        operationOffset,
                        submittedOrdinals,
                        cancellationToken);
                }

                operationOffset += operationWindow.Length;
            }

            if (results.Any(static result => result is null))
            {
                throw new InvalidOperationException(
                    "The PostgreSQL SafeMigrations classifier returned an inconsistent row count.");
            }

            await PopulateColumnDiagnosticsAsync(
                connection,
                operations,
                results,
                commandTimeout,
                cancellationToken);

            await PopulateRenameTargetEvidenceAsync(
                connection,
                operations,
                results,
                commandTimeout,
                cancellationToken);

            await PopulateLivePrerequisiteEvidenceAsync(
                connection, operations, results, commandTimeout, cancellationToken);

            await PopulateIndexDataProofsAsync(
                connection, operations, results, commandTimeout, cancellationToken);

            return Array.AsReadOnly(results);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<SafeMigrationObservedState?[]> FindShortCircuitStatesAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var states = new SafeMigrationObservedState?[operations.Count];

        // PostgreSQL resolves every relation referenced by one SQL statement
        // before CASE can select a branch. Keep catalog-only prerequisites in
        // their own statement so a data probe is never planned for a missing
        // table.
        await FindPrerequisiteStatesAsync(connection, operations, states, commandTimeout, cancellationToken);
        await FindStateEvaluationGuardStatesAsync(connection, operations, states, commandTimeout, cancellationToken);

        return states;
    }

    private async Task FindPrerequisiteStatesAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationObservedState?[] states,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var ordinal = 0;
        List<int>? indexInputOrdinals = null;
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (ordinal < operations.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);
            var selectedOrdinals = new List<int>(
                SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
                * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch);

            var batchParameterCount = 0;
            var batchPayloadBytes = 0;
            while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                   && ordinal < operations.Count)
            {
                var command = batch.CreateCommand();
                var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
                var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                    _typeMappingSource,
                    _sqlGenerationHelper,
                    parameters.AddString,
                    parameters.Add);

                var selections = new List<string>(
                    Math.Min(
                        SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement,
                        operations.Count - ordinal));

                var sqlBytes = trailerBytes;
                while (ordinal < operations.Count
                       && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var operation = operations[ordinal]
                        ?? throw new ArgumentException(
                            "The operation batch cannot contain null entries.",
                            nameof(operations));

                    var checkpoint = parameters.Capture();
                    var prerequisite = builder.BuildPrerequisiteExpression(operation);
                    // WHY: This exact builder constant needs no live catalog
                    // proof before classification, which evaluates metadata
                    // only for these operations and folds in their native owner
                    // guard. Row-reading plans keep a nonconstant prerequisite.
                    if (StringComparer.Ordinal.Equals(prerequisite, "TRUE"))
                    {
                        parameters.Rollback(checkpoint);
                        ordinal++;

                        continue;
                    }

                    var identityGuard = builder.BuildObjectIdentityGuard(operation.Intent);
                    var indexInputState = "NULL";
                    if (operation.Intent is EnsureIndexIntent index && !operation.GetAnnotations().Any()
                        && builder.GetUnsupportedFeature(operation.Intent) is null)
                    {
                        indexInputState = builder.BuildIndexInputRoutingState(index);
                    }

                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE "
                        + $"WHEN NOT COALESCE(({identityGuard}), FALSE) THEN 'schema_search_path_mismatch' "
                        + $"WHEN NOT COALESCE(({prerequisite}), FALSE) "
                        + "THEN 'prerequisite_missing' "
                        + $"ELSE ({indexInputState}) END";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + (selections.Count == 0 ? 0 : separatorBytes);

                    var statementPayload = sqlBytes + selectionBytes + parameters.Utf8PayloadBytes;
                    var prospectiveBatchParameters = batchParameterCount + parameters.Count;
                    var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                    if (SafeMigrationCatalogQueryLimits.Exceeded(
                            prospectiveBatchParameters,
                            prospectiveBatchPayload,
                            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                    {
                        parameters.Rollback(checkpoint);
                        if (selections.Count == 0)
                        {
                            if (batch.Count == 1)
                            {
                                batch.RemoveLastCommand(command);

                                throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                    ordinal,
                                    prospectiveBatchParameters,
                                    prospectiveBatchPayload);
                            }
                        }

                        break;
                    }

                    selections.Add(selection);
                    selectedOrdinals.Add(ordinal);
                    sqlBytes += selectionBytes;
                    ordinal++;
                }

                if (selections.Count == 0)
                {
                    batch.RemoveLastCommand(command);

                    break;
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;
                batchParameterCount += parameters.Count;
                batchPayloadBytes += sqlBytes + parameters.Utf8PayloadBytes;
            }

            if (batch.Count == 0)
            {
                continue;
            }

            var candidates = await ReadPrerequisiteBatchAsync(batch, states, selectedOrdinals, cancellationToken);
            if (candidates is not null)
            {
                (indexInputOrdinals ??= []).AddRange(candidates);
            }
        }

        if (indexInputOrdinals is not null)
        {
            await FindIndexInputStatesAsync(connection, operations, states, indexInputOrdinals,
                commandTimeout, cancellationToken);
        }
    }

    private async Task FindStateEvaluationGuardStatesAsync(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations,
        SafeMigrationObservedState?[] states,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var ordinal = 0;
        var separatorBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Separator);
        var trailerBytes = Encoding.UTF8.GetByteCount(SafeMigrationCatalogQueryLimits.Trailer);
        while (ordinal < operations.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout);

            var selectedOrdinals = new List<StateEvaluationGuardRequest>(
                Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement
                    * SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch,
                    operations.Count - ordinal));

            var batchParameterCount = 0;
            var batchPayloadBytes = 0;
            while (batch.Count < SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch
                   && ordinal < operations.Count)
            {
                var command = batch.CreateCommand();
                var parameters = new PostgreSqlCatalogQueryParameters(command, _typeMappingSource);
                var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(
                    _typeMappingSource,
                    _sqlGenerationHelper,
                    parameters.AddString,
                    parameters.Add);

                var selections = new List<string>(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
                var statementOrdinals = new List<StateEvaluationGuardRequest>(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
                var sqlBytes = trailerBytes;
                while (ordinal < operations.Count
                       && selections.Count < SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement)
                {
                    var operation = operations[ordinal]
                        ?? throw new ArgumentException(
                            "The operation batch cannot contain null entries.",
                            nameof(operations));

                    if (_generatedDropCatalogReadable && operation.Intent is DropColumnIntent)
                    {
                        // WHY: The same invocation already proved the required native
                        // dependency catalogs readable in its always-run
                        // visibility scalar. Runtime SQL keeps its fresh guard.
                        ordinal++;

                        continue;
                    }

                    var checkpoint = parameters.Capture();
                    var plan = builder.Build(operation, includeRuntimeIndexInputGuard: false);
                    if (states[ordinal] is null && !_columnConstraintCatalogReadable
                        && plan.ColumnTransitionTargetCatalogGuardExpression is not null)
                    {
                        // WHY: Required-target equality itself needs pg_constraint.
                        // Skip SQL binding only after earlier owner/prerequisite
                        // refusals have retained their original precedence.
                        parameters.Rollback(checkpoint);
                        states[ordinal] = SafeMigrationObservedState.Unsupported;
                        (_stateEvaluationGuardShortCircuitCodes ??= [])[ordinal] = "integer_widening_unproven";
                        ordinal++;

                        continue;
                    }

                    if (states[ordinal] is not null
                        || plan.StateEvaluationGuardFailureExpression is null)
                    {
                        parameters.Rollback(checkpoint);
                        ordinal++;

                        continue;
                    }

                    var guardFailed = $"NOT COALESCE(({plan.StateEvaluationGuardExpression}), FALSE)";
                    var defaultCode = plan.UnsupportedCode ?? "state_evaluation_guard";
                    var failureCode = plan.StateEvaluationGuardFailureCodeExpression
                        ?? parameters.AddString(defaultCode);

                    var selection = $"SELECT {ordinal.ToString(CultureInfo.InvariantCulture)}, CASE "
                        + $"WHEN {guardFailed} THEN ({plan.StateEvaluationGuardFailureExpression}) ELSE NULL END, "
                        + $"CASE WHEN {guardFailed} THEN ({failureCode}) ELSE NULL END";

                    var selectionBytes = Encoding.UTF8.GetByteCount(selection)
                        + (selections.Count == 0 ? 0 : separatorBytes);

                    var statementPayload = sqlBytes + selectionBytes + parameters.Utf8PayloadBytes;
                    var prospectiveBatchParameters = batchParameterCount + parameters.Count;
                    var prospectiveBatchPayload = batchPayloadBytes + statementPayload;
                    if (SafeMigrationCatalogQueryLimits.Exceeded(
                            prospectiveBatchParameters,
                            prospectiveBatchPayload,
                            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes))
                    {
                        parameters.Rollback(checkpoint);
                        if (selections.Count == 0)
                        {
                            if (batch.Count == 1)
                            {
                                batch.RemoveLastCommand(command);

                                throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                                    ordinal,
                                    prospectiveBatchParameters,
                                    prospectiveBatchPayload);
                            }

                            break;
                        }

                        break;
                    }

                    selections.Add(selection);
                    statementOrdinals.Add(new StateEvaluationGuardRequest(ordinal, defaultCode,
                        plan.StateEvaluationGuardFailureCodeExpression is not null));
                    sqlBytes += selectionBytes;
                    ordinal++;
                }

                if (selections.Count == 0)
                {
                    batch.RemoveLastCommand(command);

                    break;
                }

                command.CommandText = string.Join(SafeMigrationCatalogQueryLimits.Separator, selections)
                    + SafeMigrationCatalogQueryLimits.Trailer;
                batchParameterCount += parameters.Count;
                batchPayloadBytes += sqlBytes + parameters.Utf8PayloadBytes;
                selectedOrdinals.AddRange(statementOrdinals);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            var reasons = await ReadStateEvaluationGuardBatchAsync(batch, states, selectedOrdinals, cancellationToken);
            if (reasons is not null)
            {
                foreach (var (operationOrdinal, reason) in reasons)
                {
                    (_stateEvaluationGuardShortCircuitCodes ??= [])[operationOrdinal] = reason;
                }
            }
        }
    }

    private async Task ReadAnalysisAsync(
        SafeMigrationCatalogBatch batch,
        SafeMigrationProviderAnalysis[] results,
        PostgreSqlSafeMigrationRuntimePlan?[] plans,
        PostgreSqlDataProbeResult?[] dataProbeResults,
        IReadOnlyList<SafeMigrationOperation> operations,
        int operationOffset,
        IReadOnlyList<int> submittedOrdinals,
        CancellationToken cancellationToken
    )
    {
        var consumed = 0;
        await batch.ForEachResultSetAsync(
            async (reader, token) =>
            {
                if (reader.FieldCount != 10)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL SafeMigrations classifier returned an unexpected result shape.");
                }

                while (await reader.ReadAsync(token))
                {
                    var ordinal = reader.GetInt32(0);
                    SafeMigrationCatalogWorkOrder.ValidateResultOrdinal(ordinal, submittedOrdinals, ref consumed);
                    if (results[ordinal] is not null)
                    {
                        throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations classifier returned an already resolved ordinal.");
                    }

                    var state = ParseState(reader.GetString(1));
                    var repairCapability = reader.GetBoolean(3)
                        ? SafeMigrationRepairCapability.Safe
                        : SafeMigrationRepairCapability.None;

                    var plan = plans[ordinal - operationOffset]
                        ?? throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations classifier has no runtime plan for its result ordinal.");

                    var code = reader.IsDBNull(4)
                        ? state == SafeMigrationObservedState.Unsupported
                            ? plan.UnsupportedCode ?? "classified_unsupported"
                            : ClassificationCode(state)
                        : reader.GetString(4);

                    var emptyGeneratedDropSet = DecodeGeneratedDropCompleteness(
                        operations[ordinal].Intent, state, ref code);
                    CaptureColumnPreservingIndexDrop(operations[ordinal].Intent, state, ref code);

                    var evidence = plan.ModelManagedRowEvidenceExpression is null
                        ? null
                        : SafeMigrationModelManagedDataEvidence.Parse(
                                reader.GetString(5),
                                plan.ModelManagedRowCount,
                                plan.ModelManagedDependencyCountsExpression is null
                                    ? string.Empty
                                    : reader.GetString(6),
                                plan.ModelManagedDependencyCount,
                                "PostgreSQL");

                    IReadOnlyList<SafeMigrationFacetDifference> differences;
                    if (state == SafeMigrationObservedState.Different
                        && plan.DifferentDifference is not null)
                    {
                        // WHY: Model-managed values stay outside SQL diagnostics;
                        // the provider reports only the bounded mismatch category.
                        differences = [plan.DifferentDifference];
                    }
                    else
                    {
                        differences = state is not (SafeMigrationObservedState.Different
                            or SafeMigrationObservedState.DataBlocked
                            or SafeMigrationObservedState.Unsupported)
                            || reader.IsDBNull(7)
                            ? []
                            : SafeMigrationFacetDifferenceParser.Parse(reader.GetString(7), "PostgreSQL");
                    }

                    var operationalImpact = state == SafeMigrationObservedState.DataBlocked
                        || (state == SafeMigrationObservedState.Different
                            && repairCapability == SafeMigrationRepairCapability.Safe)
                            ? plan.RepairOperationalImpact
                            : SafeMigrationOperationalImpact.NotApplicable;

                    // WHY: A validated NO INHERIT parent can match even though
                    // its descendants need a fresh row proof. Diagnostics and
                    // attnotnull alone cannot convey that eligibility.
                    var requiresNullabilityDataProof = reader.GetBoolean(9);

                    var analysis = new SafeMigrationProviderAnalysis(
                        state,
                        repairCapability,
                        reader.GetBoolean(2),
                        code,
                        operationalImpact,
                        differences)
                    {
                        IsInvariantUnsupported = state == SafeMigrationObservedState.Unsupported
                            && plan.IsStaticallyUnsupported,
                        MatchedObjectName = state == SafeMigrationObservedState.Matching
                            && !reader.IsDBNull(8)
                                ? reader.GetString(8)
                                : null,
                        ModelManagedDataEvidence = evidence,
                        DropLeavesNoGeneratedColumns = emptyGeneratedDropSet,
                        // WHY: Every VARCHAR transition carries a probe template,
                        // but widening is catalog-only. Only a catalog-confirmed
                        // narrowing or nullable-to-required result becomes stale
                        // after projected DML.
                        RequiresLiveDataProof = dataProbeResults[ordinal]?.IsRequired == true
                            || requiresNullabilityDataProof,
                    };

                    results[ordinal] = analysis;
                }
            },
            cancellationToken);

        SafeMigrationCatalogWorkOrder.ValidateCompletion(consumed, submittedOrdinals);
    }

    private async Task<List<int>?> ReadPrerequisiteBatchAsync(
        SafeMigrationCatalogBatch batch,
        SafeMigrationObservedState?[] states,
        List<int> selectedOrdinals,
        CancellationToken cancellationToken
    )
    {
        var rowsRead = 0;
        List<int>? indexInputOrdinals = null;
        await batch.ForEachResultSetAsync(
            async (reader, token) =>
            {
                while (await reader.ReadAsync(token))
                {
                    var resultOrdinal = reader.GetInt32(0);
                    if (rowsRead >= selectedOrdinals.Count || resultOrdinal != selectedOrdinals[rowsRead])
                    {
                        throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations prerequisite classifier returned an invalid ordinal.");
                    }

                    var state = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (state == "index_input_required")
                    {
                        (indexInputOrdinals ??= []).Add(resultOrdinal);
                    }
                    else if (state == SchemaIdentityMismatchCode)
                    {
                        _schemaIdentityMismatchOrdinals.Add(resultOrdinal);
                        states[resultOrdinal] = SafeMigrationObservedState.Unsupported;
                    }
                    else
                    {
                        if (state is "index_input_operator_class" or "index_input_binding_unproven")
                        {
                            (_indexInputShortCircuitCodes ??= []).Add(resultOrdinal, state);
                            states[resultOrdinal] = state == "index_input_operator_class"
                        ? SafeMigrationObservedState.Unsupported
                        : SafeMigrationObservedState.PrerequisiteMissing;
                        }
                        else
                        {
                            states[resultOrdinal] = state is null ? null : ParseState(state);
                        }
                    }

                    rowsRead++;
                }
            },
            cancellationToken);

        if (rowsRead != selectedOrdinals.Count)
        {
            throw new InvalidOperationException(
                "The PostgreSQL SafeMigrations prerequisite classifier returned an inconsistent row count.");
        }

        return indexInputOrdinals;
    }

    internal static async Task<Dictionary<int, string>?> ReadStateEvaluationGuardBatchAsync(
        SafeMigrationCatalogBatch batch,
        SafeMigrationObservedState?[] states,
        IReadOnlyList<StateEvaluationGuardRequest> selectedOrdinals,
        CancellationToken cancellationToken
    )
    {
        var row = 0;
        Dictionary<int, string>? reasons = null;
        await batch.ForEachResultSetAsync(
            async (reader, token) =>
            {
                while (await reader.ReadAsync(token))
                {
                    if (reader.FieldCount != 3 || row >= selectedOrdinals.Count
                        || reader.GetValue(0) is not int resultOrdinal
                        || resultOrdinal != selectedOrdinals[row].Ordinal)
                    {
                        throw new InvalidOperationException(
                            "The PostgreSQL SafeMigrations state-evaluation guard classifier "
                            + "returned an invalid ordinal.");
                    }

                    var request = selectedOrdinals[row];
                    if (reader.IsDBNull(1))
                    {
                        if (!reader.IsDBNull(2))
                        {
                            throw InvalidStateEvaluationGuardEvidence();
                        }

                        states[resultOrdinal] = null;
                    }
                    else
                    {
                        if (reader.GetValue(1) is not string stateValue
                            || reader.GetValue(2) is not string code
                            || (code != request.DefaultCode && !(request.HasCapturedBinding
                                && code is "captured_sql_identifier" or "captured_sql_type_binding"
                                    or "captured_sql_function_binding" or "captured_sql_operator_binding")))
                        {
                            throw InvalidStateEvaluationGuardEvidence();
                        }

                        var state = ParseState(stateValue);
                        if (code is "captured_sql_identifier" or "captured_sql_type_binding"
                                or "captured_sql_function_binding" or "captured_sql_operator_binding"
                            && state != SafeMigrationObservedState.Unsupported)
                        {
                            throw InvalidStateEvaluationGuardEvidence();
                        }

                        states[resultOrdinal] = state;
                        if (state == SafeMigrationObservedState.Unsupported)
                        {
                            (reasons ??= [])[resultOrdinal] = code;
                        }
                    }

                    row++;
                }
            },
            cancellationToken);

        if (row != selectedOrdinals.Count)
        {
            throw new InvalidOperationException(
                "The PostgreSQL SafeMigrations state-evaluation guard classifier returned "
                + "an inconsistent row count.");
        }

        return reasons;
    }

    /// <summary>
    /// Rejects malformed native proof rows without copying their untrusted contents into diagnostics.
    /// </summary>
    private static InvalidOperationException InvalidStateEvaluationGuardEvidence()
        => new("The PostgreSQL SafeMigrations state-evaluation guard classifier "
            + "returned invalid capability evidence.");

    /// <summary>Scopes one ordered guard result to its exact expected bounded capability-code family.</summary>
    /// <param name="Ordinal">The immutable operation-stream ordinal.</param>
    /// <param name="DefaultCode">The existing reason retained when the earlier provider guard fails.</param>
    /// <param name="HasCapturedBinding">Whether native source keyword/type binding reasons are permitted.</param>
    internal readonly record struct StateEvaluationGuardRequest(
        int Ordinal, string DefaultCode, bool HasCapturedBinding);

    private static void ApplyCommandTimeout(
        DbCommand command,
        int? commandTimeout
    )
    {
        if (commandTimeout is not null)
        {
            command.CommandTimeout = commandTimeout.Value;
        }
    }

    /// <summary>Reads an optional effective default without rejecting explicitly qualified inventory.</summary>
    private static async Task<string?> GetCurrentSchemaAsync(
        DbConnection connection,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText = "SELECT current_schema();";

        var value = await command.ExecuteScalarAsync(cancellationToken);

        // WHY: An all-missing search path has no current schema, but explicit
        // qualified objects remain valid. Preserve absence rather than inventing
        // an empty identifier or rejecting their semantic-alias inventory.
        return value is null or DBNull ? null : value as string
            ?? throw new InvalidOperationException("PostgreSQL returned invalid current-schema identity evidence.");
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
        _ => throw new InvalidOperationException("The PostgreSQL SafeMigrations classifier returned an unknown state."),
    };

    private static string ClassificationCode(
        SafeMigrationObservedState state
    ) => state switch
    {
        SafeMigrationObservedState.Missing => "classified_missing",
        SafeMigrationObservedState.Matching => "classified_matching",
        SafeMigrationObservedState.Different => "classified_different",
        SafeMigrationObservedState.Unsupported => "classified_unsupported",
        SafeMigrationObservedState.DataBlocked => "classified_data_blocked",
        SafeMigrationObservedState.PrerequisiteMissing => "classified_prerequisite_missing",
        SafeMigrationObservedState.TransitionReady => "classified_transition_ready",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private sealed class AnalysisScope : IAsyncDisposable
    {
        private IDbContextTransaction? _transaction;

        public AnalysisScope(
            IDbContextTransaction? transaction
        )
        {
            _transaction = transaction;
        }

        public async ValueTask DisposeAsync()
        {
            if (_transaction is not null)
            {
                await _transaction.DisposeAsync();
                _transaction = null;
            }
        }
    }
}
