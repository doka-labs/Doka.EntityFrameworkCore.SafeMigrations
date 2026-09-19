namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Caches one SQLite catalog snapshot for the active connection and transaction.</summary>
internal sealed class SqliteSafeMigrationExecutionState
{
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private SqliteCatalogSnapshot? _snapshot;

    /// <summary>Gets the current catalog snapshot, refreshing it when execution scope changes.</summary>
    public SqliteCatalogSnapshot GetSnapshot(
        IRelationalConnection connection
    )
    {
        ArgumentNullException.ThrowIfNull(connection);

        var dbConnection = connection.DbConnection;
        var transaction = connection.CurrentTransaction?.GetDbTransaction();
        if (_snapshot is null
            || !ReferenceEquals(_connection, dbConnection)
            || !ReferenceEquals(_transaction, transaction))
        {
            _snapshot = SqliteSafeMigrationCatalog.Read(dbConnection, transaction);
            _connection = dbConnection;
            _transaction = transaction;
        }

        return _snapshot;
    }

    /// <summary>Invalidates the cached snapshot after catalog-changing SQL.</summary>
    public void Invalidate()
    {
        _connection = null;
        _transaction = null;
        _snapshot = null;
    }
}
