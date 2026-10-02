namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Defines one test-owned malformed classifier result without exposing provider data.</summary>
internal enum CatalogClassificationResultFault
{
    /// <summary>Preserves the real provider result.</summary>
    None,

    /// <summary>Returns a result belonging to no submitted operation.</summary>
    UnsubmittedOrdinal,

    /// <summary>Omits every result from one dispatched classifier statement.</summary>
    MissingRows,
}

/// <summary>Counts real classifier statements through the supported sequential connection fallback.</summary>
internal sealed class CatalogClassificationCountingConnection : DbConnection
{
    private readonly DbConnection _inner;
    private readonly CatalogClassificationResultFault _fault;
    private bool _innerDisposed;

    /// <summary>Wraps a test-owned provider connection without retaining its SQL or result values.</summary>
    /// <param name="inner">The provider connection disposed together with this wrapper.</param>
    /// <param name="fault">The optional malformed result injected once after real classifier dispatch.</param>
    public CatalogClassificationCountingConnection(
        DbConnection inner,
        CatalogClassificationResultFault fault = CatalogClassificationResultFault.None
    )
    {
        _inner = inner;
        _fault = fault;
    }

    /// <summary>Gets the number of dispatched statements returning the nine classifier facets.</summary>
    public int ClassificationStatementCount { get; private set; }

    /// <summary>Gets whether a real classifier result was replaced by the test-owned fault.</summary>
    public bool FaultWasInjected { get; private set; }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _inner.ConnectionString;
        set => _inner.ConnectionString = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override string Database => _inner.Database;

    /// <inheritdoc />
    public override string DataSource => _inner.DataSource;

    /// <inheritdoc />
    public override string ServerVersion => _inner.ServerVersion;

    /// <inheritdoc />
    public override ConnectionState State => _inner.State;

    /// <inheritdoc />
    public override void ChangeDatabase(
        string databaseName
    ) => _inner.ChangeDatabase(databaseName);

    /// <inheritdoc />
    public override void Open() => _inner.Open();

    /// <inheritdoc />
    public override Task OpenAsync(
        CancellationToken cancellationToken
    ) => _inner.OpenAsync(cancellationToken);

    /// <inheritdoc />
    public override void Close() => _inner.Close();

    /// <inheritdoc />
    public override Task CloseAsync() => _inner.CloseAsync();

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(
        IsolationLevel isolationLevel
    ) => _inner.BeginTransaction(isolationLevel);

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new CountingCommand(this, _inner.CreateCommand());

    /// <inheritdoc />
    protected override void Dispose(
        bool disposing
    )
    {
        if (disposing && !_innerDisposed)
        {
            _innerDisposed = true;
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (!_innerDisposed)
            {
                // WHY: Base asynchronous disposal calls the synchronous
                // override. Mark ownership first so the provider is disposed once.
                _innerDisposed = true;
                await _inner.DisposeAsync();
            }
        }
        finally
        {
            await base.DisposeAsync();
        }
    }

    /// <summary>Forwards a real provider command and observes only its classifier result shape.</summary>
    private sealed class CountingCommand : DbCommand
    {
        private readonly CatalogClassificationCountingConnection _owner;
        private readonly DbCommand _innerCommand;
        private bool _innerDisposed;

        /// <summary>Creates the command adapter owned by one counting connection.</summary>
        public CountingCommand(
            CatalogClassificationCountingConnection owner,
            DbCommand innerCommand
        )
        {
            _owner = owner;
            _innerCommand = innerCommand;
        }

        /// <inheritdoc />
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText
        {
            get => _innerCommand.CommandText;
            set => _innerCommand.CommandText = value ?? string.Empty;
        }

        /// <inheritdoc />
        public override int CommandTimeout
        {
            get => _innerCommand.CommandTimeout;
            set => _innerCommand.CommandTimeout = value;
        }

        /// <inheritdoc />
        public override CommandType CommandType
        {
            get => _innerCommand.CommandType;
            set => _innerCommand.CommandType = value;
        }

        /// <inheritdoc />
        public override bool DesignTimeVisible
        {
            get => _innerCommand.DesignTimeVisible;
            set => _innerCommand.DesignTimeVisible = value;
        }

        /// <inheritdoc />
        public override UpdateRowSource UpdatedRowSource
        {
            get => _innerCommand.UpdatedRowSource;
            set => _innerCommand.UpdatedRowSource = value;
        }

        /// <inheritdoc />
        protected override DbConnection? DbConnection
        {
            get => _owner;
            set => _innerCommand.Connection = value is CatalogClassificationCountingConnection wrapper
                ? wrapper._inner
                : value;
        }

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction
        {
            get => _innerCommand.Transaction;
            set => _innerCommand.Transaction = value;
        }

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => _innerCommand.Parameters;

        /// <inheritdoc />
        protected override DbParameter CreateDbParameter() => _innerCommand.CreateParameter();

        /// <inheritdoc />
        public override void Cancel() => _innerCommand.Cancel();

        /// <inheritdoc />
        public override void Prepare() => _innerCommand.Prepare();

        /// <inheritdoc />
        public override int ExecuteNonQuery() => _innerCommand.ExecuteNonQuery();

        /// <inheritdoc />
        public override Task<int> ExecuteNonQueryAsync(
            CancellationToken cancellationToken
        ) => _innerCommand.ExecuteNonQueryAsync(cancellationToken);

        /// <inheritdoc />
        public override object? ExecuteScalar() => _innerCommand.ExecuteScalar();

        /// <inheritdoc />
        public override Task<object?> ExecuteScalarAsync(
            CancellationToken cancellationToken
        ) => _innerCommand.ExecuteScalarAsync(cancellationToken);

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(
            CommandBehavior behavior
        )
        {
            var reader = _innerCommand.ExecuteReader(behavior);
            CountClassifier(reader);
            if (ShouldInjectFault(reader))
            {
                reader.Dispose();

                return CreateFaultReader();
            }

            return reader;
        }

        /// <inheritdoc />
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
        {
            var reader = await _innerCommand.ExecuteReaderAsync(behavior, cancellationToken);
            CountClassifier(reader);
            if (ShouldInjectFault(reader))
            {
                await reader.DisposeAsync();

                return CreateFaultReader();
            }

            return reader;
        }

        /// <summary>Limits malformed results to one real classifier statement in this test connection.</summary>
        private bool ShouldInjectFault(
            DbDataReader reader
        ) => reader.FieldCount == 9
            && !_owner.FaultWasInjected
            && _owner._fault != CatalogClassificationResultFault.None;

        /// <summary>Creates only synthetic ordinal evidence; real provider rows are never copied.</summary>
        private DataTableReader CreateFaultReader()
        {
            _owner.FaultWasInjected = true;
            var table = new DataTable();
            table.Columns.Add("ordinal", typeof(int));
            for (var column = 1; column < 9; column++)
            {
                table.Columns.Add($"facet_{column}", typeof(object));
            }

            if (_owner._fault == CatalogClassificationResultFault.UnsubmittedOrdinal)
            {
                table.Rows.Add(-1, "matching", true, false, DBNull.Value, DBNull.Value,
                    DBNull.Value, DBNull.Value, DBNull.Value);
            }

            return table.CreateDataReader();
        }

        /// <summary>Identifies classifier traffic without parsing or retaining SQL.</summary>
        private void CountClassifier(
            DbDataReader reader
        )
        {
            // WHY: Prerequisite, probe, and diagnostic statements have other
            // shapes. Only the complete nine-facet classifier is counted.
            if (reader.FieldCount == 9)
            {
                _owner.ClassificationStatementCount++;
            }
        }

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing && !_innerDisposed)
            {
                _innerDisposed = true;
                _innerCommand.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (!_innerDisposed)
                {
                    // WHY: Preserve asynchronous provider cleanup while
                    // allowing the base class to complete its own disposal once.
                    _innerDisposed = true;
                    await _innerCommand.DisposeAsync();
                }
            }
            finally
            {
                await base.DisposeAsync();
            }
        }
    }
}
