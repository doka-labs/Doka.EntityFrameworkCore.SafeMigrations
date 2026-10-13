namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>
/// Recovers session-scoped IDENTITY_INSERT when a guarded seed command fails.
/// </summary>
internal sealed class SqlServerSafeMigrationIdentityInsertCommand : SqlServerSafeMigrationGuardedCommand
{
    internal const string RecoveryFailureDataKey = "Doka:SqlServerIdentityInsertRecovery";

    private readonly string _cleanupSql;

    /// <summary>Creates a seed-command recovery boundary using the existing scoped quarantine.</summary>
    /// <param name="command">The original provider command to delegate unchanged.</param>
    /// <param name="cleanupSql">The same-session IDENTITY_INSERT recovery statement.</param>
    /// <param name="dependencies">The command-building and current-context services.</param>
    /// <param name="analyzer">The scoped provider session-quarantine owner.</param>
    /// <param name="metadataCommand">An optional shared empty metadata command, never a copy of the payload.</param>
    public SqlServerSafeMigrationIdentityInsertCommand(
        MigrationCommand command,
        string cleanupSql,
        MigrationsSqlGeneratorDependencies dependencies,
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        IRelationalCommand? metadataCommand = null
    ) : base(command, dependencies, analyzer, metadataCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cleanupSql);

        _cleanupSql = cleanupSql;
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
        var token = Guid.NewGuid().ToString("N");
        var scopeOpened = false;
        var scopeIssued = false;
        var providerDispatched = false;
        Exception? originalFailure = null;

        try
        {
            connection.Open();
            scopeOpened = true;

            using (var command = connection.DbConnection.CreateCommand())
            {
                ConfigureScopeCommand(connection, command,
                    SqlServerSafeMigrationIdentityInsertScope.BuildBootstrapSql(token));
                scopeIssued = true;
                command.ExecuteNonQuery();
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
                catch when (originalFailure is not null)
                {
                    QuarantineSession(originalFailure);
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
        var token = Guid.NewGuid().ToString("N");
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
            await using (var command = connection.DbConnection.CreateCommand())
            {
                ConfigureScopeCommand(connection, command,
                    SqlServerSafeMigrationIdentityInsertScope.BuildBootstrapSql(token));
                scopeIssued = true;

                await command.ExecuteNonQueryAsync(cancellationToken);
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
                catch when (originalFailure is not null)
                {
                    QuarantineSession(originalFailure);
                }
            }
        }
    }

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
            if (connection.DbConnection.State != System.Data.ConnectionState.Open)
            {
                return;
            }

            command = connection.DbConnection.CreateCommand();
            ConfigureCleanupCommand(connection, command, token, providerDispatched);
            command.ExecuteNonQuery();
        }
        catch
        {
            QuarantineSession(originalException);
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
                catch
                {
                    QuarantineSession(originalException);
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
            originalException.Data[RecoveryFailureDataKey] = "session_closed";
        }
        catch
        {
            try
            {
                connection.DbConnection.Dispose();
                originalException.Data[RecoveryFailureDataKey] = "session_disposed";
            }
            catch
            {
                // WHY: Even both failed physical boundaries cannot clear the
                // scoped quarantine or replace the original migration failure.
                originalException.Data[RecoveryFailureDataKey] = "session_quarantined";
            }
        }
    }

    private async Task RecoverSessionAsync(
        IRelationalConnection connection,
        string token,
        bool providerDispatched,
        Exception originalException
    )
    {
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        DbCommand? command = null;
        var recoveryRequired = false;

        try
        {
            if (connection.DbConnection.State != System.Data.ConnectionState.Open)
            {
                return;
            }

            command = connection.DbConnection.CreateCommand();
            ConfigureCleanupCommand(connection, command, token, providerDispatched);

            // WHY: SQL Server TRY/CATCH does not catch client attentions.
            // Recovery must outlive the canceled operation's token, but it
            // remains bounded independently of the caller's canceled token.
            await command.ExecuteNonQueryAsync(cleanupTimeout.Token).WaitAsync(cleanupTimeout.Token);
        }
        catch
        {
            QuarantineSession(originalException);
            recoveryRequired = providerDispatched;
        }
        finally
        {
            if (command is not null)
            {
                try
                {
                    await command.DisposeAsync().AsTask().WaitAsync(cleanupTimeout.Token);
                }
                catch
                {
                    QuarantineSession(originalException);
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
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await connection.DbConnection.CloseAsync().WaitAsync(closeTimeout.Token);
            originalException.Data[RecoveryFailureDataKey] = "session_closed";
        }
        catch
        {
            try
            {
                using var disposeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await connection.DbConnection.DisposeAsync().AsTask().WaitAsync(disposeTimeout.Token);
                originalException.Data[RecoveryFailureDataKey] = "session_disposed";
            }
            catch
            {
                originalException.Data[RecoveryFailureDataKey] = "session_quarantined";
            }
        }
    }

    private void QuarantineSession(
        Exception originalException
    )
    {
        // WHY: An uncertain session and its caller transaction cannot be
        // reused even if Close or Dispose subsequently fails or times out.
        Analyzer.QuarantineConnection();
        originalException.Data[RecoveryFailureDataKey] = "session_quarantined";
    }

    /// <summary>Recognizes the bootstrap's exact existing-key rejection before any owned marker is written.</summary>
    private static bool IsRejectedBootstrap(Exception failure, bool providerDispatched)
        => !providerDispatched && failure is Microsoft.Data.SqlClient.SqlException { Number: 51007 };

    /// <summary>Prevents an uncertain active SqlClient lease from returning to its reusable connection pool.</summary>
    private static void DiscardPooledSession(DbConnection connection)
    {
        if (connection is Microsoft.Data.SqlClient.SqlConnection sqlConnection)
        {
            // WHY: Close on a pooled SqlConnection does not physically dispose
            // the session. ClearPool marks this active lease for discard when
            // it closes; an uncertain acquired marker cannot await pool reset.
            Microsoft.Data.SqlClient.SqlConnection.ClearPool(sqlConnection);
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
        ConfigureScopeCommand(connection, command,
            SqlServerSafeMigrationIdentityInsertScope.BuildRecoverySql(token, _cleanupSql, providerDispatched));
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
    public override Microsoft.EntityFrameworkCore.Diagnostics.IRelationalCommandDiagnosticsLogger CommandLogger
        => OriginalCommand.CommandLogger;

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
        MigrationCommand command,
        MigrationsSqlGeneratorDependencies dependencies
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(dependencies);

        // WHY: Only the delegated command owns SQL. The empty base command
        // avoids copying a potentially large payload merely to expose metadata.

        return dependencies.CommandBuilderFactory.Create().Build();
    }
}
