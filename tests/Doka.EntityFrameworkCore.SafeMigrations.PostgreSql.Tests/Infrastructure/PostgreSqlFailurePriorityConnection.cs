namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Supplies independently controlled native failures and disposal for provider-entry tests.</summary>
internal sealed class PostgreSqlFailurePriorityConnection : DbConnection
{
    private ConnectionState _state;

    /// <summary>Gets an optional environment failure.</summary>
    internal Exception? EnvironmentFailure { get; init; }

    /// <summary>Gets an optional acquisition or catalog failure.</summary>
    internal Exception? ExecutionFailure { get; init; }

    /// <summary>Gets an optional connection-close failure.</summary>
    internal Exception? CloseFailure { get; init; }

    /// <summary>Gets an optional acquisition-command disposal failure.</summary>
    internal Exception? CommandDisposeFailure { get; init; }

    /// <summary>Gets the command whose disposal is controlled independently from other commands.</summary>
    internal string DisposeCommandText { get; init; } = PostgreSqlSafeMigrationProviderAnalyzer.AnalysisAdvisoryLockSql;

    /// <summary>Gets an optional first transaction-disposal failure.</summary>
    internal Exception? TransactionDisposeFailure { get; init; }

    /// <summary>Gets a test-owned close completion signal instead of a wallclock wait.</summary>
    internal Task CloseCompletion { get; init; } = Task.CompletedTask;

    /// <summary>Gets a test-owned command-disposal completion signal.</summary>
    internal Task CommandDisposeCompletion { get; init; } = Task.CompletedTask;

    /// <summary>Gets the observable native close-admission signal.</summary>
    internal TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets the observable native command-disposal admission signal.</summary>
    internal TaskCompletionSource CommandDisposeStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets the actual asynchronous close count.</summary>
    internal int AsyncCloseCount { get; private set; }

    /// <summary>Gets the actual controlled-command disposal count.</summary>
    internal int CommandDisposeCount { get; private set; }

    /// <summary>Gets actual asynchronous transaction disposal attempts.</summary>
    internal int AsyncTransactionDisposeCount { get; private set; }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString { get; set; } = "Host=localhost;Database=owned;Username=unused";

    /// <inheritdoc />
    public override string Database => "owned";

    /// <inheritdoc />
    public override string DataSource => "synthetic";

    /// <inheritdoc />
    public override string ServerVersion => EnvironmentFailure is { } failure
        ? throw failure
        : "18.6";

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override void Open() => _state = ConnectionState.Open;

    /// <inheritdoc />
    public override void Close() => _state = ConnectionState.Closed;

    /// <inheritdoc />
    public override async Task CloseAsync()
    {
        AsyncCloseCount++;
        CloseStarted.TrySetResult();
        await CloseCompletion;
        if (CloseFailure is { } failure)
        {
            throw failure;
        }

        Close();
    }

    /// <inheritdoc />
    public override void ChangeDatabase(
        string databaseName
    ) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(
        IsolationLevel isolationLevel
    ) => new FailureTransaction(this, isolationLevel);

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new FailureCommand(this);

    private sealed class FailureTransaction : DbTransaction
    {
        private readonly PostgreSqlFailurePriorityConnection _connection;

        /// <summary>Retains the synthetic connection without transferring caller ownership.</summary>
        /// <param name="connection">The connection whose transaction-disposal boundary is observed.</param>
        /// <param name="isolationLevel">The native isolation level exposed to provider validation.</param>
        internal FailureTransaction(
            PostgreSqlFailurePriorityConnection connection,
            IsolationLevel isolationLevel
        )
        {
            _connection = connection;
            IsolationLevel = isolationLevel;
        }

        /// <inheritdoc />
        public override IsolationLevel IsolationLevel { get; }

        /// <inheritdoc />
        protected override DbConnection DbConnection => _connection;

        /// <inheritdoc />
        public override void Commit() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Rollback() => throw new NotSupportedException();

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            _connection.AsyncTransactionDisposeCount++;
            await base.DisposeAsync();

            // WHY: The first native disposal is the injected failure boundary; context teardown
            // can safely retry its own remaining wrapper without hiding the test's primary result.
            if (_connection is { AsyncTransactionDisposeCount: 1, TransactionDisposeFailure: { } failure })
            {
                throw failure;
            }
        }
    }

    private sealed class FailureCommand : DbCommand
    {
        private readonly PostgreSqlFailurePriorityConnection _owner;
        private readonly NpgsqlCommand _parameters = new();

        /// <summary>Creates an execution and disposal boundary over the owning synthetic connection.</summary>
        /// <param name="owner">The connection supplying independent work and cleanup controls.</param>
        internal FailureCommand(
            PostgreSqlFailurePriorityConnection owner
        )
        {
            _owner = owner;
            DbConnection = owner;
        }

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        /// <inheritdoc />
        public override int CommandTimeout { get; set; }

        /// <inheritdoc />
        public override CommandType CommandType { get; set; }

        /// <inheritdoc />
        public override bool DesignTimeVisible { get; set; }

        /// <inheritdoc />
        public override UpdateRowSource UpdatedRowSource { get; set; }

        /// <inheritdoc />
        protected override DbConnection? DbConnection { get; set; }

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction { get; set; }

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;

        /// <inheritdoc />
        public override void Cancel() { }

        /// <inheritdoc />
        public override int ExecuteNonQuery() => 0;

        /// <inheritdoc />
        public override object? ExecuteScalar() => _owner.ExecutionFailure is { } failure
            ? throw failure
            : 1;

        /// <inheritdoc />
        public override void Prepare() { }

        /// <inheritdoc />
        protected override DbParameter CreateDbParameter() => new NpgsqlParameter();

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(
            CommandBehavior behavior
        ) => _owner.ExecutionFailure is { } failure
            ? throw failure
            : throw new NotSupportedException();

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (CommandText == _owner.DisposeCommandText)
                {
                    _owner.CommandDisposeCount++;
                    _owner.CommandDisposeStarted.TrySetResult();
                    await _owner.CommandDisposeCompletion;
                    if (_owner.CommandDisposeFailure is { } failure)
                    {
                        throw failure;
                    }
                }
            }
            finally
            {
                _parameters.Dispose();
                await base.DisposeAsync();
            }
        }
    }
}
