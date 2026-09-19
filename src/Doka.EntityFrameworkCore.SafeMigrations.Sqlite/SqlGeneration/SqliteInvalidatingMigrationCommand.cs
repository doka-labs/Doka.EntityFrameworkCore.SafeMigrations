namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite;

/// <summary>Invalidates cached catalog state after an ordinary provider command.</summary>
internal sealed class SqliteInvalidatingMigrationCommand : MigrationCommand
{
    private readonly MigrationCommand _inner;
    private readonly SqliteSafeMigrationExecutionState _executionState;
    private readonly bool _invalidatesCatalog;

    /// <summary>Initializes the catalog-invalidating command wrapper.</summary>
    public SqliteInvalidatingMigrationCommand(
        MigrationCommand inner,
        SqliteSafeMigrationExecutionState executionState,
        bool invalidatesCatalog,
        IRelationalCommand placeholder,
        DbContext context,
        IRelationalCommandDiagnosticsLogger logger
    ) : base(placeholder, context, logger, inner.TransactionSuppressed)
    {
        _inner = inner;
        _executionState = executionState;
        _invalidatesCatalog = invalidatesCatalog;
    }

    /// <inheritdoc />
    public override string CommandText => _inner.CommandText;

    /// <inheritdoc />
    public override IRelationalCommandDiagnosticsLogger CommandLogger => _inner.CommandLogger;

    /// <inheritdoc />
    public override int ExecuteNonQuery(
        IRelationalConnection connection,
        IReadOnlyDictionary<string, object?>? parameterValues = null
    )
    {
        try
        {
            return _inner.ExecuteNonQuery(connection, parameterValues);
        }
        finally
        {
            if (_invalidatesCatalog)
            {
                _executionState.Invalidate();
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
        try
        {
            return await _inner.ExecuteNonQueryAsync(connection, parameterValues, cancellationToken);
        }
        finally
        {
            if (_invalidatesCatalog)
            {
                _executionState.Invalidate();
            }
        }
    }
}
