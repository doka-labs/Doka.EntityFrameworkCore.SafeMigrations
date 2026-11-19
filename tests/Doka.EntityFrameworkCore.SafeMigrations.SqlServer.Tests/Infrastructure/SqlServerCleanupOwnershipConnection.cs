namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Controls actual provider-shaped cleanup boundaries without timers or a native server.</summary>
internal sealed class SqlServerCleanupOwnershipConnection : DbConnection
{
    private string _connectionString = "Server=localhost;Database=cleanup_ownership;TrustServerCertificate=True";
    private bool _disposeInProgress;
    private ConnectionState _state = ConnectionState.Open;

    /// <summary>Creates a connection with a transaction whose disposal remains independently observable.</summary>
    internal SqlServerCleanupOwnershipConnection()
    {
        Transaction = new CleanupOwnershipTransaction(this);
    }

    /// <summary>Gets or sets the actual boundary retained after the owner arms the selected operation.</summary>
    internal string? HeldBoundary { get; set; }

    /// <summary>Gets the signal proving the chosen boundary has actually started.</summary>
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets the owner-controlled completion signal, never a wallclock deadline.</summary>
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets the injected cleanup SQL failure, when any.</summary>
    internal Exception? CleanupFailure { get; init; }

    /// <summary>Gets the injected bootstrap SQL failure, when any.</summary>
    internal Exception? BootstrapFailure { get; init; }

    /// <summary>Gets the injected analysis-lock SQL failure, when any.</summary>
    internal Exception? AcquisitionFailure { get; init; }

    /// <summary>Gets whether the failed response follows a controlled successful server-side acquisition.</summary>
    internal bool AcquisitionFailureAfterDispatch { get; init; }

    /// <summary>
    /// Gets the controlled native acquisition response, including intentionally unproven scalar shapes.
    /// </summary>
    internal object? AcquisitionResult { get; init; } = 0;

    /// <summary>Gets or sets the protected release failure after the acquisition has been published.</summary>
    internal Exception? ReleaseFailure { get; set; }

    /// <summary>Gets or sets the native release response independently from acquisition.</summary>
    internal object? ReleaseResult { get; set; } = 0;

    /// <summary>Gets or sets the release-command setter selected for ownership-failure injection.</summary>
    internal string? FailingCommandInitializationProperty { get; set; }

    /// <summary>Gets or sets the precise exception thrown by the selected release-command setter.</summary>
    internal Exception? CommandInitializationFailure { get; set; }

    /// <summary>Gets or sets the injected disposal failure, including after acquisition has completed.</summary>
    internal Exception? CommandDisposeFailure { get; set; }

    /// <summary>Gets whether bootstrap disposal rather than cleanup disposal fails.</summary>
    internal bool FailBootstrapDisposal { get; init; }

    /// <summary>Gets the injected physical-close failure, when any.</summary>
    internal Exception? CloseFailure { get; init; }

    /// <summary>Gets or sets the virtual identity getter selected for the handoff probe.</summary>
    internal string? FailingIdentityProperty { get; set; }

    /// <summary>Gets whether identity failure is enabled before lock acquisition rather than after it.</summary>
    internal bool FailIdentityBeforeAcquisition { get; init; }

    /// <summary>Gets the exact failure produced by the controlled identity getter.</summary>
    internal Exception IdentityFailure { get; } = new InvalidOperationException("Identity capture failed.");

    /// <summary>Gets the number of successful native application-lock acquisitions.</summary>
    internal int LockAcquisitionCount { get; private set; }

    /// <summary>Gets the number of dispatched native application-lock release attempts.</summary>
    internal int LockReleaseCount { get; private set; }

    /// <summary>Gets the number of identity reads attempted after successful native acquisition.</summary>
    internal int IdentityReadAfterAcquisitionCount { get; private set; }

    /// <summary>Gets the native transaction supplied to the protected borrowed-lock release.</summary>
    internal DbTransaction? ReleaseTransaction { get; private set; }

    /// <summary>Gets the number of native transactions actually begun by EF.</summary>
    internal int TransactionBeginCount { get; private set; }

    /// <summary>Gets the transaction supplied to the real EF transaction adapter.</summary>
    internal CleanupOwnershipTransaction Transaction { get; }

    /// <summary>Gets the exact token received by the actual cleanup command.</summary>
    internal CancellationToken CleanupToken { get; private set; }

    /// <summary>Gets whether the command's native work has completed.</summary>
    internal bool ExecutionCompleted { get; private set; }

    /// <summary>Gets the completed cleanup command-disposal count.</summary>
    internal int CommandDisposeCount { get; private set; }

    /// <summary>Gets the completed physical-close count.</summary>
    internal int CloseCount { get; private set; }

    /// <summary>Gets the completed physical-disposal count.</summary>
    internal int ConnectionDisposeCount { get; private set; }

    /// <summary>Gets the native command timeout received by cleanup.</summary>
    internal int CleanupCommandTimeout { get; private set; }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString
    {
        get
        {
            CheckIdentityGetter("connection-string");

            return _connectionString;
        }
        set => _connectionString = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override string Database
    {
        get
        {
            CheckIdentityGetter("database");

            return "cleanup_ownership";
        }
    }

    /// <inheritdoc />
    public override string DataSource => "localhost";

    /// <inheritdoc />
    public override string ServerVersion => "16.0";

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override void Open() => _state = ConnectionState.Open;

    /// <inheritdoc />
    public override Task OpenAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Close()
    {
        if (!_disposeInProgress)
        {
            CloseCount++;
            if (CloseFailure is { } failure)
            {
                throw failure;
            }
        }

        _state = ConnectionState.Closed;
    }

    /// <inheritdoc />
    public override async Task CloseAsync()
    {
        if (HeldBoundary == "close")
        {
            Entered.TrySetResult();
            await Release.Task;
        }

        CloseCount++;
        if (CloseFailure is { } failure)
        {
            throw failure;
        }

        _state = ConnectionState.Closed;
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        try
        {
            if (HeldBoundary == "connection-dispose")
            {
                Entered.TrySetResult();
                await Release.Task;
            }

            ConnectionDisposeCount++;
            _state = ConnectionState.Closed;
        }
        finally
        {
            _disposeInProgress = true;
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                _disposeInProgress = false;
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(
        bool disposing
    )
    {
        var inheritedDisposal = _disposeInProgress;
        _disposeInProgress = true;
        try
        {
            if (disposing && !inheritedDisposal)
            {
                ConnectionDisposeCount++;
                _state = ConnectionState.Closed;
            }
        }
        finally
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                _disposeInProgress = inheritedDisposal;
            }
        }
    }

    /// <inheritdoc />
    public override void ChangeDatabase(
        string databaseName
    ) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(
        IsolationLevel isolationLevel
    )
    {
        TransactionBeginCount++;

        return Transaction;
    }

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new CleanupOwnershipCommand(this);

    /// <summary>Distinguishes pre-acquisition identity capture from fallible post-acquisition handoff work.</summary>
    private void CheckIdentityGetter(
        string property
    )
    {
        if (LockAcquisitionCount > 0)
        {
            IdentityReadAfterAcquisitionCount++;
        }

        if (FailingIdentityProperty == property
            && (FailIdentityBeforeAcquisition || LockAcquisitionCount > 0))
        {
            throw IdentityFailure;
        }
    }

    /// <summary>Separates bootstrap, cleanup execution and disposal so tests observe real ownership ordering.</summary>
    private sealed class CleanupOwnershipCommand : DbCommand
    {
        private readonly SqlServerCleanupOwnershipConnection _connection;
        private string _commandText = string.Empty;
        private int _commandTimeout;
        private bool _disposeAsyncInProgress;
        private DbTransaction? _transaction;

        /// <summary>Creates a command borrowing the connection's controlled completion boundaries.</summary>
        /// <param name="connection">The owner retaining all boundary signals and injected failures.</param>
        internal CleanupOwnershipCommand(
            SqlServerCleanupOwnershipConnection connection
        )
        {
            _connection = connection;
            DbConnection = connection;
        }

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText
        {
            get => _commandText;
            set
            {
                CheckInitialization("text");
                _commandText = value ?? string.Empty;
            }
        }

        /// <inheritdoc />
        public override int CommandTimeout
        {
            get => _commandTimeout;
            set
            {
                CheckInitialization("timeout");
                _commandTimeout = value;
            }
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
        protected override DbTransaction? DbTransaction
        {
            get => _transaction;
            set
            {
                CheckInitialization("transaction");
                _transaction = value;
            }
        }

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        /// <summary>Identifies bootstrap SQL without treating recovery as a second bootstrap.</summary>
        private bool IsBootstrap => CommandText.StartsWith("IF SESSION_CONTEXT", StringComparison.Ordinal);

        /// <summary>Fails only a setter explicitly armed after successful acquisition by the owning test.</summary>
        private void CheckInitialization(
            string property
        )
        {
            if (_connection.FailingCommandInitializationProperty == property
                && _connection.CommandInitializationFailure is { } failure)
            {
                throw failure;
            }
        }

        /// <inheritdoc />
        public override void Cancel() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Prepare() => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        /// <inheritdoc />
        public override int ExecuteNonQuery()
        {
            if (IsBootstrap)
            {
                if (_connection.BootstrapFailure is { } bootstrapFailure)
                {
                    throw bootstrapFailure;
                }

                return 0;
            }

            // WHY: Synchronous probes exercise native synchronous calls, never block an asynchronous test signal.
            _connection.CleanupToken = CancellationToken.None;
            _connection.CleanupCommandTimeout = CommandTimeout;
            _connection.ExecutionCompleted = true;
            if (_connection.CleanupFailure is { } failure)
            {
                throw failure;
            }

            return 0;
        }

        /// <inheritdoc />
        public override async Task<int> ExecuteNonQueryAsync(
            CancellationToken cancellationToken
        )
        {
            if (IsBootstrap)
            {
                if (_connection.BootstrapFailure is { } bootstrapFailure)
                {
                    throw bootstrapFailure;
                }

                return 0;
            }

            _connection.CleanupToken = cancellationToken;
            _connection.CleanupCommandTimeout = CommandTimeout;
            if (_connection.HeldBoundary == "execute")
            {
                _connection.Entered.TrySetResult();
                await _connection.Release.Task;
            }

            _connection.ExecutionCompleted = true;
            if (_connection.CleanupFailure is { } failure)
            {
                throw failure;
            }

            return 0;
        }

        /// <inheritdoc />
        public override object? ExecuteScalar()
        {
            if (CommandText.Contains("sp_releaseapplock", StringComparison.Ordinal))
            {
                ObserveLockCommand(CancellationToken.None);
                if (_connection.ReleaseFailure is { } releaseFailure)
                {
                    throw releaseFailure;
                }

                return _connection.ReleaseResult;
            }

            if (_connection.AcquisitionFailure is { } failure)
            {
                if (_connection.AcquisitionFailureAfterDispatch)
                {
                    ObserveLockCommand(CancellationToken.None);
                }

                throw failure;
            }

            ObserveLockCommand(CancellationToken.None);

            return CommandText.Contains("sp_getapplock", StringComparison.Ordinal)
                ? _connection.AcquisitionResult
                : 0;
        }

        /// <inheritdoc />
        public override async Task<object?> ExecuteScalarAsync(
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (CommandText.Contains("sp_releaseapplock", StringComparison.Ordinal))
            {
                ObserveLockCommand(cancellationToken);
                if (_connection.HeldBoundary == "lock-release")
                {
                    _connection.Entered.TrySetResult();
                    await _connection.Release.Task;
                }

                if (_connection.ReleaseFailure is { } releaseFailure)
                {
                    throw releaseFailure;
                }

                return _connection.ReleaseResult;
            }

            if (_connection.AcquisitionFailure is { } failure)
            {
                if (_connection.AcquisitionFailureAfterDispatch)
                {
                    ObserveLockCommand(cancellationToken);
                }

                throw failure;
            }

            ObserveLockCommand(cancellationToken);

            return CommandText.Contains("sp_getapplock", StringComparison.Ordinal)
                ? _connection.AcquisitionResult
                : 0;
        }

        /// <summary>
        /// Records actual acquisition and protected release without interpreting unrelated cleanup SQL.
        /// </summary>
        private void ObserveLockCommand(
            CancellationToken cancellationToken
        )
        {
            if (CommandText.Contains("sp_getapplock", StringComparison.Ordinal)
                && _connection.AcquisitionResult is not int or >= 0)
            {
                _connection.LockAcquisitionCount++;
            }
            else if (CommandText.Contains("sp_releaseapplock", StringComparison.Ordinal))
            {
                _connection.LockReleaseCount++;
                _connection.ReleaseTransaction = DbTransaction;
                _connection.CleanupToken = cancellationToken;
                _connection.CleanupCommandTimeout = CommandTimeout;
            }
        }

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(
            CommandBehavior behavior
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (IsBootstrap)
                {
                    if (_connection.FailBootstrapDisposal
                        && _connection.CommandDisposeFailure is { } bootstrapFailure)
                    {
                        throw bootstrapFailure;
                    }

                    return;
                }

                if (_connection.HeldBoundary == "command-dispose")
                {
                    _connection.Entered.TrySetResult();
                    await _connection.Release.Task;
                }

                _connection.CommandDisposeCount++;
                if (!_connection.FailBootstrapDisposal
                    && _connection.CommandDisposeFailure is { } failure)
                {
                    throw failure;
                }
            }
            finally
            {
                _disposeAsyncInProgress = true;
                try
                {
                    await base.DisposeAsync();
                }
                finally
                {
                    _disposeAsyncInProgress = false;
                }
            }
        }

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
        {
            try
            {
                if (disposing && !_disposeAsyncInProgress)
                {
                    if (IsBootstrap)
                    {
                        if (_connection.FailBootstrapDisposal
                            && _connection.CommandDisposeFailure is { } bootstrapFailure)
                        {
                            throw bootstrapFailure;
                        }
                    }
                    else
                    {
                        _connection.CommandDisposeCount++;
                        if (!_connection.FailBootstrapDisposal
                            && _connection.CommandDisposeFailure is { } failure)
                        {
                            throw failure;
                        }
                    }
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>Supplies an actual ADO.NET transaction boundary to EF's ownership adapter.</summary>
    internal sealed class CleanupOwnershipTransaction : DbTransaction
    {
        private readonly SqlServerCleanupOwnershipConnection _connection;
        private bool _disposeAsyncInProgress;

        /// <summary>Creates the provider-shaped transaction borrowed by the real EF adapter.</summary>
        /// <param name="connection">The owning controlled connection.</param>
        internal CleanupOwnershipTransaction(
            SqlServerCleanupOwnershipConnection connection
        )
        {
            _connection = connection;
        }

        /// <summary>Gets or sets the exact injected disposal error.</summary>
        internal Exception? DisposeFailure { get; set; }

        /// <summary>Gets the completed transaction-disposal count.</summary>
        internal int DisposeCount { get; private set; }

        /// <summary>Gets rollback attempts so borrowed ownership can be proven unchanged.</summary>
        internal int RollbackCount { get; private set; }

        /// <inheritdoc />
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        /// <inheritdoc />
        protected override DbConnection DbConnection => _connection;

        /// <inheritdoc />
        public override void Commit() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Rollback()
        {
            RollbackCount++;

            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (_connection.HeldBoundary == "transaction-dispose")
                {
                    _connection.Entered.TrySetResult();
                    await _connection.Release.Task;
                }

                DisposeCount++;
                if (DisposeFailure is { } failure)
                {
                    throw failure;
                }
            }
            finally
            {
                _disposeAsyncInProgress = true;
                try
                {
                    await base.DisposeAsync();
                }
                finally
                {
                    _disposeAsyncInProgress = false;
                }
            }
        }

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
        {
            try
            {
                if (disposing && !_disposeAsyncInProgress)
                {
                    DisposeCount++;
                    if (DisposeFailure is { } failure)
                    {
                        throw failure;
                    }
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
