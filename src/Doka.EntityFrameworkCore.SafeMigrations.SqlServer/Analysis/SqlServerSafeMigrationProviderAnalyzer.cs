namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Classifies bounded SQL Server catalog operations within the active analysis transaction.</summary>
internal sealed partial class SqlServerSafeMigrationProviderAnalyzer :
    ISafeMigrationProviderAnalyzer,
    ISafeMigrationProviderObjectIdentityNormalizer,
    ISafeMigrationProviderOperationProjection,
    ISafeMigrationProjectedKeyAnalyzer,
    ISafeMigrationProjectedColumnAnalyzer,
    ISafeMigrationProjectedDependencyAnalyzer,
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
    public string NormalizeIdentifier(string identifier) => identifier;

    /// <inheritdoc />
    public string? NormalizeSchema(string? schema) => schema ?? "dbo";

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
        transitionAnalysis = QualifyProjectedCheckPredicate(operation, transitionAnalysis);
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
        InvalidateProjectedSeedProofs();
        InvalidateProjectedIdentityProofs();
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

    /// <summary>Permanently rejects supported migration and catalog reuse of this scoped session.</summary>
    internal void QuarantineConnection()
    {
        _connectionQuarantined = true;
        ClearCatalogPreamble();
    }

    /// <summary>Discards both successful proofs and scope-local invariant rejections.</summary>
    private void ClearCatalogPreamble()
    {
        _catalogPreamble = null;
        _catalogInventoryRejectedContext = null;
    }

    /// <summary>Requires the same still-active physical analysis session for cached evidence.</summary>
    private bool HasCurrentAnalysisSession(
        DbContext context,
        DbConnection connection,
        DbTransaction? transaction
    )
        => _analysisScope?.Matches(context, connection, transaction) == true;

    /// <summary>Releases any outstanding scope when the scoped analyzer is disposed.</summary>
    public ValueTask DisposeAsync() => _analysisScope?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <summary>Releases an outstanding scope when EF disposes its service scope synchronously.</summary>
    public void Dispose() => _analysisScope?.Dispose();

    /// <summary>Requires a replacement context after any provider session-recovery failure.</summary>
    internal void ThrowIfConnectionQuarantined()
    {
        if (_connectionQuarantined)
        {
            throw new InvalidOperationException(
                "SQL Server SafeMigrations requires a new context after session recovery failed.");
        }
    }

    /// <inheritdoc />
    public async Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ValidateContext(context);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            return new SafeMigrationProviderEnvironment(ProviderId, "sqlserver", connection.ServerVersion);
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
        ValidateContext(context);

        if (_analysisScope is not null)
        {
            throw new InvalidOperationException("The SQL Server analyzer already owns an active analysis scope.");
        }

        ClearCatalogPreamble();

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        try
        {
            if (context.Database.CurrentTransaction is null)
            {
                // WHY: SQL Server system catalog metadata is not versioned like
                // ordinary row data. ReadCommitted avoids SNAPSHOT metadata
                // conflicts; runtime guards recheck immediately before DDL.
                transaction = await context.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.ReadCommitted,
                    cancellationToken);
            }

            var connection = context.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            ApplyCommandTimeout(command, context.Database.GetCommandTimeout());
            command.CommandText = "DECLARE @result int; " + ScopeSql;
            var lockResult = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (lockResult < 0)
            {
                throw new InvalidOperationException("SQL Server SafeMigrations could not acquire its analysis lock.");
            }

            _analysisScope = new AnalysisScope(
                this,
                context,
                connection,
                command.Transaction
                    ?? throw new InvalidOperationException("The SQL Server analysis scope requires a transaction."),
                transaction,
                context.Database.GetCommandTimeout());

            return _analysisScope;
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        _dependencyGraph = null;
        ClearCatalogPreamble();
        _projectedKeyTables = new Dictionary<(string Schema, string Table), SqlServerProjectedKeyTable>();
        ResetProjectedSeedProofs();
        ResetProjectedIdentityProofs();
        ResetProjectedColumnLayouts();
        ResetProjectedColumnTransitions();

        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(operations);

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
            using var baselineActivity = SafeMigrationTelemetry.StartAnalysisStage("provider-baseline", operations.Count);
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

            var references = SqlServerIdentifierContract.Collect(operations);
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
                        cancellationToken)
                    : null;
            }

            var expectedTables = SafeMigrationExpectedTableConstraints.FromOperations(
                operations,
                static schema => schema ?? "dbo");

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

                    var plan = builder.Build(operation, expected, absentTargets[index]);
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
            CaptureProjectedCheckPredicates(operations, results);
            if (identifierSafe)
            {
                await ReadProjectedColumnTransitionsAsync(connection, transaction, operations, results,
                    environment.CanReadExpressionDependencies, context.Database.GetCommandTimeout(), cancellationToken);
                await ReadProjectedCheckReplacementsAsync(connection, transaction, operations, results,
                    context.Database.GetCommandTimeout(), cancellationToken);
            }

            _dependencyGraph?.CaptureRenameTargetPresence(operations, results);

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

    /// <inheritdoc />
    public async Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    )
    {
        ValidateContext(context);
        ArgumentNullException.ThrowIfNull(operations);

        var expected = SafeMigrationExpectedCatalog.Create(operations, static schema => schema ?? "dbo");
        if (expected.Count == 0)
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
            var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            var currentSession = HasCurrentAnalysisSession(context, connection, transaction);
            var preamble = currentSession ? _catalogPreamble : null;
            // WHY: EXECUTE AS, permission changes and database collation changes can occur
            // without replacing the connection or transaction. Recheck this cheap stamp before
            // reusing either an identifier verdict or a previous invariant rejection.
            var environment = await ReadCatalogEnvironmentAsync(
                connection, transaction, context.Database.GetCommandTimeout(), cancellationToken);

            if (preamble is not null
                && (!environment.HasReusableIdentity || preamble.Environment != environment))
            {
                ClearCatalogPreamble();
                preamble = null;
            }

            if (!environment.CanSeeDatabaseMetadata)
            {
                if (currentSession && ReferenceEquals(context, _catalogInventoryRejectedContext))
                {
                    // WHY: Preserve the preceding invariant Unsupported report.
                    // This empty optional inventory is not evidence of absence;
                    // direct inventory requests without that rejection still fail.
                    return [];
                }

                throw new InvalidOperationException(
                    "SQL Server catalog inventory requires database metadata visibility.");
            }

            var safeOperations = operations.OfType<SafeMigrationOperation>().ToArray();
            var references = SqlServerIdentifierContract.Collect(safeOperations);
            var identifierSafe = preamble is not null && preamble.Covers(context, references, environment)
                ? preamble.IdentifierSafe
                : await ReadIdentifierContractAsync(
                    connection,
                    transaction,
                    references,
                    context.Database.GetCommandTimeout(),
                    cancellationToken);

            if ((!environment.DefaultSchemaIsDbo
                    && safeOperations.Any(static operation => RequiresDefaultSchema(operation.Intent)))
                || !identifierSafe)
            {
                if (currentSession && ReferenceEquals(context, _catalogInventoryRejectedContext))
                {
                    return [];
                }

                throw new InvalidOperationException("SQL Server catalog inventory could not prove object identity.");
            }

            return await ReadUnexpectedObjectsAsync(
                connection,
                transaction,
                expected,
                context.Database.GetCommandTimeout(),
                cancellationToken);
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

    private static async Task<SqlServerCatalogEnvironment> ReadCatalogEnvironmentAsync(
        DbConnection connection,
        DbTransaction? transaction,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText =
            "SELECT SCHEMA_NAME(), CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), "
            + "HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION'), "
            + "CASE WHEN SCHEMA_NAME() COLLATE CATALOG_DEFAULT = N'dbo' COLLATE CATALOG_DEFAULT "
            + "THEN 1 ELSE 0 END, DATABASE_PRINCIPAL_ID(), CONVERT(varchar(170), SUSER_SID(), 2), "
            + "CASE WHEN EXISTS(SELECT 1 FROM sys.triggers WHERE parent_class=0 AND is_disabled=0) "
            + "OR EXISTS(SELECT 1 FROM sys.server_triggers WHERE is_disabled=0) THEN 1 "
            + "WHEN COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION'),0)<>1 "
            + "OR COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW ANY DEFINITION'),0)<>1 THEN 2 ELSE 0 END, "
            + "CASE WHEN (" + SqlServerSafeMigrationCatalogSqlBuilder.ExpressionDependencyReadPermission
            + ") THEN 1 ELSE 0 END, "
            // WHY: One invocation-local bit covers SQL/CLR triggers on any
            // table or view, including indirectly modified cascade targets.
            // Complete database visibility is already mandatory above.
            + "CASE WHEN EXISTS(SELECT 1 FROM sys.triggers WHERE parent_class=1 AND is_disabled=0) "
            + "THEN 1 ELSE 0 END;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (reader.FieldCount != 9 || !await reader.ReadAsync(cancellationToken)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1))
        {
            throw new InvalidOperationException("SQL Server did not return its catalog identity.");
        }

        return new SqlServerCatalogEnvironment(
            reader.GetString(0),
            !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
            !reader.IsDBNull(3) && reader.GetInt32(3) == 1,
            reader.GetString(1),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5))
        {
            DdlRowEffectRisk = reader.IsDBNull(6) ? SqlServerDdlRowEffectRisk.VisibilityUnproven
                : reader.GetInt32(6) switch
                {
                    0 => SqlServerDdlRowEffectRisk.None,
                    1 => SqlServerDdlRowEffectRisk.EnabledTrigger,
                    _ => SqlServerDdlRowEffectRisk.VisibilityUnproven,
                },
            CanReadExpressionDependencies = !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
            HasEnabledDmlTriggers = reader.IsDBNull(8) || reader.GetInt32(8) != 0,
        };
    }

    private async Task<bool> ReadIdentifierContractAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SqlServerIdentifierReference> references,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var commands = SqlServerIdentifierContract.BuildCollisionGuardCommands(
            references,
            throwOnCollision: false,
            out var temporaryTable);

        // WHY: Prove the invocation's name is unowned before entering the
        // cleanup scope. Attention during CREATE can otherwise leave a table
        // even when ExecuteNonQueryAsync never reports successful creation.
        if (temporaryTable is not null)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            ApplyCommandTimeout(command, commandTimeout);
            command.CommandText = $"SELECT OBJECT_ID(N'tempdb..{temporaryTable}');";
            var existing = await command.ExecuteScalarAsync(cancellationToken);
            if (existing is not null and not DBNull)
            {
                throw new InvalidOperationException("SQL Server identifier guard temporary scope is occupied.");
            }
        }

        Exception? originalFailure = null;
        try
        {
            for (var ordinal = 0; ordinal < commands.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                ApplyCommandTimeout(command, commandTimeout);
                command.CommandText = commands[ordinal];
                if (ordinal + 1 < commands.Count)
                {
                    await command.ExecuteNonQueryAsync(cancellationToken);

                    continue;
                }

                var result = await command.ExecuteScalarAsync(cancellationToken);

                return result is not null
                    && Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
            }

            throw new UnreachableException();
        }
        catch (Exception exception)
        {
            originalFailure = exception;

            throw;
        }
        finally
        {
            if (temporaryTable is not null)
            {
                await CleanupTemporaryScopeAsync(
                    connection, transaction, temporaryTable, commandTimeout, originalFailure);
            }
        }
    }

    private static bool RequiresDefaultSchema(SafeMigrationIntent intent)
        => intent switch
        {
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
            _ => false,
        };

    /// <summary>Builds one metadata-only classifier retaining the shared physical-engine boundary.</summary>
    /// <param name="ordinal">The original operation ordinal.</param>
    /// <param name="plan">The captured catalog contract.</param>
    /// <returns>A selection suitable for a bounded UNION ALL statement.</returns>
    internal static string BuildCatalogSelection(
        int ordinal,
        SqlServerSafeMigrationRuntimePlan plan
    )
        => BuildCatalogSelection(
            ordinal.ToString(CultureInfo.InvariantCulture), plan, includePhysicalTableSupport: true);

    private static string BuildCatalogSelection(
        string ordinal,
        SqlServerSafeMigrationRuntimePlan plan,
        bool includePhysicalTableSupport
    )
    {
        var guardedState = plan.StateEvaluationGuardFailureExpression is null
            ? plan.RenderStateExpression()
            : $"CASE WHEN COALESCE(({plan.StateEvaluationGuardExpression}), 0) = 1 "
                + $"THEN ({plan.RenderStateExpression()}) "
                + $"ELSE ({plan.StateEvaluationGuardFailureExpression}) END";

        var state = $"CASE WHEN COALESCE(({plan.PrerequisiteExpression}), 0) = 1 "
            + $"THEN ({guardedState}) ELSE N'prerequisite_missing' END";

        var post = plan.Postcondition;
        var repair = plan.RenderRepairPrecondition();
        var code = plan.RenderClassificationCodeExpression() ?? "CONVERT(nvarchar(128), NULL)";
        if (plan.PrerequisiteFailureCodeExpression is { } failureCode)
        {
            code = $"CASE WHEN COALESCE(({plan.PrerequisiteExpression}), 0) = 1 THEN ({code}) "
                + $"ELSE ({failureCode}) END";
        }

        var rows = plan.ModelManagedRowEvidenceExpression ?? "CONVERT(nvarchar(max), NULL)";
        var dependencies = plan.ModelManagedDependencyCountsExpression ?? "CONVERT(nvarchar(max), NULL)";
        var diagnostics = plan.DiagnosticEvidenceExpression ?? "CONVERT(nvarchar(max), NULL)";
        var matched = plan.MatchedObjectNameExpression ?? "CONVERT(nvarchar(128), NULL)";
        var physicalTableSupport = includePhysicalTableSupport ? plan.PhysicalTableSupportExpression : null;
        var defaultValueSupport = includePhysicalTableSupport ? plan.DefaultValueSupportExpression : null;
        var columnCollationSupport = includePhysicalTableSupport ? plan.ColumnCollationSupportExpression : null;
        var indexFilterSupport = includePhysicalTableSupport ? plan.IndexFilterSupportExpression : null;
        var columnLayoutFailure = includePhysicalTableSupport ? plan.ColumnLayoutFailureExpression : null;
        if (columnLayoutFailure is not null)
        {
            state = $"CASE WHEN doka_layout.failure IS NULL THEN ({state}) ELSE N'unsupported' END";
            post = $"CASE WHEN doka_layout.failure IS NULL THEN ({post}) ELSE 0 END";
            repair = $"CASE WHEN doka_layout.failure IS NULL THEN ({repair}) ELSE 0 END";
            code = $"CASE WHEN doka_layout.failure IS NULL THEN ({code}) ELSE doka_layout.failure END";
        }

        if (indexFilterSupport is not null)
        {
            state = $"CASE WHEN doka_filter.supported = 1 THEN ({state}) ELSE N'unsupported' END";
            post = $"CASE WHEN doka_filter.supported = 1 THEN ({post}) ELSE 0 END";
            repair = $"CASE WHEN doka_filter.supported = 1 THEN ({repair}) ELSE 0 END";
            code = $"CASE WHEN doka_filter.supported = 1 THEN ({code}) "
                + $"ELSE N'{SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode}' END";
        }

        if (defaultValueSupport is not null)
        {
            state = $"CASE WHEN doka_default.supported = 1 THEN ({state}) ELSE N'unsupported' END";
            post = $"CASE WHEN doka_default.supported = 1 THEN ({post}) ELSE 0 END";
            repair = $"CASE WHEN doka_default.supported = 1 THEN ({repair}) ELSE 0 END";
            code = $"CASE WHEN doka_default.supported = 1 THEN ({code}) "
                + $"ELSE N'{SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode}' END";
        }

        if (columnCollationSupport is not null)
        {
            state = $"CASE WHEN doka_collation.supported = 1 THEN ({state}) ELSE N'unsupported' END";
            post = $"CASE WHEN doka_collation.supported = 1 THEN ({post}) ELSE 0 END";
            repair = $"CASE WHEN doka_collation.supported = 1 THEN ({repair}) ELSE 0 END";
            code = $"CASE WHEN doka_collation.supported = 1 THEN ({code}) "
                + $"ELSE N'{SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode}' END";
        }

        if (physicalTableSupport is not null)
        {
            // WHY: Pure catalog expressions need no delayed name binding.
            // Keep their bounded UNION ALL transport and share one engine
            // predicate across state, code, and proof results. Row-reading and
            // preamble classifiers use the outer IF boundary instead.
            state = $"CASE WHEN doka_physical.supported = 1 THEN ({state}) ELSE N'unsupported' END";
            post = $"CASE WHEN doka_physical.supported = 1 THEN ({post}) ELSE 0 END";
            repair = $"CASE WHEN doka_physical.supported = 1 THEN ({repair}) ELSE 0 END";
            code = $"CASE WHEN doka_physical.supported = 1 THEN ({code}) "
                + $"ELSE N'{SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode}' END";
        }

        var supportSource = physicalTableSupport is null ? string.Empty
            : $"(SELECT COALESCE(({physicalTableSupport}), 0) AS supported) doka_physical";

        if (defaultValueSupport is not null)
        {
            supportSource += (supportSource.Length == 0 ? string.Empty : " CROSS JOIN ")
                + $"(SELECT COALESCE(({defaultValueSupport}), 0) AS supported) doka_default";
        }

        if (columnCollationSupport is not null)
        {
            supportSource += (supportSource.Length == 0 ? string.Empty : " CROSS JOIN ")
                + $"(SELECT COALESCE(({columnCollationSupport}), 0) AS supported) doka_collation";
        }

        if (indexFilterSupport is not null)
        {
            supportSource += (supportSource.Length == 0 ? string.Empty : " CROSS JOIN ")
                + $"(SELECT COALESCE(({indexFilterSupport}), 0) AS supported) doka_filter";
        }

        if (columnLayoutFailure is not null)
        {
            supportSource += (supportSource.Length == 0 ? string.Empty : " CROSS JOIN ")
                + $"(SELECT ({columnLayoutFailure}) AS failure) doka_layout";
        }

        return $"SELECT {ordinal}, ({state}), "
            + $"COALESCE(({post}), 0), COALESCE(({repair}), 0), ({code}), "
            + $"({rows}), ({dependencies}), ({diagnostics}), ({matched})"
            + (supportSource.Length == 0 ? string.Empty : " FROM " + supportSource);
    }

    /// <summary>Builds one delayed classifier within the complete statement payload bound.</summary>
    /// <param name="ordinal">The original operation ordinal.</param>
    /// <param name="plan">The already captured operation contract.</param>
    /// <param name="dispatchSlot">An optional statement-local slot owning the transport parameters.</param>
    /// <returns>One isolated classifier returning exactly one nine-column result set.</returns>
    internal static string BuildDelayedCatalogSelection(
        int ordinal,
        SqlServerSafeMigrationRuntimePlan plan,
        int? dispatchSlot = null
    )
    {
        var definitions = BuildDelayedParameterDefinitions(plan);
        var template = BuildDelayedCatalogTemplate(plan, definitions);
        var selection = BuildDelayedCatalogInvocation(ordinal, plan, dispatchSlot,
            "N'" + template.Replace("'", "''", StringComparison.Ordinal) + "'", definitions);

        var bytes = Encoding.UTF8.GetByteCount(selection);
        if (bytes > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes)
        {
            throw SafeMigrationCatalogQueryLimits.OversizedOperation(
                ordinal, plan.AnalysisParameters.Count + (dispatchSlot is not null
                    && UsesDelayedOrdinalParameter(plan) ? 1 : 0), bytes);
        }

        return selection;
    }

    /// <summary>Invokes a complete classifier using either a Unicode literal or a statement-local template.</summary>
    private static string BuildDelayedCatalogInvocation(
        int ordinal,
        SqlServerSafeMigrationRuntimePlan plan,
        int? dispatchSlot,
        string statement,
        string definitions
    )
    {
        var arguments = new StringBuilder("@doka_ordinal = ");
        var bindOrdinal = dispatchSlot is not null && UsesDelayedOrdinalParameter(plan);
        arguments.Append(bindOrdinal && dispatchSlot is { } slot
            ? DelayedOrdinalParameterName(slot)
            : ordinal.ToString(CultureInfo.InvariantCulture));

        for (var index = 0; index < plan.AnalysisParameters.Count; index++)
        {
            var value = plan.AnalysisParameters[index];
            arguments.Append(", ").Append(value.Name).Append(" = ")
                .Append(dispatchSlot is { } valueSlot
                    ? DelayedSourceParameterName(valueSlot, index)
                    : value.ExternalName);
        }

        // WHY: The heavyweight guarded body is compiled independently of neighboring classifiers.
        // Original ordinals and source values are inputs, never template identities or cached results.
        return "EXEC sys.sp_executesql " + statement
            + ", N'" + definitions + "', " + arguments + ";";
    }

    /// <summary>Builds an ordinal-independent classifier with private proof and preamble variables.</summary>
    /// <param name="plan">The complete captured operation contract.</param>
    /// <returns>The guarded dynamic body; each branch returns one owned result set.</returns>
    internal static string BuildDelayedCatalogTemplate(SqlServerSafeMigrationRuntimePlan plan)
        => BuildDelayedCatalogTemplate(plan, BuildDelayedParameterDefinitions(plan));

    /// <summary>Builds both dynamic scopes using one shared immutable parameter-definition buffer.</summary>
    private static string BuildDelayedCatalogTemplate(SqlServerSafeMigrationRuntimePlan plan, string definitions)
    {
        // WHY: SQL Server binds table and column references in a skipped IF
        // branch before execution. Dynamic SQL is required after the catalog
        // prerequisite has succeeded, not merely an IF wrapper.
        var inner = (plan.CatalogPreambleSql is null ? string.Empty : plan.CatalogPreambleSql + "\n")
            + BuildCatalogSelection("@doka_ordinal", plan, includePhysicalTableSupport: false);

        var escaped = inner.Replace("'", "''", StringComparison.Ordinal);
        var parameterArguments = plan.AnalysisParameters.Count == 0 ? string.Empty
            : ", " + string.Join(", ", plan.AnalysisParameters.Select(static value =>
                value.Name + " = " + value.Name));

        var outerStateGuard = plan.AnalysisOuterStateGuardExpression ?? plan.StateEvaluationGuardExpression;
        var outerStateFailure = plan.AnalysisOuterStateGuardFailureExpression
            ?? plan.StateEvaluationGuardFailureExpression;

        var physicalGate = plan.PhysicalTableSupportExpression is null ? string.Empty
            : BuildDelayedScalarProof(plan.PhysicalTableSupportExpression, "@doka_physical", "int",
                definitions, parameterArguments)
                + "IF COALESCE(@doka_physical, 0) <> 1 "
                + "SELECT @doka_ordinal, "
                + $"N'unsupported', 0, 0, N'{SqlServerSafeMigrationRuntimePlan.PhysicalTableUnsupportedCode}', "
                + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
                + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL); ELSE BEGIN ";

        var collationGate = plan.ColumnCollationSupportExpression is null ? string.Empty
            : BuildDelayedScalarProof(plan.ColumnCollationSupportExpression, "@doka_collation", "int",
                definitions, parameterArguments)
                + "IF COALESCE(@doka_collation, 0) <> 1 "
                + "SELECT @doka_ordinal, "
                + $"N'unsupported', 0, 0, N'{SqlServerSafeMigrationRuntimePlan.ColumnCollationUnsupportedCode}', "
                + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
                + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL); ELSE BEGIN ";

        const string layoutVariable = "@doka_layout";
        var layoutSetup = plan.ColumnLayoutFailureExpression is null ? string.Empty
            : BuildDelayedScalarProof(plan.ColumnLayoutFailureExpression, layoutVariable, "nvarchar(128)",
                definitions, parameterArguments);

        var layoutGate = plan.ColumnLayoutFailureExpression is null ? string.Empty
            : $"IF {layoutVariable} IS NOT NULL "
                + $"SELECT @doka_ordinal, N'unsupported', 0, 0, {layoutVariable}, "
                + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
                + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL); ELSE BEGIN ";

        var defaultGate = plan.DefaultValueSupportExpression is null ? string.Empty
            : BuildDelayedScalarProof(plan.DefaultValueSupportExpression, "@doka_default", "int",
                definitions, parameterArguments)
                + "IF COALESCE(@doka_default, 0) <> 1 "
                + "SELECT @doka_ordinal, "
                + $"N'unsupported', 0, 0, N'{SqlServerSafeMigrationRuntimePlan.DefaultValueUnsupportedCode}', "
                + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
                + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL); ELSE BEGIN ";

        var indexFilterGate = plan.IndexFilterSupportExpression is null ? string.Empty
            : BuildDelayedScalarProof(plan.IndexFilterSupportExpression, "@doka_filter", "int",
                definitions, parameterArguments)
                + "IF COALESCE(@doka_filter, 0) <> 1 "
                + "SELECT @doka_ordinal, "
                + $"N'unsupported', 0, 0, N'{SqlServerSafeMigrationRuntimePlan.IndexFilterUnsupportedCode}', "
                + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
                + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL); ELSE BEGIN ";

        var prerequisite = $"CASE WHEN COALESCE(({plan.PrerequisiteExpression}), 0) = 1 "
            + (outerStateFailure is null
                ? string.Empty
                : $"AND COALESCE(({outerStateGuard}), 0) = 1 ")
            + "THEN 1 ELSE 0 END";

        var fallback = "SELECT @doka_ordinal, "
            + (outerStateFailure is null
                ? "N'prerequisite_missing'"
                : $"CASE WHEN COALESCE(({plan.PrerequisiteExpression}), 0) = 1 "
                    + $"THEN ({outerStateFailure}) ELSE N'prerequisite_missing' END")
            + ", 0, 0, " + (plan.PrerequisiteFailureCodeExpression ?? "CONVERT(nvarchar(128), NULL)")
            + ", CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(max), NULL), "
            + "CONVERT(nvarchar(max), NULL), CONVERT(nvarchar(128), NULL);";

        var selection = physicalGate + layoutSetup + layoutGate + collationGate
            + defaultGate + indexFilterGate
            + BuildDelayedScalarProof(prerequisite, "@doka_prerequisite", "int", definitions, parameterArguments)
            + "IF @doka_prerequisite = 1 "
            // WHY: Returning the nested SELECT directly avoids INSERT EXEC and a statement-sized
            // table variable. Transport validates one result set per classifier before accepting evidence.
            + $"EXEC sys.sp_executesql N'{escaped}', "
            + $"N'{definitions}', @doka_ordinal = @doka_ordinal"
            + parameterArguments + " "
            + "ELSE EXEC sys.sp_executesql N'" + fallback.Replace("'", "''", StringComparison.Ordinal)
            + $"', N'{definitions}', @doka_ordinal = @doka_ordinal" + parameterArguments + ";";

        // WHY: An ELSE protects only one T-SQL statement. Every admitted support gate must enclose
        // all subsequent declarations, scalar EXEC calls and the final classifier, not just a DECLARE.
        var gateCount = (physicalGate.Length > 0 ? 1 : 0) + (layoutGate.Length > 0 ? 1 : 0)
            + (collationGate.Length > 0 ? 1 : 0) + (defaultGate.Length > 0 ? 1 : 0)
            + (indexFilterGate.Length > 0 ? 1 : 0);

        var closures = gateCount switch
        {
            0 => string.Empty,
            1 => " END;",
            2 => " END; END;",
            3 => " END; END; END;",
            4 => " END; END; END; END;",
            5 => " END; END; END; END; END;",
            _ => throw new UnreachableException(),
        };

        return selection + closures;
    }

    /// <summary>Retains stable local source mappings in both levels of delayed name binding.</summary>
    private static string BuildDelayedParameterDefinitions(SqlServerSafeMigrationRuntimePlan plan)
        => "@doka_ordinal int" + (plan.AnalysisParameters.Count == 0 ? string.Empty
            : ", " + string.Join(", ", plan.AnalysisParameters.Select(static value =>
                value.Name + " " + value.Mapping.StoreType)));

    /// <summary>Gets the statement-local original-ordinal transport marker.</summary>
    private static string DelayedOrdinalParameterName(int slot)
        => "@doka_ordinal" + slot.ToString(CultureInfo.InvariantCulture);

    /// <summary>Preserves the complete source-parameter admission boundary without raising the wire limit.</summary>
    private static bool UsesDelayedOrdinalParameter(SqlServerSafeMigrationRuntimePlan plan)
        // WHY: A previously admissible 2,000-source capture has no spare RPC parameter. Its generated
        // integer label can be literal only in the small outer dispatcher; the heavy classifier remains stable.
        => plan.AnalysisParameters.Count < SqlServerCatalogParameterBindings.MaximumParameters;

    /// <summary>Gets a source transport marker independent of the migration's global ordinal.</summary>
    private static string DelayedSourceParameterName(int slot, int index)
        => "@doka_source" + slot.ToString(CultureInfo.InvariantCulture) + "_"
            + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>Preserves independent permission refusals before ordered structure projection can replace a state.</summary>
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

    /// <summary>Loads an invocation-owned inventory scope before batching its final immutable reads.</summary>
    /// <param name="connection">The borrowed open analysis connection.</param>
    /// <param name="transaction">The active caller transaction.</param>
    /// <param name="expected">The invocation's expected table inventory.</param>
    /// <param name="commandTimeout">The caller's command timeout.</param>
    /// <param name="cancellationToken">The token that cancels scope loading and reading.</param>
    /// <returns>The complete unexpected-object evidence after owned scope cleanup.</returns>
    internal async Task<IReadOnlyList<SafeMigrationUnexpectedObject>> ReadUnexpectedObjectsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var findings = new List<SafeMigrationUnexpectedObject>();
        var temporaryTable = "#doka_sm_inventory_" + Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText = $"SELECT OBJECT_ID(N'tempdb..{temporaryTable}');";
        var occupied = await command.ExecuteScalarAsync(cancellationToken);
        if (occupied is not null and not DBNull)
        {
            throw new InvalidOperationException("SQL Server inventory temporary scope is occupied.");
        }

        var createStatement = $"IF OBJECT_ID(N'tempdb..{temporaryTable}') IS NOT NULL "
            + "THROW 51002, N'doka_sm_unsupported', 1; "
            + $"CREATE TABLE {temporaryTable} (kind int NOT NULL, "
            + "schema_name nvarchar(128) COLLATE CATALOG_DEFAULT NOT NULL, "
            + "table_name nvarchar(128) COLLATE CATALOG_DEFAULT NOT NULL, "
            + "name nvarchar(128) COLLATE CATALOG_DEFAULT NOT NULL);";

        Exception? originalFailure = null;
        try
        {
            command.CommandText = createStatement;
            await command.ExecuteNonQueryAsync(cancellationToken);

            // WHY: Runtime classification uses catalog collation for physical
            // names. The inventory must not turn a proven spelling alias into
            // an unexpected object through ordinal client-side comparisons.
            foreach (var rows in InventoryRows(expected).Chunk(SafeMigrationCatalogQueryLimits.MaximumInventoryValues))
            {
                cancellationToken.ThrowIfCancellationRequested();

                command.Parameters.Clear();
                var values = new string[rows.Length];
                var valueBytes = 0;
                for (var ordinal = 0; ordinal < rows.Length; ordinal++)
                {
                    var row = rows[ordinal];
                    var suffix = ordinal.ToString(CultureInfo.InvariantCulture);
                    var schemaParameter = "@schema" + suffix;
                    var tableParameter = "@table" + suffix;
                    var nameParameter = "@name" + suffix;
                    AddInventoryParameter(command, schemaParameter, row.Schema);
                    AddInventoryParameter(command, tableParameter, row.Table);
                    AddInventoryParameter(command, nameParameter, row.Name);
                    values[ordinal] = "(" + row.Kind.ToString(CultureInfo.InvariantCulture)
                        + ", " + schemaParameter + ", " + tableParameter + ", " + nameParameter + ")";
                    valueBytes += Encoding.UTF8.GetByteCount(row.Schema)
                        + Encoding.UTF8.GetByteCount(row.Table) + Encoding.UTF8.GetByteCount(row.Name);
                }

                command.CommandText = $"INSERT INTO {temporaryTable} VALUES " + string.Join(", ", values) + ";";
                var bytes = Encoding.UTF8.GetByteCount(command.CommandText) + valueBytes;
                if (bytes > SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes)
                {
                    throw SafeMigrationCatalogQueryLimits.OversizedOperation(0, command.Parameters.Count, bytes);
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            command.Parameters.Clear();
            var tableInventorySql = "SELECT 0, s.name, t.name FROM sys.tables t "
                + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
                + $"WHERE EXISTS (SELECT 1 FROM {temporaryTable} e WHERE e.schema_name = s.name) "
                + $"AND NOT EXISTS (SELECT 1 FROM {temporaryTable} e WHERE e.kind = 0 "
                + "AND e.schema_name = s.name AND e.table_name = t.name) ORDER BY s.name, t.name;";

            var childInventorySql = "WITH expected(schema_name, table_name) AS ("
                + $"SELECT schema_name, table_name FROM {temporaryTable} WHERE kind = 0), "
                + "physical(kind, schema_name, table_name, name) AS ("
                + "SELECT 1, s.name, t.name, c.name FROM expected e "
                + "JOIN sys.schemas s ON s.name = e.schema_name "
                + "JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = e.table_name "
                + "JOIN sys.columns c ON c.object_id = t.object_id "
                + "UNION ALL SELECT 2, s.name, t.name, i.name FROM expected e "
                + "JOIN sys.schemas s ON s.name = e.schema_name "
                + "JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = e.table_name "
                + "JOIN sys.indexes i ON i.object_id = t.object_id "
                + "WHERE i.name IS NOT NULL AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 "
                + "UNION ALL SELECT CASE kc.type WHEN 'PK' THEN 3 ELSE 4 END, s.name, t.name, kc.name "
                + "FROM expected e JOIN sys.schemas s ON s.name = e.schema_name "
                + "JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = e.table_name "
                + "JOIN sys.key_constraints kc ON kc.parent_object_id = t.object_id "
                + "UNION ALL SELECT 5, s.name, t.name, cc.name FROM expected e "
                + "JOIN sys.schemas s ON s.name = e.schema_name "
                + "JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = e.table_name "
                + "JOIN sys.check_constraints cc ON cc.parent_object_id = t.object_id "
                + "UNION ALL SELECT 6, s.name, t.name, fk.name FROM expected e "
                + "JOIN sys.schemas s ON s.name = e.schema_name "
                + "JOIN sys.tables t ON t.schema_id = s.schema_id AND t.name = e.table_name "
                + "JOIN sys.foreign_keys fk ON fk.parent_object_id = t.object_id) "
                + "SELECT 1, p.kind, p.schema_name, p.table_name, p.name FROM physical p "
                + $"WHERE NOT EXISTS (SELECT 1 FROM {temporaryTable} e WHERE e.kind = p.kind "
                + "AND e.schema_name = p.schema_name AND e.table_name = p.table_name AND e.name = p.name) "
                + "ORDER BY p.kind, p.schema_name, p.table_name, p.name;";

            // WHY: Loading the owned scope is a prerequisite. Only its two
            // immutable final reads share a transport, before owned cleanup.
            await SafeMigrationCatalogProbeBatch.ReadAsync(
                connection, 2, SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes, commandTimeout,
                (statement, slot) =>
                {
                    statement.CommandText = slot == 0 ? tableInventorySql : childInventorySql;

                    return new SafeMigrationCatalogProbeStatement(1, 0, slot);
                },
                async (reader, slot, _, token) =>
                {
                    if (reader.FieldCount != (slot == 0 ? 3 : 5))
                    {
                        throw new InvalidOperationException("SQL Server returned an unowned inventory result set.");
                    }

                    while (await reader.ReadAsync(token))
                    {
                        if (reader.GetInt32(0) != slot)
                        {
                            throw new InvalidOperationException("SQL Server returned an unowned inventory object.");
                        }

                        if (slot == 0)
                        {
                            findings.Add(new SafeMigrationUnexpectedObject(
                                SafeMigrationDatabaseObjectKind.Table, reader.GetString(1),
                                table: null, reader.GetString(2), "unexpected_table"));

                            continue;
                        }

                        var kind = (SafeMigrationDatabaseObjectKind)reader.GetInt32(1);
                        findings.Add(new SafeMigrationUnexpectedObject(
                            kind,
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            kind switch
                            {
                                SafeMigrationDatabaseObjectKind.Column => "unexpected_column",
                                SafeMigrationDatabaseObjectKind.Index => "unexpected_index",
                                SafeMigrationDatabaseObjectKind.PrimaryKey => "unexpected_primary_key",
                                SafeMigrationDatabaseObjectKind.UniqueConstraint => "unexpected_unique_constraint",
                                SafeMigrationDatabaseObjectKind.CheckConstraint => "unexpected_check_constraint",
                                SafeMigrationDatabaseObjectKind.ForeignKey => "unexpected_foreign_key",
                                _ => throw new UnreachableException(),
                            }));
                    }
                },
                cancellationToken, transaction, SqlServerCatalogParameterBindings.MaximumParameters);

            return findings.AsReadOnly();
        }
        catch (Exception exception)
        {
            originalFailure = exception;

            throw;
        }
        finally
        {
            // WHY: Cancellation must not leave invocation-owned temporary
            // state attached to an open caller connection.
            await CleanupTemporaryScopeAsync(
                connection, transaction, temporaryTable, commandTimeout, originalFailure);
        }
    }

    /// <summary>Proves an invocation-owned temporary scope is gone or quarantines its connection.</summary>
    /// <param name="connection">The borrowed connection retaining the temporary scope.</param>
    /// <param name="transaction">The caller transaction, which is never explicitly rolled back here.</param>
    /// <param name="temporaryTable">The generated invocation-owned table name.</param>
    /// <param name="commandTimeout">The optional caller timeout, capped for cleanup.</param>
    /// <param name="originalFailure">The original analysis failure to preserve when recovery also fails.</param>
    /// <returns>A task representing bounded cleanup and recovery.</returns>
    internal async Task CleanupTemporaryScopeAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string temporaryTable,
        int? commandTimeout,
        Exception? originalFailure
    )
    {
        var prefix = temporaryTable.StartsWith("#doka_sm_identifiers_", StringComparison.Ordinal)
            ? "#doka_sm_identifiers_"
            : "#doka_sm_inventory_";

        if (!temporaryTable.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(temporaryTable.AsSpan(prefix.Length), "N", out _))
        {
            throw new ArgumentException("Cleanup requires an invocation-owned temporary scope.",
                nameof(temporaryTable));
        }

        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeout is > 0 ? Math.Min(commandTimeout.Value, 30) : 30;
            command.CommandText = $"IF OBJECT_ID(N'tempdb..{temporaryTable}') IS NOT NULL DROP TABLE {temporaryTable};";
            await command.ExecuteNonQueryAsync(cleanupTimeout.Token);
        }
        catch (Exception cleanupFailure)
        {
            QuarantineConnection();
            var recoveryStatus = "connection_quarantined";
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await connection.CloseAsync().WaitAsync(closeTimeout.Token);
                recoveryStatus = "connection_closed";
            }
            catch (Exception)
            {
                // WHY: A wrapper may fail to close. Disposal is the remaining
                // connection boundary; the analyzer never reuses this context.
                try
                {
                    using var disposeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await connection.DisposeAsync().AsTask().WaitAsync(disposeTimeout.Token);
                    recoveryStatus = "connection_disposed";
                }
                catch (Exception)
                {
                    recoveryStatus = "connection_quarantined";
                }
            }

            if (originalFailure is not null)
            {
                // WHY: Preserve cancellation/provider failure identity, not a
                // secondary DROP error. Recovery status contains no SQL names.
                originalFailure.Data["Doka.SafeMigrations.SqlServer.RecoveryStatus"] = recoveryStatus;

                return;
            }

            var failure = new InvalidOperationException(
                "SQL Server SafeMigrations temporary-scope cleanup failed; recreate the context "
                + "and treat any caller transaction as unusable.", cleanupFailure);

            failure.Data["Doka.SafeMigrations.SqlServer.RecoveryStatus"] = recoveryStatus;

            throw failure;
        }
    }

    private static IEnumerable<(int Kind, string Schema, string Table, string Name)> InventoryRows(
        IReadOnlyList<SafeMigrationExpectedTableInventory> expected
    )
    {
        foreach (var table in expected)
        {
            var schema = table.Schema ?? "dbo";
            yield return (0, schema, table.Table, table.Table);
            foreach (var column in table.Columns)
            {
                yield return (1, schema, table.Table, column);
            }

            foreach (var index in table.Indexes)
            {
                yield return (2, schema, table.Table, index);
            }

            foreach (var constraint in table.Constraints)
            {
                yield return ((int)constraint.Value, schema, table.Table, constraint.Key);
            }
        }
    }

    private static void AddInventoryParameter(
        DbCommand command,
        string name,
        string value
    )
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = System.Data.DbType.String;
        parameter.Size = 128;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Captures the database environment and current execution identity for bounded proof reuse.</summary>
    /// <param name="DefaultSchema">The current user's default schema.</param>
    /// <param name="CanSeeDatabaseMetadata">Whether complete database definitions are visible.</param>
    /// <param name="DefaultSchemaIsDbo">Whether unqualified names resolve to the supported default schema.</param>
    /// <param name="Collation">The current database collation.</param>
    /// <param name="DatabasePrincipalId">The current database principal, or null when it cannot be resolved.</param>
    /// <param name="LoginSid">The current login SID in hexadecimal, or null when it cannot be resolved.</param>
    internal readonly record struct SqlServerCatalogEnvironment(
        string DefaultSchema,
        bool CanSeeDatabaseMetadata,
        bool DefaultSchemaIsDbo,
        string Collation,
        int? DatabasePrincipalId,
        string? LoginSid
    )
    {
        /// <summary>Gets the visible trigger or metadata-visibility uncertainty for this invocation.</summary>
        public SqlServerDdlRowEffectRisk DdlRowEffectRisk { get; init; }

        /// <summary>Gets whether both database and protected-view dependency-read permissions are proven.</summary>
        public bool CanReadExpressionDependencies { get; init; }

        /// <summary>Gets whether enabled SQL or CLR DML triggers can invalidate unrelated physical metadata.</summary>
        public bool HasEnabledDmlTriggers { get; init; }

        /// <summary>Requires both execution identities before another call can reuse this proof.</summary>
        public bool HasReusableIdentity => DatabasePrincipalId.HasValue && LoginSid is not null;
    }

    /// <summary>Retains the session-scoped probes of one analysis run for its inventory pass.</summary>
    /// <remarks>
    /// WHY: Classification and the unexpected-object inventory are two calls of the same run
    /// against the same session. The environment must be read afresh to detect same-session
    /// identity changes. Its stamp gates reuse of the costlier identifier collision verdict;
    /// only a completed analysis in the active scope can publish this preamble.
    /// </remarks>
    /// <param name="Context">The context that produced the probes.</param>
    /// <param name="Environment">The observed catalog environment of that session.</param>
    /// <param name="References">The identifier references the contract verdict was proven for.</param>
    /// <param name="IdentifierSafe">Whether the identifier contract held for those references.</param>
    internal sealed record SqlServerCatalogPreamble(
        DbContext Context,
        SqlServerCatalogEnvironment Environment,
        IReadOnlyList<SqlServerIdentifierReference> References,
        bool IdentifierSafe
    )
    {
        /// <summary>Determines whether this preamble proves the identifier contract for another request.</summary>
        /// <remarks>
        /// WHY: The verdict only covers the references it was computed from. Reusing it for a
        /// different reference set would turn a cache into a silent contract change, so the
        /// comparison is exact in content and order.
        /// </remarks>
        /// <param name="context">The context of the requesting call.</param>
        /// <param name="references">The identifier references the caller needs proven.</param>
        /// <param name="environment">The freshly read environment and execution identity.</param>
        /// <returns><see langword="true" /> when the retained verdict applies unchanged.</returns>
        public bool Covers(
            DbContext context,
            IReadOnlyList<SqlServerIdentifierReference> references,
            SqlServerCatalogEnvironment environment
        )
        {
            ArgumentNullException.ThrowIfNull(references);

            if (!ReferenceEquals(Context, context) || References.Count != references.Count
                || !environment.HasReusableIdentity || Environment != environment)
            {
                return false;
            }

            for (var index = 0; index < references.Count; index++)
            {
                if (!References[index].Equals(references[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Owns catalog-proof lifetime and the transaction-owned analysis lock.</summary>
    private sealed class AnalysisScope : IDisposable, IAsyncDisposable
    {
        private readonly SqlServerSafeMigrationProviderAnalyzer _analyzer;
        private readonly DbContext _context;
        private readonly DbConnection _connection;
        private readonly DbTransaction _transaction;
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _ownedTransaction;
        private readonly int? _commandTimeout;
        private readonly string _database;
        private readonly string _connectionString;
        private bool _disposed;
        private bool _sessionInvalidated;

        /// <summary>Captures the physical session after its analysis lock has been acquired.</summary>
        /// <param name="analyzer">The analyzer whose proofs belong to this scope.</param>
        /// <param name="context">The exact context supplying the session.</param>
        /// <param name="connection">The locked physical connection.</param>
        /// <param name="transaction">The transaction owning the application lock.</param>
        /// <param name="ownedTransaction">The EF transaction created here, or null for a borrowed transaction.</param>
        /// <param name="commandTimeout">The timeout used to release a borrowed lock.</param>
        public AnalysisScope(
            SqlServerSafeMigrationProviderAnalyzer analyzer,
            DbContext context,
            DbConnection connection,
            DbTransaction transaction,
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? ownedTransaction,
            int? commandTimeout
        )
        {
            _analyzer = analyzer;
            _context = context;
            _connection = connection;
            _transaction = transaction;
            _ownedTransaction = ownedTransaction;
            _commandTimeout = commandTimeout;
            _database = connection.Database;
            _connectionString = connection.ConnectionString;
            connection.StateChange += OnConnectionStateChange;
        }

        /// <summary>Rejects evidence after transaction replacement or any physical session change.</summary>
        /// <param name="context">The inventory request's context.</param>
        /// <param name="connection">The inventory request's physical connection.</param>
        /// <param name="transaction">The inventory request's current transaction.</param>
        /// <returns>Whether the original consistency window is still active.</returns>
        public bool Matches(
            DbContext context,
            DbConnection connection,
            DbTransaction? transaction
        )
        {
            if (_disposed || _sessionInvalidated || !ReferenceEquals(_context, context))
            {
                return false;
            }

            if (!ReferenceEquals(_connection, connection) || !ReferenceEquals(_transaction, transaction)
                || !ReferenceEquals(_transaction.Connection, connection)
                || connection.State != System.Data.ConnectionState.Open
                || !StringComparer.Ordinal.Equals(_database, connection.Database)
                || !StringComparer.Ordinal.Equals(_connectionString, connection.ConnectionString))
            {
                // WHY: Returning to the former database/transaction does not restore a
                // lost consistency window. Invalidation is permanent for this scope.
                _sessionInvalidated = true;
                _analyzer.ClearCatalogPreamble();

                return false;
            }

            return true;
        }

        private void OnConnectionStateChange(object? sender, System.Data.StateChangeEventArgs args)
        {
            if (args.CurrentState != System.Data.ConnectionState.Open)
            {
                _sessionInvalidated = true;
                _analyzer.ClearCatalogPreamble();
            }
        }

        /// <summary>Invalidates evidence before either synchronous or asynchronous transaction cleanup.</summary>
        private bool Detach()
        {
            if (_disposed)
            {
                return false;
            }

            _disposed = true;
            _connection.StateChange -= OnConnectionStateChange;
            _analyzer.ClearCatalogPreamble();
            _analyzer._analysisScope = null;

            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (!Detach())
            {
                return;
            }

            // WHY: EF can dispose its scoped services synchronously. Use synchronous ADO.NET
            // cleanup instead of blocking an asynchronous continuation on a caller's context.
            if (_analyzer._connectionQuarantined)
            {
                try
                {
                    _ownedTransaction?.Dispose();
                }
                catch (Exception)
                {
                    return;
                }

                return;
            }

            if (_ownedTransaction is not null)
            {
                _ownedTransaction.Dispose();

                return;
            }

            if (!CanReleaseBorrowedLock())
            {
                return;
            }

            using var command = CreateReleaseCommand();
            ValidateReleaseResult(command.ExecuteScalar());
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (!Detach())
            {
                return;
            }

            if (_analyzer._connectionQuarantined)
            {
                // WHY: Recovery already reported the primary failure and
                // invalidated the connection. A release on the borrowed
                // transaction must not replace that failure. Only an owned
                // EF transaction may need best-effort local disposal here.
                if (_ownedTransaction is not null)
                {
                    try
                    {
                        await _ownedTransaction.DisposeAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }

                return;
            }

            if (_ownedTransaction is not null)
            {
                await _ownedTransaction.DisposeAsync();

                return;
            }

            if (!CanReleaseBorrowedLock())
            {
                return;
            }

            // WHY: a transaction-owned application lock would otherwise remain
            // until the caller commits its unrelated transaction. SQL Server
            // requires one release for each successful acquisition.
            await using var command = CreateReleaseCommand();
            ValidateReleaseResult(await command.ExecuteScalarAsync());
        }

        /// <summary>Skips release only after the original session or transaction has already ended.</summary>
        private bool CanReleaseBorrowedLock()
        {
            // WHY: EF service disposal can tear down its relational connection before this
            // analyzer. A transaction-owned lock ends with that transaction/session; attempting
            // another command then would turn successful disposal into a provider exception.

            return _connection.State == System.Data.ConnectionState.Open
                && ReferenceEquals(_transaction.Connection, _connection);
        }

        /// <summary>Builds one bounded release of the caller transaction's acquired application lock.</summary>
        private DbCommand CreateReleaseCommand()
        {
            var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            ApplyCommandTimeout(command, _commandTimeout);
            command.CommandText = ReleaseScopeSql;

            return command;
        }

        private static void ValidateReleaseResult(object? result)
        {
            if (Convert.ToInt32(result, CultureInfo.InvariantCulture) < 0)
            {
                throw new InvalidOperationException("SQL Server did not release the borrowed analysis lock.");
            }
        }
    }
}
