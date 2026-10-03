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

        try
        {
            return OriginalCommand.ExecuteNonQuery(connection, parameterValues);
        }
        catch (Exception exception)
        {
            RecoverSession(connection, exception);

            throw;
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

        try
        {
            return await OriginalCommand.ExecuteNonQueryAsync(connection, parameterValues, cancellationToken);
        }
        catch (Exception exception)
        {
            await RecoverSessionAsync(connection, exception);

            throw;
        }
    }

    private void RecoverSession(
        IRelationalConnection connection,
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
            ConfigureCleanupCommand(connection, command);
            command.ExecuteNonQuery();
        }
        catch
        {
            QuarantineSession(originalException);
            recoveryRequired = true;
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
                    recoveryRequired = true;
                }
            }
        }

        if (!recoveryRequired)
        {
            return;
        }

        try
        {
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
            ConfigureCleanupCommand(connection, command);

            // WHY: SQL Server TRY/CATCH does not catch client attentions.
            // Recovery must outlive the canceled operation's token, but it
            // remains bounded independently of the caller's canceled token.
            await command.ExecuteNonQueryAsync(cleanupTimeout.Token).WaitAsync(cleanupTimeout.Token);
        }
        catch
        {
            QuarantineSession(originalException);
            recoveryRequired = true;
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
                    recoveryRequired = true;
                }
            }
        }

        if (!recoveryRequired)
        {
            return;
        }

        try
        {
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

    private void ConfigureCleanupCommand(
        IRelationalConnection connection,
        DbCommand command
    )
    {
        command.Transaction = connection.CurrentTransaction?.GetDbTransaction();
        command.CommandTimeout = 30;
        command.CommandText = _cleanupSql;
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
