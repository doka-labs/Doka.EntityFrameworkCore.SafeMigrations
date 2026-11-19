namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer : ISafeMigrationProviderAnalyzer,
    ISafeMigrationProviderOperationProjection, ISafeMigrationProjectedKeyAnalyzer,
    ISafeMigrationProviderObjectIdentityNormalizer
{
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

    private readonly MySqlSafeMigrationPlanCapture _planCapture;
    private readonly IMigrationsSqlGenerator _sqlGenerator;
    private readonly IRelationalTypeMappingSource _typeMappingSource;
    private string? _currentDatabase;
    private MySqlGeneratedColumnDependencyContractCatalog? _generatedColumnDependencyContracts;
    private bool _generatedColumnDependencyIsMariaDb;
    private bool _requiresGeneratedDependencyQualification;
    private bool _tableNamesIgnoreCase;

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

    /// <summary>
    /// Uses the independently captured native owner policy without folding mode-zero table identities.
    /// </summary>
    private StringComparer TableIdentifierComparer => _tableNamesIgnoreCase
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    bool ISafeMigrationProjectedKeyAnalyzer.SharesUniqueConstraintAndIndexIdentity => true;

    public string ProviderId => "doka_mysql";

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
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        Exception? environmentFailure = null;

        try
        {
            var serverVersion = MySqlServerVersion.AutoDetect(
                connection,
                MySqlServerVersionCompatibilityMode.AllowUnsupported);

            return new SafeMigrationProviderEnvironment(
                ProviderId,
                serverVersion.IsMariaDb
                    ? "mariadb"
                    : "mysql",
                connection.ServerVersion);
        }
        catch (Exception exception)
        {
            environmentFailure = exception;

            throw;
        }
        finally
        {
            if (openedHere)
            {
                await SafeMigrationAnalysisCleanup.CloseAsync(connection, environmentFailure);
            }
        }
    }

    public async Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
        DbContext context,
        CancellationToken cancellationToken
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
        CancellationToken cancellationToken
    ) => AnalyzeCoreAsync(context, operations, captureRenameSources: true, null, cancellationToken);

    StringComparer ISafeMigrationProviderObjectIdentityNormalizer.IdentifierComparer => TableIdentifierComparer;

    StringComparer ISafeMigrationProviderObjectIdentityNormalizer.ColumnIdentifierComparer
        => StringComparer.OrdinalIgnoreCase;

    /// <inheritdoc />
    char? ISafeMigrationProviderObjectIdentityNormalizer.CapturedSqlIdentifierDelimiter => '`';

    /// <inheritdoc />
    StringComparer ISafeMigrationProviderObjectIdentityNormalizer.IndexIdentifierComparer
        => StringComparer.OrdinalIgnoreCase;

    StringComparer ISafeMigrationProviderObjectIdentityNormalizer.ForeignKeyIdentifierComparer
        => StringComparer.OrdinalIgnoreCase;

    string ISafeMigrationProviderObjectIdentityNormalizer.NormalizeIdentifier(
        string identifier
    ) => NormalizeTableIdentifier(identifier);

    /// <inheritdoc />
    string ISafeMigrationProviderObjectIdentityNormalizer.NormalizeColumnIdentifier(
        string identifier
    ) => SafeMigrationModelManagedKeyIdentity.NormalizeOrdinalIgnoreCase(identifier);

    string? ISafeMigrationProviderObjectIdentityNormalizer.NormalizeSchema(
        string? schema
    ) => schema is not null && StringComparer.Ordinal.Equals(schema, _currentDatabase)
        ? null
        : schema;

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

    /// <summary>Canonicalizes an owner only after the server proves case-insensitive physical identity.</summary>
    private string NormalizeTableIdentifier(
        string identifier
    ) => _tableNamesIgnoreCase
        ? SafeMigrationModelManagedKeyIdentity.NormalizeOrdinalIgnoreCase(identifier)
        : identifier;

    /// <summary>Analyzes one immutable snapshot without recursively capturing rename-source evidence.</summary>
    private async Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeCoreAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        bool captureRenameSources,
        MySqlBackfillConstraintCatalog? inheritedBackfillConstraints,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operations);

        if (captureRenameSources)
        {
            ResetCapturedAnalysis(operations);
        }

        if (operations.Count == 0)
        {
            return [];
        }

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        Exception? analysisFailure = null;
        try
        {
            MySqlSafeMigrationConnectionValidator.Validate(connection);

            var catalog = await CaptureAnalysisCatalogAsync(
                context,
                connection,
                operations,
                captureRenameSources,
                inheritedBackfillConstraints,
                cancellationToken);

            var results = new SafeMigrationProviderAnalysis[operations.Count];
            await AnalyzeOperationWindowsAsync(context, operations, catalog, results, cancellationToken);
            await CompleteAnalysisAsync(
                context,
                operations,
                captureRenameSources,
                catalog,
                results,
                cancellationToken);

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
                await SafeMigrationAnalysisCleanup.CloseAsync(connection, analysisFailure);
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
