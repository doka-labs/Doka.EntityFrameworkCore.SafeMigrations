namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>
/// Recovers session-scoped IDENTITY_INSERT when a guarded seed command fails.
/// </summary>
internal sealed class SqlServerSafeMigrationIdentityInsertCommand : SqlServerSafeMigrationGuardedCommand
{
    internal const string RecoveryFailureDataKey = "Doka:SqlServerIdentityInsertRecovery";
    private readonly string? _acquisitionUnprovenMessage;

    private readonly string _cleanupSql;
    private readonly string? _scopeInUseMessage;

    /// <summary>Creates a seed-command recovery boundary using the existing scoped quarantine.</summary>
    /// <param name="command">The original provider command to delegate unchanged.</param>
    /// <param name="cleanupSql">The same-session IDENTITY_INSERT recovery statement.</param>
    /// <param name="dependencies">The command-building and current-context services.</param>
    /// <param name="analyzer">The scoped provider session-quarantine owner.</param>
    /// <param name="metadataCommand">An optional shared empty metadata command, never a copy of the payload.</param>
    /// <param name="operation">The exact authored operation when known, never inferred from cleanup SQL.</param>
    /// <param name="operationOrdinal">The known operation ordinal, or null when unavailable.</param>
    public SqlServerSafeMigrationIdentityInsertCommand(
        MigrationCommand command,
        string cleanupSql,
        MigrationsSqlGeneratorDependencies dependencies,
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        IRelationalCommand? metadataCommand = null,
        SafeMigrationOperation? operation = null,
        int? operationOrdinal = null
    ) : base(command, dependencies, analyzer, metadataCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cleanupSql);

        _cleanupSql = cleanupSql;
        if (operation is not null)
        {
            // WHY: A command can be retried. Capture bounded identifier-only
            // messages once, without changing its native cleanup or delegate.
            _scopeInUseMessage = SafeMigrationDiagnosticMessages.FormatRuntime(
                operation,
                "doka_sm_identity_scope_in_use",
                operationOrdinal);

            _acquisitionUnprovenMessage = SafeMigrationDiagnosticMessages.FormatRuntime(
                operation,
                "doka_sm_identity_acquisition_unproven",
                operationOrdinal);
        }
    }

    /// <inheritdoc />
    public override int ExecuteNonQuery(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null
    )
    {
        // WHY: A rejected retry did not execute SQL and must not attempt
        // recovery against the already-quarantined session.
        ThrowIfConnectionQuarantined();
        var token = Guid
            .NewGuid()
            .ToString("N");

        var scopeOpened = false;
        var scopeIssued = false;
        var providerDispatched = false;
        Exception? originalFailure = null;

        try
        {
            connection.Open();
            scopeOpened = true;

            var command = connection.DbConnection.CreateCommand();
            Exception? bootstrapFailure = null;
            try
            {
                ConfigureScopeCommand(
                    connection,
                    command,
                    SqlServerSafeMigrationIdentityInsertScope.BuildBootstrapSql(token, _scopeInUseMessage));

                scopeIssued = true;
                command.ExecuteNonQuery();
            }
            catch (Exception exception)
            {
                bootstrapFailure = exception;

                throw;
            }
            finally
            {
                try
                {
                    command.Dispose();
                }
                catch (Exception exception) when (bootstrapFailure is not null)
                {
                    SafeMigrationAnalysisCleanup.RetainFailure(
                        bootstrapFailure,
                        exception,
                        "SafeMigrations.AnalysisCommandDisposeException");
                }
            }

            providerDispatched = true;

            return OriginalCommand.ExecuteNonQuery(connection, parameterValues);
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            if (scopeIssued && !IsRejectedBootstrap(exception, providerDispatched))
            {
                RecoverSession(connection, token, providerDispatched, exception);
            }

            throw;
        }
        finally
        {
            if (scopeOpened)
            {
                try
                {
                    connection.Close();
                }
                catch (Exception exception) when (originalFailure is not null)
                {
                    QuarantineSession(originalFailure);
                    SafeMigrationAnalysisCleanup.RetainFailure(
                        originalFailure,
                        exception,
                        "SafeMigrations.AnalysisConnectionCloseException");
                }
            }
        }
    }

    /// <inheritdoc />
    public override async Task<int> ExecuteNonQueryAsync(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfConnectionQuarantined();
        cancellationToken.ThrowIfCancellationRequested();
        var token = Guid
            .NewGuid()
            .ToString("N");

        var scopeOpened = false;
        var scopeIssued = false;
        var providerDispatched = false;
        Exception? originalFailure = null;

        try
        {
            await connection.OpenAsync(cancellationToken);
            scopeOpened = true;

            // WHY: A generated command is reusable and interceptors can fail
            // before dispatch. One seed-only bootstrap roundtrip reserves a
            // fresh invocation. EF's counted Open/Close keeps caller ownership.
            // If Open/CreateCommand/configuration fails before SQL is issued,
            // no owned marker or ON exists and recovery must not touch callers.
            var command = connection.DbConnection.CreateCommand();
            Exception? bootstrapFailure = null;
            try
            {
                ConfigureScopeCommand(
                    connection,
                    command,
                    SqlServerSafeMigrationIdentityInsertScope.BuildBootstrapSql(token, _scopeInUseMessage));

                scopeIssued = true;

                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                bootstrapFailure = exception;

                throw;
            }
            finally
            {
                await SafeMigrationAnalysisCleanup.DisposeAsync(
                    command,
                    bootstrapFailure,
                    "SafeMigrations.AnalysisCommandDisposeException");
            }

            cancellationToken.ThrowIfCancellationRequested();
            providerDispatched = true;

            return await OriginalCommand.ExecuteNonQueryAsync(connection, parameterValues, cancellationToken);
        }
        catch (Exception exception)
        {
            originalFailure = exception;
            if (scopeIssued && !IsRejectedBootstrap(exception, providerDispatched))
            {
                await RecoverSessionAsync(connection, token, providerDispatched, exception);
            }

            throw;
        }
        finally
        {
            if (scopeOpened)
            {
                try
                {
                    await connection.CloseAsync();
                }
                catch (Exception exception) when (originalFailure is not null)
                {
                    QuarantineSession(originalFailure);
                    SafeMigrationAnalysisCleanup.RetainFailure(
                        originalFailure,
                        exception,
                        "SafeMigrations.AnalysisConnectionCloseException");
                }
            }
        }
    }

    /// <summary>Recovers exact owned identity state without replacing the original execution failure.</summary>
    /// <param name="connection">The borrowed relational session and transaction.</param>
    /// <param name="token">The invocation's hexadecimal ownership marker, not a cancellation token.</param>
    /// <param name="providerDispatched">Whether the original command could have enabled identity insertion.</param>
    /// <param name="originalException">The primary failure retaining any secondary recovery evidence.</param>
    private void RecoverSession(
        IRelationalConnection connection,
        string token,
        bool providerDispatched,
        Exception originalException
    )
    {
        DbCommand? command = null;
        var recoveryRequired = false;

        try
        {
            if (connection.DbConnection.State != ConnectionState.Open)
            {
                return;
            }

            command = connection.DbConnection.CreateCommand();
            ConfigureCleanupCommand(connection, command, token, providerDispatched);
            command.ExecuteNonQuery();
        }
        catch (Exception exception)
        {
            QuarantineSession(originalException);
            SafeMigrationAnalysisCleanup.RetainFailure(
                originalException,
                exception,
                "Doka.SafeMigrations.SqlServer.IdentityInsertCleanupException");

            recoveryRequired = providerDispatched;
        }
        finally
        {
            if (command is not null)
            {
                try
                {
                    command.Dispose();
                }
                catch (Exception exception)
                {
                    QuarantineSession(originalException);
                    SafeMigrationAnalysisCleanup.RetainFailure(
                        originalException,
                        exception,
                        "SafeMigrations.AnalysisCommandDisposeException");

                    recoveryRequired = providerDispatched;
                }
            }
        }

        if (!recoveryRequired)
        {
            return;
        }

        try
        {
            DiscardPooledSession(connection.DbConnection);
            connection.DbConnection.Close();
            SafeMigrationAnalysisCleanup.RetainRecoveryStatus(
                originalException,
                RecoveryFailureDataKey,
                "session_closed");
        }
        catch (Exception exception)
        {
            SafeMigrationAnalysisCleanup.RetainFailure(
                originalException,
                exception,
                "SafeMigrations.AnalysisConnectionCloseException");

            try
            {
                connection.DbConnection.Dispose();
                SafeMigrationAnalysisCleanup.RetainRecoveryStatus(
                    originalException,
                    RecoveryFailureDataKey,
                    "session_disposed");
            }
            catch (Exception disposeFailure)
            {
                // WHY: Even both failed physical boundaries cannot clear the
                // scoped quarantine or replace the original migration failure.
                SafeMigrationAnalysisCleanup.RetainRecoveryStatus(
                    originalException,
                    RecoveryFailureDataKey,
                    "session_quarantined");

                SafeMigrationAnalysisCleanup.RetainFailure(
                    originalException,
                    disposeFailure,
                    "SafeMigrations.AnalysisConnectionDisposeException");
            }
        }
    }

    /// <summary>Retains actual native recovery and disposal work until the borrowed lifetime ends.</summary>
    /// <param name="connection">The borrowed relational session and transaction.</param>
    /// <param name="token">The invocation's hexadecimal ownership marker, not a cancellation token.</param>
    /// <param name="providerDispatched">Whether the original command could have enabled identity insertion.</param>
    /// <param name="originalException">The primary failure retaining any secondary recovery evidence.</param>
    /// <returns>Completion of protected recovery, including any required physical connection disposal.</returns>
    private async Task RecoverSessionAsync(
        IRelationalConnection connection,
        string token,
        bool providerDispatched,
        Exception originalException
    )
    {
        DbCommand? command = null;
        var recoveryRequired = false;

        try
        {
            if (connection.DbConnection.State != ConnectionState.Open)
            {
                return;
            }

            command = connection.DbConnection.CreateCommand();
            ConfigureCleanupCommand(connection, command, token, providerDispatched);

            // WHY: SQL Server TRY/CATCH does not catch client attentions.
            // Recovery retains its actual borrowed command until execution ends;
            // canceling an observer would not finish the native operation. Keep
            // its explicit command timeout, without an independent waiting timer.
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            QuarantineSession(originalException);
            SafeMigrationAnalysisCleanup.RetainFailure(
                originalException,
                exception,
                "Doka.SafeMigrations.SqlServer.IdentityInsertCleanupException");

            recoveryRequired = providerDispatched;
        }
        finally
        {
            if (command is not null)
            {
                try
                {
                    await command.DisposeAsync();
                }
                catch (Exception exception)
                {
                    QuarantineSession(originalException);
                    SafeMigrationAnalysisCleanup.RetainFailure(
                        originalException,
                        exception,
                        "SafeMigrations.AnalysisCommandDisposeException");

                    recoveryRequired = providerDispatched;
                }
            }
        }

        if (!recoveryRequired)
        {
            return;
        }

        try
        {
            DiscardPooledSession(connection.DbConnection);
            await connection.DbConnection.CloseAsync();

            SafeMigrationAnalysisCleanup.RetainRecoveryStatus(
                originalException,
                RecoveryFailureDataKey,
                "session_closed");
        }
        catch (Exception exception)
        {
            SafeMigrationAnalysisCleanup.RetainFailure(
                originalException,
                exception,
                "SafeMigrations.AnalysisConnectionCloseException");

            try
            {
                await connection.DbConnection.DisposeAsync();

                SafeMigrationAnalysisCleanup.RetainRecoveryStatus(
                    originalException,
                    RecoveryFailureDataKey,
                    "session_disposed");
            }
            catch (Exception disposeFailure)
            {
                SafeMigrationAnalysisCleanup.RetainRecoveryStatus(
                    originalException,
                    RecoveryFailureDataKey,
                    "session_quarantined");

                SafeMigrationAnalysisCleanup.RetainFailure(
                    originalException,
                    disposeFailure,
                    "SafeMigrations.AnalysisConnectionDisposeException");
            }
        }
    }

    private void QuarantineSession(
        Exception originalException
    )
    {
        // WHY: An uncertain session and its caller transaction cannot be
        // reused even if actual Close or Dispose subsequently fails.
        Analyzer.QuarantineConnection(originalException);
        SafeMigrationAnalysisCleanup.RetainRecoveryStatus(
            originalException,
            RecoveryFailureDataKey,
            "session_quarantined");
    }

    /// <summary>Recognizes the bootstrap's exact existing-key rejection before any owned marker is written.</summary>
    private static bool IsRejectedBootstrap(
        Exception failure,
        bool providerDispatched
    ) => !providerDispatched && failure is SqlException { Number: 51007 };

    /// <summary>Prevents an uncertain active SqlClient lease from returning to its reusable connection pool.</summary>
    private static void DiscardPooledSession(
        DbConnection connection
    )
    {
        if (connection is SqlConnection sqlConnection)
        {
            // WHY: Close on a pooled SqlConnection does not physically dispose
            // the session. ClearPool marks this active lease for discard when
            // it closes; an uncertain acquired marker cannot await pool reset.
            SqlConnection.ClearPool(sqlConnection);
        }
    }

    private void ConfigureCleanupCommand(
        IRelationalConnection connection,
        DbCommand command,
        string token,
        bool providerDispatched
    )
    {
        // WHY: A failed bootstrap cannot have executed its own ON. Its recovery
        // may remove only an exact ready marker; even observation failure must
        // quarantine supported reuse without physically closing caller state.
        ConfigureScopeCommand(
            connection,
            command,
            SqlServerSafeMigrationIdentityInsertScope.BuildRecoverySql(
                token,
                _cleanupSql,
                providerDispatched,
                _acquisitionUnprovenMessage));
    }

    /// <summary>Keeps ownership setup and recovery on the borrowed session and current caller transaction.</summary>
    private static void ConfigureScopeCommand(
        IRelationalConnection connection,
        DbCommand command,
        string sql
    )
    {
        command.Transaction = connection.CurrentTransaction?.GetDbTransaction();
        command.CommandTimeout = 30;
        command.CommandText = sql;
    }
}

/// <summary>
/// Rejects supported migration execution after the scoped provider session is quarantined.
/// </summary>
/// <remarks>
/// This command boundary does not intercept arbitrary EF queries or separately executed SQL scripts.
/// </remarks>
internal class SqlServerSafeMigrationGuardedCommand : MigrationCommand
{
    /// <summary>Creates a quarantine gate while retaining the original provider command.</summary>
    /// <param name="command">The original provider command, including all overrides.</param>
    /// <param name="dependencies">The command-building and current-context services.</param>
    /// <param name="analyzer">The scoped provider session-quarantine owner.</param>
    /// <param name="metadataCommand">An optional shared empty metadata command, never a copy of the payload.</param>
    public SqlServerSafeMigrationGuardedCommand(
        MigrationCommand command,
        MigrationsSqlGeneratorDependencies dependencies,
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        IRelationalCommand? metadataCommand = null
    ) : base(
        metadataCommand ?? CreateEmptyMetadataCommand(command, dependencies),
        dependencies.CurrentContext.Context,
        command.CommandLogger,
        command.TransactionSuppressed)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(analyzer);

        OriginalCommand = command;
        Analyzer = analyzer;
    }

    /// <summary>Gets the original provider command, including its execution overrides.</summary>
    protected MigrationCommand OriginalCommand { get; }

    /// <summary>Gets the existing scoped analyzer that owns session quarantine.</summary>
    protected SqlServerSafeMigrationProviderAnalyzer Analyzer { get; }

    /// <inheritdoc />
    public override string CommandText => OriginalCommand.CommandText;

    /// <inheritdoc />
    public override bool TransactionSuppressed => OriginalCommand.TransactionSuppressed;

    /// <inheritdoc />
    public override IRelationalCommandDiagnosticsLogger CommandLogger => OriginalCommand.CommandLogger;

    /// <inheritdoc />
    public override int ExecuteNonQuery(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null
    )
    {
        ThrowIfConnectionQuarantined();

        return OriginalCommand.ExecuteNonQuery(connection, parameterValues);
    }

    /// <inheritdoc />
    public override Task<int> ExecuteNonQueryAsync(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfConnectionQuarantined();

        return OriginalCommand.ExecuteNonQueryAsync(connection, parameterValues, cancellationToken);
    }

    /// <summary>Rejects reuse before delegating any SQL to the original command.</summary>
    protected void ThrowIfConnectionQuarantined() => Analyzer.ThrowIfConnectionQuarantined();

    private static IRelationalCommand CreateEmptyMetadataCommand(

        // WHY: The wrapper must reject a null delegated EF command before publishing its metadata shell.
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
        MigrationCommand command,
        MigrationsSqlGeneratorDependencies dependencies
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(dependencies);

        // WHY: Only the delegated command owns SQL. The empty base command
        // avoids copying a potentially large payload merely to expose metadata.

        return dependencies
            .CommandBuilderFactory
            .Create()
            .Build();
    }
}
