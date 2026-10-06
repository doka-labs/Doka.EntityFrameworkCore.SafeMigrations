namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Holds a session-scoped copy of the catalog views batched classification reads.</summary>
/// <remarks>
/// WHY: MariaDB materialises every INFORMATION_SCHEMA subquery into an internal temporary table,
/// and its HEAP engine cannot hold the TEXT columns those views carry, so each lands on disk as an
/// Aria table. Replaying a real classification statement of 491 catalog subqueries showed 4,453
/// internal temporary tables of which 711 on disk, costing 130.1 ms; against a copy of the views
/// the same statement cost 26.4 ms with 112 temporary tables and one on disk.
///
/// Coverage is not negotiable per view. A first attempt left KEY_COLUMN_USAGE on the live view
/// because the statement reads it only three times; that kept 352 of the 711 on-disk tables and
/// 65 percent of the remaining cost, because the optimiser re-executes those subqueries per row.
/// Every view the classifier can name is therefore copied, and a facet that introduces a new one
/// must extend this list. Index tuning beyond the lookup keys was measured and does not pay.
/// See D-014.
/// </remarks>
internal sealed class MySqlCatalogSnapshot : IAsyncDisposable
{
    private static readonly (string View, string? Predicate, string Keys)[] s_views =
    [
        ("INFORMATION_SCHEMA.COLUMNS", "TABLE_SCHEMA = DATABASE()",
            "KEY `by_ordinal` (`TABLE_NAME`, `ORDINAL_POSITION`),"
            + " KEY `by_name` (`TABLE_NAME`, `COLUMN_NAME`)"),
        ("INFORMATION_SCHEMA.TABLES", "TABLE_SCHEMA = DATABASE()",
            "KEY `by_name` (`TABLE_NAME`)"),
        ("INFORMATION_SCHEMA.STATISTICS", "TABLE_SCHEMA = DATABASE()",
            "KEY `by_index` (`TABLE_NAME`, `INDEX_NAME`, `SEQ_IN_INDEX`)"),
        ("INFORMATION_SCHEMA.TABLE_CONSTRAINTS", "CONSTRAINT_SCHEMA = DATABASE()",
            "KEY `by_name` (`TABLE_NAME`, `CONSTRAINT_NAME`)"),
        ("INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS", "CONSTRAINT_SCHEMA = DATABASE()",
            "KEY `by_name` (`TABLE_NAME`, `CONSTRAINT_NAME`)"),
        ("INFORMATION_SCHEMA.CHECK_CONSTRAINTS", "CONSTRAINT_SCHEMA = DATABASE()",
            "KEY `by_name` (`CONSTRAINT_NAME`)"),
        // WHY: Incoming foreign keys belong to the dependent database, not necessarily the database
        // being analysed. Excluding them would incorrectly certify model-managed parent deletes.
        ("INFORMATION_SCHEMA.KEY_COLUMN_USAGE",
            "CONSTRAINT_SCHEMA = DATABASE() OR REFERENCED_TABLE_SCHEMA = DATABASE()",
            "KEY `by_name` (`CONSTRAINT_SCHEMA`, `TABLE_NAME`, `CONSTRAINT_NAME`, `ORDINAL_POSITION`),"
            + " KEY `by_principal` (`REFERENCED_TABLE_SCHEMA`, `REFERENCED_TABLE_NAME`, `REFERENCED_COLUMN_NAME`)"),
        ("INFORMATION_SCHEMA.COLLATIONS", null, "KEY `by_name` (`COLLATION_NAME`)"),
        ("INFORMATION_SCHEMA.CHARACTER_SETS", null, "KEY `by_name` (`CHARACTER_SET_NAME`)"),
    ];

    private readonly DbConnection _connection;
    private readonly int? _commandTimeout;
    private readonly string[] _tables = new string[s_views.Length];
    private int _ownedCount;
    private bool _disposed;
    private bool _cleanupSucceeded;
    private bool _canUseLiveCatalog;

    // WHY: Redirection runs once per catalog relation of every rendered expression, so the
    // delimited names are precomputed and handed out rather than formatted per call.
    private readonly Dictionary<string, string> _relations = new(s_views.Length, StringComparer.Ordinal);

    private MySqlCatalogSnapshot(
        DbConnection connection,
        int? commandTimeout
    )
    {
        _connection = connection;
        _commandTimeout = commandTimeout;
        var identity = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        for (var index = 0; index < s_views.Length; index++)
        {
            // WHY: A unique, bounded identifier isolates this snapshot from caller-owned temporary
            // tables and from another snapshot on the same session without deleting either.
            var table = $"`__doka_sm_cat_{identity}_{index.ToString(CultureInfo.InvariantCulture)}`";
            _tables[index] = table;
            _relations.Add(s_views[index].View, table);
        }
    }

    /// <summary>Copies the catalog views of the connected database into session temporary tables.</summary>
    /// <param name="connection">The open analysis connection.</param>
    /// <param name="commandTimeout">The configured command timeout.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The snapshot, or null when temporary-table creation or reliable session cleanup is unavailable.</returns>
    public static async Task<MySqlCatalogSnapshot?> TryCreateAsync(
        DbConnection connection,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(connection);

        var settings = new MySqlConnectionStringBuilder(connection.ConnectionString);
        if (settings.Pooling && !settings.ConnectionReset)
        {
            // WHY: A failed DROP must not leak copies into a reused pooled session. ConnectionReset
            // clears temporary objects before reuse, including MySqlDataSource pools and wrappers;
            // without that guarantee the existing live-catalog path remains the safe option.
            return null;
        }

        var snapshot = new MySqlCatalogSnapshot(connection, commandTimeout);
        try
        {
            var builder = new StringBuilder(512);
            for (var index = 0; index < s_views.Length; index++)
            {
                var (view, predicate, keys) = s_views[index];
                // WHY: Catalog views contain TEXT columns, which MEMORY cannot store. Pin the
                // snapshot engine instead of inheriting the consumer's default temporary engine.
                builder.Clear().Append("CREATE TEMPORARY TABLE ").Append(snapshot._tables[index])
                    .Append(" (").Append(keys).Append(") ENGINE=InnoDB AS SELECT * FROM ").Append(view);
                if (predicate is not null)
                {
                    builder.Append(" WHERE ").Append(predicate);
                }

                // WHY: Inline indexes avoid extra ALTER statements. Separate acknowledgements
                // establish ownership after each successful CREATE; a failed/colliding target is never dropped.
                await snapshot.CreateTableAsync(builder.Append(';').ToString(), cancellationToken);
            }
        }
        catch (Exception exception)
        {
            var cleaned = await snapshot.DisposeAfterFailureAsync(exception);
            if (cleaned && connection.State == System.Data.ConnectionState.Open && snapshot._canUseLiveCatalog)
            {
                return null;
            }

            throw;
        }

        return snapshot;
    }

    /// <summary>Resolves a catalog relation to the copy that replaces it.</summary>
    /// <param name="view">The catalog relation a template names.</param>
    /// <returns>The snapshot table, or the live view when the snapshot does not carry it.</returns>
    public string Resolve(
        string view
    ) => _relations.TryGetValue(view, out var table) ? table : view;

    /// <summary>Drops the snapshot tables of this session.</summary>
    /// <returns>A task that completes when the session holds no snapshot.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownedCount == 0)
        {
            _cleanupSucceeded = true;

            return;
        }

        if (_connection.State != System.Data.ConnectionState.Open)
        {
            var exception = new InvalidOperationException("The catalog snapshot session is no longer available.");
            await InvalidateConnectionAsync(exception);

            throw exception;
        }

        // WHY: One statement drops every copy, because the analyzer's catalog round trips are
        // bounded by tests and a drop per view would spend that budget on cleanup.
        var builder = new StringBuilder(512).Append("DROP TEMPORARY TABLE IF EXISTS ");
        for (var index = 0; index < _ownedCount; index++)
        {
            builder.Append(index == 0 ? string.Empty : ", ").Append(_tables[index]);
        }

        try
        {
            await ExecuteAsync(builder.Append(';').ToString(), CancellationToken.None);
            _ownedCount = 0;
            _cleanupSucceeded = true;
        }
        catch (Exception exception)
        {
            // WHY: A failed DROP ends this analysis session. Non-pooled close removes its temporary
            // objects; pooled sessions are reset before reuse, as required at snapshot creation.
            await InvalidateConnectionAsync(exception);

            throw;
        }
    }

    /// <summary>Cleans up without replacing an exception already raised by analysis or creation.</summary>
    /// <param name="primaryException">The failure whose type and stack must be preserved.</param>
    /// <returns>True when all owned tables were removed without a cleanup failure.</returns>
    public async ValueTask<bool> DisposeAfterFailureAsync(
        Exception primaryException
    )
    {
        ArgumentNullException.ThrowIfNull(primaryException);

        try
        {
            await DisposeAsync();

            return _cleanupSucceeded;
        }
        catch (Exception cleanupException)
        {
            // WHY: Keep secondary diagnostics available without replacing cancellation, timeout, or
            // the original catalog failure with a less useful error from the cleanup command.
            primaryException.Data["SafeMigrations.CatalogSnapshotCleanupException"] = cleanupException;

            return false;
        }
    }

    // WHY: The fixed InnoDB copy is optional, just like temporary-table privileges. Its absence
    // must not prevent live catalog analysis; unrelated SQL, storage, and transport errors still fail.
    private static bool IsUnavailable(
        Exception exception
    ) => exception is MySqlException
    {
        ErrorCode: MySqlErrorCode.DatabaseAccessDenied
        or MySqlErrorCode.TableAccessDenied
        or MySqlErrorCode.CannotExecuteInReadOnlyTransaction
        or MySqlErrorCode.UnknownStorageEngine,
    };

    private async Task InvalidateConnectionAsync(
        Exception primaryException
    )
    {
        if (_connection is MySqlConnection connection)
        {
            try
            {
                // WHY: Evict the ordinary connection-string pool as extra protection. MySqlDataSource
                // owns a separate pool, so correctness relies on reset-before-reuse rather than this call.
                await MySqlConnection.ClearPoolAsync(connection, CancellationToken.None);
            }
            catch (Exception clearException)
            {
                primaryException.Data["SafeMigrations.CatalogSnapshotPoolException"] = clearException;
            }
        }

        try
        {
            await _connection.CloseAsync();
        }
        catch (Exception closeException)
        {
            primaryException.Data["SafeMigrations.CatalogSnapshotCloseException"] = closeException;
        }
    }

    /// <summary>Records acknowledged table ownership before the command wrapper can fail during disposal.</summary>
    private async Task CreateTableAsync(
        string sql,
        CancellationToken cancellationToken
    )
    {
        var command = _connection.CreateCommand();
        try
        {
            command.CommandText = sql;
            if (_commandTimeout is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
            // WHY: A command wrapper may throw from DisposeAsync after the server created the table.
            // Ownership must already be recorded so that creation-failure cleanup includes this object.
            _ownedCount++;
        }
        catch (Exception exception)
        {
            var commandCleaned = await DisposeCommandAfterFailureAsync(command, exception);
            // WHY: Capability fallback applies only to the CREATE execution, never a wrapper's
            // disposal error, and only when both command and table cleanup completed successfully.
            _canUseLiveCatalog = commandCleaned && IsUnavailable(exception);

            throw;
        }

        await command.DisposeAsync();
    }

    private async Task ExecuteAsync(
        string sql,
        CancellationToken cancellationToken
    )
    {
        var command = _connection.CreateCommand();
        try
        {
            command.CommandText = sql;
            if (_commandTimeout is { } timeout)
            {
                command.CommandTimeout = timeout;
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            await DisposeCommandAfterFailureAsync(command, exception);

            throw;
        }

        await command.DisposeAsync();
    }

    /// <summary>Preserves a command's execution failure if its wrapper also fails during disposal.</summary>
    private static async ValueTask<bool> DisposeCommandAfterFailureAsync(
        DbCommand command,
        Exception primaryException
    )
    {
        try
        {
            await command.DisposeAsync();

            return true;
        }
        catch (Exception cleanupException)
        {
            // WHY: await-using would replace the execution failure with the disposal failure.
            // Retain secondary diagnostics without changing the failure that determines fallback.
            primaryException.Data["SafeMigrations.CatalogSnapshotCommandCleanupException"] = cleanupException;

            return false;
        }
    }
}
