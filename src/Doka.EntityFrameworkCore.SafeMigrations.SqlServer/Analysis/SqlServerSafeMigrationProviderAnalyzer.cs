namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Classifies bounded SQL Server catalog operations within the active analysis transaction.</summary>
internal sealed partial class SqlServerSafeMigrationProviderAnalyzer :
    ISafeMigrationProviderAnalyzer,
    ISafeMigrationProviderTargetModelAnalyzer,
    ISafeMigrationProviderObjectIdentityNormalizer,
    ISafeMigrationProviderOperationProjection,
    ISafeMigrationProjectedKeyAnalyzer,
    ISafeMigrationProjectedColumnAnalyzer,
    ISafeMigrationProjectedDependencyAnalyzer,
    ISafeMigrationLivePrerequisiteSource,
    IDisposable,
    IAsyncDisposable
{
    private const string ScopeSql = "EXEC @result = sys.sp_getapplock "
        + "@Resource = N'doka-sm-catalog-analysis', @LockMode = N'Exclusive', "
        + "@LockOwner = N'Transaction', @LockTimeout = 30000; SELECT @result;";
    private const string ReleaseScopeSql = "DECLARE @result int; EXEC @result = sys.sp_releaseapplock "
        + "@Resource = N'doka-sm-catalog-analysis', @LockOwner = N'Transaction'; SELECT @result;";

    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;
    private readonly SqlServerSafeMigrationCatalogSqlBuilder _catalogSqlBuilder;
    private SqlServerProjectedDependencyGraph? _dependencyGraph;
    private DbContext? _catalogInventoryRejectedContext;
    private SqlServerCatalogPreamble? _catalogPreamble;
    private AnalysisScope? _analysisScope;
    private bool _connectionQuarantined;

    /// <summary>
    /// Creates an analyzer using the provider's type mappings and SQL identifier rules.
    /// </summary>
    /// <param name="typeMappingSource">The SQL Server relational type mapping source.</param>
    /// <param name="sqlGenerationHelper">The SQL Server SQL generation helper.</param>
    public SqlServerSafeMigrationProviderAnalyzer(
        IRelationalTypeMappingSource typeMappingSource,
        ISqlGenerationHelper sqlGenerationHelper
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentNullException.ThrowIfNull(sqlGenerationHelper);

        _typeMappingSource = typeMappingSource;
        _sqlGenerationHelper = sqlGenerationHelper;
        _catalogSqlBuilder = new SqlServerSafeMigrationCatalogSqlBuilder(typeMappingSource, sqlGenerationHelper);
    }

    /// <inheritdoc />
    public string ProviderId => "efcore_sqlserver";

    /// <inheritdoc />
    public StringComparer IdentifierComparer => StringComparer.Ordinal;

    /// <inheritdoc />
    public char? CapturedSqlIdentifierDelimiter => '[';

    /// <inheritdoc />
    public string NormalizeIdentifier(string identifier) => identifier;

    /// <inheritdoc />
    public string? NormalizeSchema(string? schema) => schema ?? "dbo";

    /// <inheritdoc />
    public bool HasLiveColumn(string table, string? schema, string column)
        => _dependencyGraph?.HasLiveColumn(table, schema, column) == true
            || _projectedKeyTables.TryGetValue((schema ?? "dbo", table), out var snapshot)
                && snapshot.Exists && snapshot.PhysicalSupported && snapshot.Columns.ContainsKey(column);

    /// <inheritdoc />
    public bool RequiresLiveForeignKeyQualification(EnsureForeignKeyIntent intent)
        => _dependencyGraph?.RequiresLiveForeignKeyQualification(intent) == true;

    /// <inheritdoc />
    public IReadOnlyList<SafeMigrationLiveCandidateKey>? GetLiveForeignKeyCandidateKeys(
        EnsureForeignKeyIntent intent,
        ISafeMigrationProjectedColumnSource columns
    )
        => _dependencyGraph?.GetLiveForeignKeyCandidateKeys(intent, columns);

    /// <inheritdoc />
    public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis)
        => analysis.Code is "default_schema_mismatch" or "identifier_collation_unproven";

    /// <inheritdoc />
    public bool PreservesExistingTableState(MigrationOperation operation) => IsZeroCommandSchemaEnsure(operation);

    /// <inheritdoc />
    public bool IsSequenceAwareAnalysis(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    )
    {
        // WHY: A positive binding names the unchanged physical column captured with this exact live match.
        // Drops remove bindings, repairs invalidate the changed binding, and newly allocated columns use
        // negative identities. Aggregate row-layout uncertainty must not erase this narrower certificate.
        return !_columnMatchingEvidenceInvalidated
            && analysis.ObservedState == SafeMigrationObservedState.Matching
            && operation.Intent is EnsureColumnIntent column
            && _projectedColumnLayouts.TryGetValue((column.Schema ?? "dbo", column.Table), out var layout)
            && layout is not null
            && layout.Bindings.TryGetValue(column.Definition.Name, out var identity)
            && identity > 0;
    }

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis? ValidateOpaqueProviderPostcondition(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var orderedAnalysis = _dependencyGraph?.ValidateOpaqueProviderPostcondition(operation, liveAnalysis, columns);
        if (orderedAnalysis is null)
        {
            return null;
        }

        return ValidateProjectedOperation(operation, orderedAnalysis, columns);
    }

    /// <inheritdoc />
    public SafeMigrationProviderAnalysis ValidateProjectedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis projectedAnalysis,
        ISafeMigrationProjectedColumnSource columns
    )
    {
        var transitionAnalysis = QualifyProjectedIntegerWidening(operation, projectedAnalysis, columns);
        transitionAnalysis = QualifyProjectedComputedColumn(operation, transitionAnalysis, columns);
        transitionAnalysis = QualifyProjectedNativeComputedDrop(operation, transitionAnalysis, columns);
        transitionAnalysis = QualifyProjectedCheckPredicate(operation, transitionAnalysis, columns);
        var dependencyAnalysis = _dependencyGraph?.ValidateProjectedOperation(operation, transitionAnalysis, columns)
            ?? transitionAnalysis;

        var seedAnalysis = QualifyProjectedSeedOperation(operation, dependencyAnalysis, columns);

        var identityAnalysis = QualifyProjectedIdentityOperation(operation, seedAnalysis);

        var columnAnalysis = QualifyProjectedColumnLayoutOperation(operation, identityAnalysis);

        return QualifyProjectedDdlFreshness(operation, columnAnalysis);
    }

    /// <inheritdoc />
    public void ObserveAcceptedOperation(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        _dependencyGraph?.ObserveAcceptedOperation(operation, liveAnalysis, analysis, decision);
        ObserveProjectedSeedOperation(operation, decision);
        ObserveProjectedIdentityOperation(operation, analysis, decision);
        ObserveProjectedCheckDependencies(operation, decision);
        ObserveProjectedComputedDropProofs(operation, liveAnalysis, analysis, decision);
        ObserveProjectedComputedOwnerRename(operation, decision);
        ObserveProjectedColumnLayoutOperation(operation, analysis, decision);
        ObserveProjectedColumnTransition(operation, decision);
    }

    /// <inheritdoc />
    public void ObserveProviderOperation(MigrationOperation operation)
    {
        if (IsZeroCommandSchemaEnsure(operation))
        {
            // WHY: EF emits no command, so neither captured column/dependency
            // proofs nor the ordinal of an actual trigger source can change.
            return;
        }

        _dependencyGraph?.ObserveProviderOperation(operation);
        InvalidateProjectedComputedDropProofs();
        InvalidateProjectedSeedProofs();
        InvalidateProjectedIdentityProofs();
        ObserveProviderCheckDependencies(operation);
        ObserveProviderColumnLayoutOperation(operation);
        ResetProjectedColumnTransitions(retainInvalidatedCheckPredicates: true);
        ObserveProviderDdlRowEffects(operation);
    }

    /// <summary>Matches the provider baseline's exact zero-command default-schema exception.</summary>
    private static bool IsZeroCommandSchemaEnsure(MigrationOperation operation)
        => operation is EnsureSchemaOperation schema && StringComparer.OrdinalIgnoreCase.Equals(schema.Name, "dbo");

    /// <inheritdoc />
    public void ValidateContext(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ThrowIfConnectionQuarantined();

        if (!StringComparer.Ordinal.Equals(context.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer"))
        {
            throw new InvalidOperationException("The context is not configured with the EF Core SQL Server provider.");
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
        => AnalyzeCoreAsync(context, operations, operations, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<IModel?>? targetModels,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (targetModels is not null && targetModels.Count != operations.Count)
        {
            throw new ArgumentException("Target models must match the operation count.", nameof(targetModels));
        }

        // WHY: Only typed ordered declarations inform CHECK dependency roles.
        // Ordinary operations retain their provider boundary; target models do
        // not certify their execution or make opaque SQL a proven declaration.
        return AnalyzeCoreAsync(context, operations.OfType<SafeMigrationOperation>().ToArray(), operations,
            cancellationToken);
    }

    /// <summary>Classifies the safe subset while retaining the original ordered CHECK declaration boundaries.</summary>
    private async Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeCoreAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        IReadOnlyList<MigrationOperation> orderedOperations,
        CancellationToken cancellationToken
    )
    {
        _dependencyGraph = null;
        ClearCatalogPreamble();
        _projectedKeyTables = new Dictionary<(string Schema, string Table), SqlServerProjectedKeyTable>();
        ResetProjectedSeedProofs();
        ResetProjectedIdentityProofs();
        ResetProjectedColumnLayouts();
        ResetProjectedColumnTransitions();
        _projectedComputedKeys.Clear();
        _nativeComputedDropRefusals = null;
        _nativeColumnDropOwnerRoutes = null;
        _nativeColumnDropObserverEpoch = 0;
        _computedDropIndexCandidates = null;
        _columnDropConstraintCandidates = null;
        _acceptedDeclaredChecks.Clear();
        _checkRenameDependencies.Clear();

        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Count == 0)
        {
            return [];
        }

        PrepareComputedDropIndexCandidates(operations);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            using var baselineActivity = SafeMigrationTelemetry.StartAnalysisStage(
                "provider-baseline", operations.Count);

            var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            var environment = await ReadCatalogEnvironmentAsync(
                connection,
                transaction,
                context.Database.GetCommandTimeout(),
                cancellationToken);

            if (!environment.CanSeeDatabaseMetadata)
            {
                if (HasCurrentAnalysisSession(context, connection, transaction))
                {
                    _catalogPreamble = new SqlServerCatalogPreamble(context, environment, [], IdentifierSafe: false);
                    _catalogInventoryRejectedContext = context;
                }

                return Enumerable.Range(0, operations.Count)
                    .Select(static _ => Unsupported("catalog_metadata_not_visible"))
                    .ToArray();
            }

            CaptureProjectedDdlRowEffects(environment.DdlRowEffectRisk);
            CaptureProjectedDmlEffects(environment.HasEnabledDmlTriggers);

            var references = SqlServerIdentifierContract.IncludeCheckDeclarationReferences(orderedOperations,
                SqlServerIdentifierContract.Collect(operations));
            var identifierSafe = await ReadIdentifierContractAsync(
                connection,
                transaction,
                references,
                context.Database.GetCommandTimeout(),
                cancellationToken);

            var collationUnsafe = !identifierSafe;
            if (identifierSafe)
            {
                await ReadProjectedColumnLayoutsAsync(
                    connection,
                    transaction,
                    operations,
                    context.Database.GetCommandTimeout(),
                    cancellationToken);

                await ReadProjectedCheckRenameDependenciesAsync(connection, transaction, orderedOperations,
                    context.Database.GetCommandTimeout(), cancellationToken);

                await ReadProjectedKeySnapshotAsync(
                    connection,
                    transaction,
                    operations,
                    context.Database.GetCommandTimeout(),
                    cancellationToken);

                _dependencyGraph = SqlServerProjectedDependencyGraph.IsRequired(operations)
                    ? await SqlServerProjectedDependencyGraph.ReadAsync(
                        connection,
                        transaction,
                        operations,
                        context.Database.GetCommandTimeout(),
                        _catalogSqlBuilder.BuildInlineForeignKeyPrerequisite,
                        _catalogSqlBuilder.ColumnStorageEquals,
                        _catalogSqlBuilder.InlineForeignKeyPhysicalWidthIsSupported,
                        cancellationToken,
                        _catalogSqlBuilder.BuildSparseForeignKeyPrerequisite)
                    : null;
            }

            PrepareColumnDropConstraintCandidates(operations);

            // WHY: Native catalog guards own CHECK rename admission. Offline
            // source collection must not approximate catalog case/accent rules.
            var expectedTables = SafeMigrationExpectedTableConstraints.FromOperations(
                operations,
                static schema => schema ?? "dbo",
                StringComparer.Ordinal,
                '[',
                foldUnquotedCapturedSqlIdentifiers: false);

            baselineActivity?.Dispose();
            using var classificationActivity = SafeMigrationTelemetry.StartAnalysisStage(
                "catalog-classification", operations.Count);

            var results = new SafeMigrationProviderAnalysis[operations.Count];
            var ordinal = 0;
            while (ordinal < operations.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var captureStart = ordinal;
                var count = Math.Min(
                    SafeMigrationCatalogQueryLimits.MaximumOperationsPerPlanCapture,
                    operations.Count - captureStart);

                var absentTargets = identifierSafe
                    ? await ReadAbsentTableTargetsAsync(connection, transaction, operations, captureStart, count,
                        context.Database.GetCommandTimeout(), cancellationToken)
                    : new bool[count];

                var plans = new SqlServerSafeMigrationRuntimePlan?[count];
                // WHY: Bind each capture-local plan to its original operation and result ordinal.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var operation = operations[captureStart + index]
                        ?? throw new ArgumentException("The operation batch cannot contain null entries.",
                            nameof(operations));

                    if (RequiresDefaultSchema(operation.Intent) && !environment.DefaultSchemaIsDbo)
                    {
                        results[captureStart + index] = Unsupported("default_schema_mismatch");

                        continue;
                    }

                    if (collationUnsafe)
                    {
                        results[captureStart + index] = Unsupported("identifier_collation_unproven");

                        continue;
                    }

                    var expected = operation.Intent is EnsureTableIntent table
                        ? expectedTables.GetValueOrDefault((table.Definition.Schema, table.Definition.Table))
                        : null;

                    var bindings = operation.Intent is ModelManagedDataIntent
                        ? new SqlServerCatalogParameterBindings(_typeMappingSource, captureStart + index)
                        : null;

                    var builder = bindings is null ? _catalogSqlBuilder
                        : new SqlServerSafeMigrationCatalogSqlBuilder(
                            _typeMappingSource, _sqlGenerationHelper,
                            sourceParameter: (value, _) => bindings.Add(value));

                    var plan = builder.Build(operation, expected, absentTargets[index],
                        GetComputedDropIndexCandidateNames(operation, operations),
                        GetColumnDropConstraintCandidates(operation));
                    if (plan.IsStaticallyUnsupported)
                    {
                        results[captureStart + index] = Unsupported(plan.UnsupportedCode ?? "classified_unsupported");

                        continue;
                    }

                    CaptureProjectedDdlRowDependency(operation, plan);

                    if (bindings is not null && bindings.Values.Count > 0)
                    {
                        // WHY: The complete guarded classifier now owns a private dynamic scope.
                        // Render its pre-binding scalar guard with the same stable local markers,
                        // rather than leaking ordinal-specific transport names into cached SQL.
                        var outerBuilder = new SqlServerSafeMigrationCatalogSqlBuilder(
                            _typeMappingSource, _sqlGenerationHelper,
                            sourceParameter: (value, _) => bindings.Add(value));

                        var outerGuard = outerBuilder.BuildModelManagedDataAnalysisGuard(
                            (ModelManagedDataIntent)operation.Intent);

                        plan = plan with
                        {
                            AnalysisParameters = bindings.Values,
                            AnalysisOuterStateGuardExpression = outerGuard.Guard,
                            AnalysisOuterStateGuardFailureExpression = outerGuard.Failure,
                        };
                    }

                    plans[index] = plan;
                }

                await ReadCatalogCaptureAsync(
                    connection,
                    transaction,
                    context.Database.GetCommandTimeout(),
                    plans,
                    captureStart,
                    results,
                    cancellationToken);

                ordinal += count;
            }

            CaptureIdentitySlotConflicts(operations, results);
            CaptureProjectedComputedDropRefusals(operations, results);
            CaptureProjectedCheckPredicates(operations, results);
            if (identifierSafe)
            {
                await ReadProjectedColumnTransitionsAsync(connection, transaction, operations, results,
                    environment.CanReadExpressionDependencies, context.Database.GetCommandTimeout(), cancellationToken);
                await ReadProjectedCheckReplacementsAsync(connection, transaction, operations, results,
                    context.Database.GetCommandTimeout(), cancellationToken);
            }

            _dependencyGraph?.CaptureRenameTargetPresence(operations, results);
            if (identifierSafe)
            {
                await PopulateMemberRenameTargetEvidenceAsync(
                    connection, transaction, operations, results,
                    context.Database.GetCommandTimeout(), cancellationToken);
            }

            // WHY: Inventory may reuse completed analysis proofs only within the same
            // lock/transaction scope. A partial or separately invoked analysis is not a certificate.
            if (HasCurrentAnalysisSession(context, connection, transaction))
            {
                _catalogPreamble = new SqlServerCatalogPreamble(context, environment, references, identifierSafe);
                if (collationUnsafe || !environment.DefaultSchemaIsDbo
                    && operations.Any(static operation => RequiresDefaultSchema(operation.Intent)))
                {
                    _catalogInventoryRejectedContext = context;
                }
            }

            return Array.AsReadOnly(results);
        }
        catch
        {
            ClearCatalogPreamble();

            throw;
        }
        finally
        {
            if (openedHere && !_connectionQuarantined)
            {
                ClearCatalogPreamble();
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Preserves independent permission refusals before ordered structure projection can replace a state.
    /// </summary>
    private static SafeMigrationProviderAnalysis ReadAnalysis(
        DbDataReader reader,
        SqlServerSafeMigrationRuntimePlan plan
    )
    {
        var state = ParseState(reader.GetString(1));
        var repair = reader.GetInt32(3) == 1
            ? SafeMigrationRepairCapability.Safe
            : SafeMigrationRepairCapability.None;

        var code = reader.IsDBNull(4)
            ? state == SafeMigrationObservedState.Unsupported
                ? plan.UnsupportedCode ?? "classified_unsupported"
                : StateCode(state)
            : reader.GetString(4);

        var permissionDenied = code is "dependency_catalog_permission" or "column_alter_write_permission"
            or "column_alter_read_permission";
        if (permissionDenied)
        {
            // WHY: An absent owner can produce the prerequisite fallback even
            // when this independent permission gate failed. Accepted creates
            // and matching columns prove structure, not the caller's rights.
            state = SafeMigrationObservedState.Unsupported;
            repair = SafeMigrationRepairCapability.None;
        }

        var evidence = plan.ModelManagedRowEvidenceExpression is null || reader.IsDBNull(5)
            ? null
            : SafeMigrationModelManagedDataEvidence.Parse(
                reader.GetString(5),
                plan.ModelManagedRowCount,
                plan.ModelManagedDependencyCountsExpression is null || reader.IsDBNull(6)
                    ? string.Empty
                    : reader.GetString(6),
                plan.ModelManagedDependencyCount,
                "SQL Server");

        IReadOnlyList<SafeMigrationFacetDifference> differences = state == SafeMigrationObservedState.Different
            && plan.DifferentDifference is not null
                ? [plan.DifferentDifference]
                : Array.Empty<SafeMigrationFacetDifference>();

        return new SafeMigrationProviderAnalysis(
            state,
            repair,
            reader.GetInt32(2) == 1,
            code,
            state == SafeMigrationObservedState.Different && repair == SafeMigrationRepairCapability.Safe
                ? plan.RepairOperationalImpact : SafeMigrationOperationalImpact.NotApplicable,
            differences)
        {
            RequiresLiveDataProof = plan.RequiresLiveDataProof && state != SafeMigrationObservedState.Matching,
            IsInvariantUnsupported = permissionDenied || state == SafeMigrationObservedState.Unsupported
                && (plan.IsStaticallyUnsupported
                    || code == SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode
                    || code == SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode
                    || code == "column_row_layout_unproven"
                    || code == SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode
                    || code == SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode),
            MatchedObjectName = state == SafeMigrationObservedState.Matching && !reader.IsDBNull(8)
                ? reader.GetString(8)
                : null,
            ModelManagedDataEvidence = evidence,
        };
    }

    private static SafeMigrationProviderAnalysis Unsupported(string code)
        => new(SafeMigrationObservedState.Unsupported, SafeMigrationRepairCapability.None, false, code)
        {
            IsInvariantUnsupported = true,
        };

    private static SafeMigrationObservedState ParseState(string state) => state switch
    {
        "missing" => SafeMigrationObservedState.Missing,
        "matching" => SafeMigrationObservedState.Matching,
        "different" => SafeMigrationObservedState.Different,
        "unsupported" => SafeMigrationObservedState.Unsupported,
        "data_blocked" => SafeMigrationObservedState.DataBlocked,
        "prerequisite_missing" => SafeMigrationObservedState.PrerequisiteMissing,
        "transition_ready" => SafeMigrationObservedState.TransitionReady,
        _ => throw new InvalidOperationException("SQL Server returned an unknown SafeMigrations state."),
    };

    private static string StateCode(SafeMigrationObservedState state) => state switch
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
}
