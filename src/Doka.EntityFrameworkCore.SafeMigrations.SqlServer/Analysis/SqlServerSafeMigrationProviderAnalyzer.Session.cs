using System.Data;

namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
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
    ) => _analysisScope?.Matches(context, connection, transaction) == true;

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

        IDbContextTransaction? transaction = null;
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
            await using var command = connection.CreateCommand();
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            ApplyCommandTimeout(command, context.Database.GetCommandTimeout());
            command.CommandText = "DECLARE @result int; " + ScopeSql;
            var lockResult = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken),
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
            throw new ArgumentException(
                "Cleanup requires an invocation-owned temporary scope.",
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
                await connection
                    .CloseAsync()
                    .WaitAsync(closeTimeout.Token);

                recoveryStatus = "connection_closed";
            }
            catch (Exception)
            {
                // WHY: A wrapper may fail to close. Disposal is the remaining
                // connection boundary; the analyzer never reuses this context.
                try
                {
                    using var disposeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await connection
                        .DisposeAsync()
                        .AsTask()
                        .WaitAsync(disposeTimeout.Token);

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

    /// <summary>Owns catalog-proof lifetime and the transaction-owned analysis lock.</summary>
    private sealed class AnalysisScope : IDisposable, IAsyncDisposable
    {
        private readonly SqlServerSafeMigrationProviderAnalyzer _analyzer;
        private readonly DbContext _context;
        private readonly DbConnection _connection;
        private readonly DbTransaction _transaction;
        private readonly IDbContextTransaction? _ownedTransaction;
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
            IDbContextTransaction? ownedTransaction,
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
                    // ignored
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
                        // ignored
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
        private bool CanReleaseBorrowedLock() =>
            // WHY: EF service disposal can tear down its relational connection before this
            // analyzer. A transaction-owned lock ends with that transaction/session; attempting
            // another command then would turn successful disposal into a provider exception.
            _connection.State == ConnectionState.Open && ReferenceEquals(_transaction.Connection, _connection);

        /// <summary>Builds one bounded release of the caller transaction's acquired application lock.</summary>
        private DbCommand CreateReleaseCommand()
        {
            var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            ApplyCommandTimeout(command, _commandTimeout);
            command.CommandText = ReleaseScopeSql;

            return command;
        }

        private static void ValidateReleaseResult(
            object? result
        )
        {
            if (Convert.ToInt32(result, CultureInfo.InvariantCulture) < 0)
            {
                throw new InvalidOperationException("SQL Server did not release the borrowed analysis lock.");
            }
        }
    }
}
