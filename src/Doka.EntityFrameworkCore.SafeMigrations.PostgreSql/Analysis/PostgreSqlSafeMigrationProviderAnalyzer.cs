namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
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
    private readonly PostgreSqlSafeMigrationCatalogSqlBuilder _catalogSqlBuilder;
    private readonly ISqlGenerationHelper _sqlGenerationHelper;

    private readonly IRelationalTypeMappingSource _typeMappingSource;

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
    public string ProviderId => "npgsql_postgresql";

    /// <inheritdoc />
    public void ValidateContext(
        DbContext context
    ) => ArgumentNullException.ThrowIfNull(context);

    /// <inheritdoc />
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
            return new SafeMigrationProviderEnvironment(ProviderId, "postgresql", connection.ServerVersion);
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

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
        DbContext context,
        CancellationToken cancellationToken
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
                    IsolationLevel.RepeatableRead,
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

            var command = context
                .Database
                .GetDbConnection()
                .CreateCommand();

            Exception? commandFailure = null;
            try
            {
                command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                ApplyCommandTimeout(command, context.Database.GetCommandTimeout());
                command.CommandText = AnalysisAdvisoryLockSql;
                _ = await command.ExecuteScalarAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                commandFailure = exception;

                throw;
            }
            finally
            {
                // WHY: The command still borrows the transaction until its actual disposal ends.
                // Its cleanup must preserve execution failure before outer acquisition recovery runs.
                await SafeMigrationAnalysisCleanup.DisposeAsync(
                    command,
                    commandFailure,
                    "SafeMigrations.AnalysisCommandDisposeException");
            }

            return new AnalysisScope(transaction);
        }
        catch (Exception exception)
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                transaction,
                exception,
                "SafeMigrations.AnalysisTransactionDisposeException");

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken
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
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        Exception? analysisFailure = null;
        try
        {
            var commandTimeout = context.Database.GetCommandTimeout();
            var results = await ClassifyOperationsAsync(
                connection,
                operations,
                commandTimeout,
                cancellationToken);

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
                connection,
                operations,
                results,
                commandTimeout,
                cancellationToken);

            await PopulateIndexDataProofsAsync(
                connection,
                operations,
                results,
                commandTimeout,
                cancellationToken);

            return Array.AsReadOnly(results);
        }
        catch (Exception exception)
        {
            analysisFailure = exception;

            throw;
        }
        finally
        {
            if (openedHere)
            {
                await SafeMigrationAnalysisCleanup.CloseAsync(connection, analysisFailure);
            }
        }
    }

    private static async Task ValidateCallerOwnedTransactionAsync(
        IDbContextTransaction transaction,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var dbTransaction = transaction.GetDbTransaction();
        if (dbTransaction.IsolationLevel is not (IsolationLevel.RepeatableRead
            or IsolationLevel.Serializable))
        {
            throw new InvalidOperationException(
                "PostgreSQL SafeMigrations analysis requires a caller-owned transaction "
                + "to use RepeatableRead or Serializable isolation.");
        }

        var connection = dbTransaction.Connection
            ?? throw new InvalidOperationException(
                "The caller-owned PostgreSQL analysis transaction has no active connection.");

        var command = connection.CreateCommand();
        Exception? commandFailure = null;

        try
        {
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
        catch (Exception exception)
        {
            commandFailure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                command,
                commandFailure,
                "SafeMigrations.AnalysisCommandDisposeException");
        }
    }

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
        var command = connection.CreateCommand();
        Exception? commandFailure = null;

        try
        {
            ApplyCommandTimeout(command, commandTimeout);
            command.CommandText = "SELECT current_schema();";

            var value = await command.ExecuteScalarAsync(cancellationToken);

            // WHY: An all-missing search path has no current schema, but explicit
            // qualified objects remain valid. Preserve absence rather than inventing
            // an empty identifier or rejecting their semantic-alias inventory.
            return value is null or DBNull
                ? null
                : value as string
                ?? throw new InvalidOperationException("PostgreSQL returned invalid current-schema identity evidence.");
        }
        catch (Exception exception)
        {
            commandFailure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                command,
                commandFailure,
                "SafeMigrations.AnalysisCommandDisposeException");
        }
    }

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
