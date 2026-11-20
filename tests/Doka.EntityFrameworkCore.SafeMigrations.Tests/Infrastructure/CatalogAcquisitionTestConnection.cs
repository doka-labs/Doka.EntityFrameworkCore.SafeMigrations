namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Fails catalog configuration before the acquired handle is registered with its owner.</summary>
internal sealed class CatalogAcquisitionTestConnection : DbConnection
{
    /// <summary>Creates one independent transport configuration probe.</summary>
    /// <param name="native">Whether the connection exposes native batching.</param>
    public CatalogAcquisitionTestConnection(
        bool native
    )
    {
        CanCreateBatch = native;
    }

    /// <summary>Gets whether this probe exposes a native batch.</summary>
    public override bool CanCreateBatch { get; }

    /// <summary>Gets the exact provider configuration failure.</summary>
    public InvalidOperationException ConfigurationFailure { get; } = new("configuration failed");

    /// <summary>Gets the exact secondary provider disposal failure.</summary>
    public InvalidOperationException DisposalFailure { get; } = new("disposal failed");

    /// <summary>Gets the number of acquired-handle disposal attempts.</summary>
    public int HandleDisposals { get; private set; }

    /// <summary>Gets or sets the virtual setter that rejects configuration, or null for success.</summary>
    public string? FailingSetter { get; set; }

    /// <summary>Gets or initializes whether disposal also fails.</summary>
    public bool FailDisposal { get; init; }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string Database => "acquisition";

    /// <inheritdoc />
    public override string DataSource => "acquisition";

    /// <inheritdoc />
    public override string ServerVersion => "1";

    /// <inheritdoc />
    public override ConnectionState State => ConnectionState.Open;

    /// <inheritdoc />
    public override void Open() { }

    /// <inheritdoc />
    public override void Close() { }

    /// <inheritdoc />
    public override void ChangeDatabase(
        string databaseName
    ) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(
        IsolationLevel isolationLevel
    ) => new AcquisitionTransaction(this);

    /// <inheritdoc />
    protected override DbBatch CreateDbBatch() => new AcquisitionBatch(this);

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new AcquisitionCommand(this);

    /// <summary>Rejects the selected configuration boundary before ownership can be published.</summary>
    /// <param name="setter">The provider setter currently invoked.</param>
    private void Configure(
        string setter
    )
    {
        if (FailingSetter == setter)
        {
            throw ConfigurationFailure;
        }
    }

    /// <summary>Counts every disposal attempt, including attempts whose provider cleanup fails.</summary>
    private void DisposeHandle()
    {
        HandleDisposals++;
        if (FailDisposal)
        {
            throw DisposalFailure;
        }
    }

    /// <summary>Provides a borrowed transaction without allocating provider state.</summary>
    private sealed class AcquisitionTransaction : DbTransaction
    {
        private readonly CatalogAcquisitionTestConnection _connection;

        /// <summary>Initializes the transaction's borrowed connection identity.</summary>
        /// <param name="connection">The borrowed connection whose transaction is forwarded.</param>
        public AcquisitionTransaction(
            CatalogAcquisitionTestConnection connection
        )
        {
            _connection = connection;
        }

        /// <inheritdoc />
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        /// <inheritdoc />
        protected override DbConnection DbConnection => _connection;

        /// <inheritdoc />
        public override void Commit() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Rollback() => throw new NotSupportedException();
    }

    /// <summary>Exposes only acquisition, virtual configuration, and cleanup of a native batch.</summary>
    private sealed class AcquisitionBatch : DbBatch
    {
        private readonly CatalogAcquisitionTestConnection _connection;

        /// <summary>Initializes the batch's independent observation source.</summary>
        /// <param name="connection">The probe receiving configuration and disposal observations.</param>
        public AcquisitionBatch(
            CatalogAcquisitionTestConnection connection
        )
        {
            _connection = connection;
            DbConnection = connection;
        }

        /// <inheritdoc />
        public override int Timeout
        {
            get => 30;
            set => _connection.Configure("timeout");
        }

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction
        {
            get => null;
            set => _connection.Configure("transaction");
        }

        /// <inheritdoc />
        protected override DbConnection? DbConnection { get; set; }

        /// <inheritdoc />
        protected override DbBatchCommandCollection DbBatchCommands => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbBatchCommand CreateDbBatchCommand() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Cancel() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Prepare() => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task PrepareAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task<int> ExecuteNonQueryAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new NotSupportedException();

        /// <inheritdoc />
        public override Task<object?> ExecuteScalarAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(
            CommandBehavior behavior
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Dispose()
        {
            try
            {
                _connection.DisposeHandle();
            }
            finally
            {
                base.Dispose();
            }
        }
    }

    /// <summary>Exposes only acquisition, virtual configuration, and cleanup of a sequential command.</summary>
    private sealed class AcquisitionCommand : DbCommand
    {
        private readonly CatalogAcquisitionTestConnection _connection;

        /// <summary>Initializes the command's independent observation source.</summary>
        /// <param name="connection">The probe receiving configuration and disposal observations.</param>
        public AcquisitionCommand(
            CatalogAcquisitionTestConnection connection
        )
        {
            _connection = connection;
            DbConnection = connection;
        }

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        /// <inheritdoc />
        public override int CommandTimeout
        {
            get => 30;
            set => _connection.Configure("timeout");
        }

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction
        {
            get => null;
            set => _connection.Configure("transaction");
        }

        /// <inheritdoc />
        public override CommandType CommandType { get; set; }

        /// <inheritdoc />
        public override bool DesignTimeVisible { get; set; }

        /// <inheritdoc />
        public override UpdateRowSource UpdatedRowSource { get; set; }

        /// <inheritdoc />
        protected override DbConnection? DbConnection { get; set; }

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Cancel() => throw new NotSupportedException();

        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new NotSupportedException();

        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Prepare() => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(
            CommandBehavior behavior
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
        {
            try
            {
                if (disposing)
                {
                    _connection.DisposeHandle();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
