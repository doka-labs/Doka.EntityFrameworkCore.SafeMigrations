namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    /// <summary>Releases any outstanding scope when the scoped analyzer is disposed.</summary>
    public ValueTask DisposeAsync() => _analysisScope?.DisposeAsync() ?? ValueTask.CompletedTask;

    /// <summary>Releases an outstanding scope when EF disposes its service scope synchronously.</summary>
    public void Dispose() => _analysisScope?.Dispose();

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
        DbContext context,
        CancellationToken cancellationToken
    )
    {
        ValidateContext(context);

        if (_analysisScope is not null)
        {
            throw new InvalidOperationException("The SQL Server analyzer already owns an active analysis scope.");
        }

        ClearCatalogPreamble();

        IDbContextTransaction? transaction = null;
        AnalysisScope? acquiredScope = null;
        var acquisitionMayOwnLock = false;
        try
        {
            if (context.Database.CurrentTransaction is null)
            {
                // WHY: SQL Server system catalog metadata is not versioned like
                // ordinary row data. ReadCommitted avoids SNAPSHOT metadata
                // conflicts; runtime guards recheck immediately before DDL.
                transaction = await context.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
            }

            var connection = context.Database.GetDbConnection();

            // WHY: Virtual identity getters and transaction metadata can fail. Capture them
            // before acquiring the application lock so a borrowed lock cannot lose its owner
            // between successful native acquisition and construction of the releasing scope.
            var database = connection.Database;
            var connectionString = connection.ConnectionString;
            var commandTimeout = context.Database.GetCommandTimeout();
            var activeTransaction = context.Database.CurrentTransaction?.GetDbTransaction()
                ?? throw new InvalidOperationException("The SQL Server analysis scope requires a transaction.");

            var command = connection.CreateCommand();
            Exception? commandFailure = null;
            try
            {
                command.Transaction = activeTransaction;
                ApplyCommandTimeout(command, commandTimeout);
                command.CommandText = "DECLARE @result int; " + ScopeSql;

                // WHY: A failed response supplies no negative native return code. Until the result
                // proves rejection, an issued acquisition may already own a transaction-scoped lock.
                acquisitionMayOwnLock = true;
                var lockResult = ReadApplicationLockStatus(await command.ExecuteScalarAsync(cancellationToken));

                if (lockResult < 0)
                {
                    acquisitionMayOwnLock = false;

                    throw new InvalidOperationException(
                        "SQL Server SafeMigrations could not acquire its analysis lock.");
                }

                acquiredScope = new AnalysisScope(
                    this,
                    context,
                    connection,
                    activeTransaction,
                    transaction,
                    commandTimeout,
                    database,
                    connectionString);
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

            // WHY: Publish only after bootstrap command disposal succeeds. A failed
            // handoff must release even a borrowed transaction's acquired lock.
            _analysisScope = acquiredScope;

            return _analysisScope;
        }
        catch (Exception exception)
        {
            if (acquiredScope is not null)
            {
                var scopeDisposed = await SafeMigrationAnalysisCleanup.DisposeAsync(
                    acquiredScope,
                    exception,
                    "SafeMigrations.AnalysisScopeDisposeException");

                if (!scopeDisposed)
                {
                    QuarantineConnection();
                }
            }
            else
            {
                if (transaction is null && acquisitionMayOwnLock)
                {
                    // WHY: Never roll back a caller transaction or release unproven/reentrant lock
                    // ownership. Quarantine supported reuse when no owned transaction can end it.
                    QuarantineConnection();
                }

                var transactionDisposed = await SafeMigrationAnalysisCleanup.DisposeAsync(
                    transaction,
                    exception,
                    "SafeMigrations.AnalysisTransactionDisposeException");

                if (!transactionDisposed)
                {
                    QuarantineConnection();
                }
            }

            throw;
        }
    }

    /// <summary>Permanently rejects supported migration and catalog reuse of this scoped session.</summary>
    /// <param name="originalFailure">The first known failure of the active scope, or null when none is known.</param>
    internal void QuarantineConnection(
        Exception? originalFailure = null
    )
    {
        _connectionQuarantined = true;
        if (originalFailure is not null)
        {
            _analysisScope?.RetainPrimaryFailure(originalFailure);
        }

        ClearCatalogPreamble();
    }

    /// <summary>Requires the actual CLR representation of the declared T-SQL integer status.</summary>
    /// <param name="result">The scalar returned by an application-lock acquisition or release.</param>
    /// <returns>The native status without fabricating success from a missing or convertible value.</returns>
    private static int ReadApplicationLockStatus(
        object? result
    )
    {
        // WHY: Both lock statements SELECT a declared int, mapped by SqlClient to Int32.
        // Convert.ToInt32(null) and conversions from unrelated scalar types can fabricate zero.
        return result is int status
            ? status
            : throw new InvalidOperationException("SQL Server did not return an integer application-lock status.");
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
    ) => _analysisScope?.Matches(context, connection, transaction) == true;

    /// <summary>Requires a replacement context after any provider session-recovery failure.</summary>
    internal void ThrowIfConnectionQuarantined()
    {
        if (_connectionQuarantined)
        {
            throw new InvalidOperationException(
                "SQL Server SafeMigrations requires a new context after session recovery failed.");
        }
    }

    /// <summary>Proves an invocation-owned temporary scope is gone or quarantines its connection.</summary>
    /// <param name="connection">The borrowed connection retaining the temporary scope.</param>
    /// <param name="transaction">The caller transaction, which is never explicitly rolled back here.</param>
    /// <param name="temporaryTable">The generated invocation-owned table name.</param>
    /// <param name="commandTimeout">The optional caller timeout, capped for cleanup.</param>
    /// <param name="originalFailure">The original analysis failure to preserve when recovery also fails.</param>
    /// <returns>Completion of the actual cleanup and any required physical recovery.</returns>
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
            throw new ArgumentException(
                "Cleanup requires an invocation-owned temporary scope.",
                nameof(temporaryTable));
        }

        try
        {
            await CleanupTemporaryCommandAsync(connection, transaction, temporaryTable, commandTimeout);
        }
        catch (Exception cleanupFailure)
        {
            QuarantineConnection(originalFailure ?? cleanupFailure);
            string recoveryStatus;
            try
            {
                await connection.CloseAsync();

                recoveryStatus = "connection_closed";
            }
            catch (Exception closeFailure)
            {
                SafeMigrationAnalysisCleanup.RetainFailure(
                    cleanupFailure,
                    closeFailure,
                    "SafeMigrations.AnalysisConnectionCloseException");

                // WHY: A wrapper may fail to close. Disposal is the remaining
                // connection boundary; the analyzer never reuses this context.
                try
                {
                    await connection.DisposeAsync();

                    recoveryStatus = "connection_disposed";
                }
                catch (Exception disposeFailure)
                {
                    SafeMigrationAnalysisCleanup.RetainFailure(
                        cleanupFailure,
                        disposeFailure,
                        "SafeMigrations.AnalysisConnectionDisposeException");

                    recoveryStatus = "connection_quarantined";
                }
            }

            if (originalFailure is not null)
            {
                // WHY: Preserve cancellation/provider failure identity, not a
                // secondary DROP error. Recovery status contains no SQL names.
                SafeMigrationAnalysisCleanup.RetainRecoveryStatus(
                    originalFailure,
                    "Doka.SafeMigrations.SqlServer.RecoveryStatus",
                    recoveryStatus);

                SafeMigrationAnalysisCleanup.RetainFailure(
                    originalFailure,
                    cleanupFailure,
                    "Doka.SafeMigrations.SqlServer.CleanupException");

                return;
            }

            var failure = new InvalidOperationException(
                "SQL Server SafeMigrations temporary-scope cleanup failed; recreate the context "
                + "and treat any caller transaction as unusable.",
                cleanupFailure)
            {
                Data =
                {
                    ["Doka.SafeMigrations.SqlServer.RecoveryStatus"] = recoveryStatus,
                },
            };

            throw failure;
        }
    }

    /// <summary>Retains the cleanup command until its actual execution and disposal have completed.</summary>
    /// <param name="connection">The borrowed physical connection containing the owned temporary table.</param>
    /// <param name="transaction">The unchanged borrowed transaction.</param>
    /// <param name="temporaryTable">The already validated invocation-owned temporary table.</param>
    /// <param name="commandTimeout">The native timeout contract, capped independently of observer cancellation.</param>
    /// <returns>Completion of the actual native work, including command disposal.</returns>
    private static async Task CleanupTemporaryCommandAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string temporaryTable,
        int? commandTimeout
    )
    {
        DbCommand? command = null;
        Exception? executionFailure = null;
        try
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = commandTimeout is > 0
                ? Math.Min(commandTimeout.Value, 30)
                : 30;

            command.CommandText = $"IF OBJECT_ID(N'tempdb..{temporaryTable}') IS NOT NULL DROP TABLE {temporaryTable};";

            // WHY: Protected cleanup cannot abandon a borrowed command when only an
            // observer is canceled. Its native command timeout remains authoritative.
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            executionFailure = exception;

            throw;
        }
        finally
        {
            await SafeMigrationAnalysisCleanup.DisposeAsync(
                command,
                executionFailure,
                "SafeMigrations.AnalysisCommandDisposeException");
        }
    }

    /// <summary>Owns catalog-proof lifetime and the transaction-owned analysis lock.</summary>
    private sealed class AnalysisScope : IDisposable, IAsyncDisposable
    {
        private readonly SqlServerSafeMigrationProviderAnalyzer _analyzer;
        private readonly int? _commandTimeout;
        private readonly DbConnection _connection;
        private readonly string _connectionString;
        private readonly DbContext _context;
        private readonly string _database;
        private readonly IDbContextTransaction? _ownedTransaction;
        private readonly DbTransaction _transaction;
        private bool _disposed;
        private Exception? _primaryFailure;
        private bool _sessionInvalidated;

        /// <summary>Captures the physical session after its analysis lock has been acquired.</summary>
        /// <param name="analyzer">The analyzer whose proofs belong to this scope.</param>
        /// <param name="context">The exact context supplying the session.</param>
        /// <param name="connection">The locked physical connection.</param>
        /// <param name="transaction">The transaction owning the application lock.</param>
        /// <param name="ownedTransaction">The EF transaction created here, or null for a borrowed transaction.</param>
        /// <param name="commandTimeout">The timeout used to release a borrowed lock.</param>
        /// <param name="database">The database identity captured before native application-lock acquisition.</param>
        /// <param name="connectionString">The connection identity captured before native acquisition.</param>
        public AnalysisScope(
            SqlServerSafeMigrationProviderAnalyzer analyzer,
            DbContext context,
            DbConnection connection,
            DbTransaction transaction,
            IDbContextTransaction? ownedTransaction,
            int? commandTimeout,
            string database,
            string connectionString
        )
        {
            _analyzer = analyzer;
            _context = context;
            _connection = connection;
            _transaction = transaction;
            _ownedTransaction = ownedTransaction;
            _commandTimeout = commandTimeout;
            _database = database;
            _connectionString = connectionString;
            connection.StateChange += OnConnectionStateChange;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (!Detach())
            {
                return;
            }

            var originalFailure = _primaryFailure;
            _primaryFailure = null;

            if (_analyzer._connectionQuarantined)
            {
                // WHY: A raw await-using caller may already be unwinding the known work failure.
                // Retain owned cleanup evidence under that exact exception rather than replacing it.
                // No known primary means a disposal fault must reach the existing outer cleanup owner.
                if (_ownedTransaction is not null)
                {
                    await SafeMigrationAnalysisCleanup.DisposeAsync(
                        _ownedTransaction,
                        originalFailure,
                        "SafeMigrations.AnalysisTransactionDisposeException");
                }

                return;
            }

            try
            {
                if (_ownedTransaction is not null)
                {
                    await _ownedTransaction.DisposeAsync();

                    return;
                }

                if (!CanReleaseBorrowedLock())
                {
                    return;
                }

                // WHY: Transaction-owned locks need one release per successful acquisition.
                // Own the command before its provider setters can fail during configuration.
                DbCommand? command = null;
                Exception? releaseFailure = null;
                try
                {
                    command = _connection.CreateCommand();
                    ConfigureReleaseCommand(command);
                    ValidateReleaseResult(await command.ExecuteScalarAsync(CancellationToken.None));
                }
                catch (Exception exception)
                {
                    releaseFailure = exception;

                    throw;
                }
                finally
                {
                    await SafeMigrationAnalysisCleanup.DisposeAsync(
                        command,
                        releaseFailure,
                        "SafeMigrations.AnalysisCommandDisposeException");
                }
            }
            catch (Exception)
            {
                // WHY: Detaching proofs does not prove that native lock ownership ended.
                // Failed release, command cleanup or owned transaction disposal forbids reuse.
                _analyzer.QuarantineConnection();

                throw;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (!Detach())
            {
                return;
            }

            var originalFailure = _primaryFailure;
            _primaryFailure = null;

            // WHY: EF can dispose its scoped services synchronously. Use synchronous ADO.NET
            // cleanup instead of blocking an asynchronous continuation on a caller's context.
            if (_analyzer._connectionQuarantined)
            {
                try
                {
                    _ownedTransaction?.Dispose();
                }
                catch (Exception cleanupFailure) when (originalFailure is not null)
                {
                    SafeMigrationAnalysisCleanup.RetainFailure(
                        originalFailure,
                        cleanupFailure,
                        "SafeMigrations.AnalysisTransactionDisposeException");
                }

                return;
            }

            try
            {
                if (_ownedTransaction is not null)
                {
                    _ownedTransaction.Dispose();

                    return;
                }

                if (!CanReleaseBorrowedLock())
                {
                    return;
                }

                DbCommand? command = null;
                Exception? releaseFailure = null;
                try
                {
                    command = _connection.CreateCommand();
                    ConfigureReleaseCommand(command);
                    ValidateReleaseResult(command.ExecuteScalar());
                }
                catch (Exception exception)
                {
                    releaseFailure = exception;

                    throw;
                }
                finally
                {
                    try
                    {
                        command?.Dispose();
                    }
                    catch (Exception exception) when (releaseFailure is not null)
                    {
                        SafeMigrationAnalysisCleanup.RetainFailure(
                            releaseFailure,
                            exception,
                            "SafeMigrations.AnalysisCommandDisposeException");
                    }
                }
            }
            catch (Exception)
            {
                _analyzer.QuarantineConnection();

                throw;
            }
        }

        /// <summary>Retains only the active invocation's first known primary until its actual cleanup starts.</summary>
        /// <param name="originalFailure">The original work error, never a later recovery failure.</param>
        public void RetainPrimaryFailure(
            Exception originalFailure
        ) => _primaryFailure ??= originalFailure;

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
            if (_disposed
                || _sessionInvalidated
                || !ReferenceEquals(_context, context))
            {
                return false;
            }

            if (!ReferenceEquals(_connection, connection)
                || !ReferenceEquals(_transaction, transaction)
                || !ReferenceEquals(_transaction.Connection, connection)
                || connection.State != ConnectionState.Open
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

        private void OnConnectionStateChange(
            object? sender,
            StateChangeEventArgs args
        )
        {
            if (args.CurrentState != ConnectionState.Open)
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

        /// <summary>Skips release only after the original session or transaction has already ended.</summary>
        private bool CanReleaseBorrowedLock() =>

            // WHY: EF service disposal can tear down its relational connection before this
            // analyzer. A transaction-owned lock ends with that transaction/session; attempting
            // another command then would turn successful disposal into a provider exception.
            _connection.State == ConnectionState.Open && ReferenceEquals(_transaction.Connection, _connection);

        /// <summary>Configures one already-owned release command for the caller's acquired application lock.</summary>
        /// <param name="command">The command already retained by its caller's protected cleanup boundary.</param>
        private void ConfigureReleaseCommand(
            DbCommand command
        )
        {
            command.Transaction = _transaction;
            ApplyCommandTimeout(command, _commandTimeout);
            command.CommandText = ReleaseScopeSql;
        }

        private static void ValidateReleaseResult(
            object? result
        )
        {
            if (ReadApplicationLockStatus(result) < 0)
            {
                throw new InvalidOperationException("SQL Server did not release the borrowed analysis lock.");
            }
        }
    }
}
