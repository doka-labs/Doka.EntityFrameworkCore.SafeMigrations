namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Supplies deterministic independent-probe result sets without provider dependencies.</summary>
internal sealed class CatalogProbeTestConnection : DbConnection
{
    private bool _disposed;

    /// <summary>Creates a native or sequential test transport.</summary>
    public CatalogProbeTestConnection(
        bool native
    )
    {
        CanCreateBatch = native;
    }

    /// <summary>Gets the executed native batches.</summary>
    public int BatchExecutions { get; private set; }

    /// <summary>Gets the executed sequential statements.</summary>
    public int CommandExecutions { get; private set; }

    /// <summary>Gets the executed statement count across both transport modes.</summary>
    public int StatementCount { get; private set; }

    /// <summary>Gets disposed native batches or sequential commands.</summary>
    public int Disposals { get; private set; }

    /// <summary>Gets disposed result readers across both transport modes.</summary>
    public int ReaderDisposals { get; private set; }

    /// <summary>Gets caller connection disposals, which probes must never initiate.</summary>
    public int ConnectionDisposals { get; private set; }

    /// <summary>Gets caller transaction disposals, which probes must never initiate.</summary>
    public int TransactionDisposals { get; private set; }

    /// <summary>Gets each native batch's statement count.</summary>
    public List<int> BatchCounts { get; } = [];

    /// <summary>Gets the execution timeouts.</summary>
    public List<int> Timeouts { get; } = [];

    /// <summary>Gets the caller transactions forwarded to execution.</summary>
    public List<DbTransaction?> Transactions { get; } = [];

    /// <summary>Gets or sets one synthetic response corruption.</summary>
    public string? Fault { get; init; }

    /// <summary>Gets the controlled completion of each sequential command's actual disposal.</summary>
    public List<Task> CommandDisposeCompletions { get; } = [];

    /// <summary>Gets or initializes the native batch's actual disposal operation.</summary>
    public Task BatchDisposeCompletion { get; init; } = Task.CompletedTask;

    /// <summary>Gets or initializes the reader's actual disposal operation.</summary>
    public Task ReaderDisposeCompletion { get; init; } = Task.CompletedTask;

    /// <summary>Gets or initializes the signal published when actual reader disposal begins.</summary>
    public TaskCompletionSource? ReaderDisposeStarted { get; init; }

    /// <summary>Gets or initializes the signal published when a sequential command's disposal begins.</summary>
    public TaskCompletionSource? CommandDisposeStarted { get; init; }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string Database => "probe-test";

    /// <inheritdoc />
    public override string DataSource => "probe-test";

    /// <inheritdoc />
    public override string ServerVersion => "1";

    /// <inheritdoc />
    public override ConnectionState State => ConnectionState.Open;

    /// <inheritdoc />
    public override bool CanCreateBatch { get; }

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
    ) => new ProbeTransaction(this, isolationLevel);

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new ProbeCommand(this);

    /// <inheritdoc />
    protected override DbBatch CreateDbBatch() => new ProbeBatch(this);

    /// <inheritdoc />
    protected override void Dispose(
        bool disposing
    )
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            ConnectionDisposals++;
        }

        base.Dispose(disposing);
    }

    private DataSet Execute(
        string[] statements,
        int timeout,
        DbTransaction? transaction,
        bool native,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatementCount += statements.Length;
        Timeouts.Add(timeout);
        Transactions.Add(transaction);
        if (native)
        {
            BatchExecutions++;
            BatchCounts.Add(statements.Length);
        }
        else
        {
            CommandExecutions++;
        }

        var results = new DataSet();
        foreach (var statement in statements)
        {
            var table = new DataTable();
            table.Columns.Add("ordinal", typeof(int));
            var values = statement
                .Split(',')
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                .ToArray();

            if (Fault == "reversed")
            {
                Array.Reverse(values);
            }

            if (Fault != "empty-set")
            {
                foreach (var value in values)
                {
                    table.Rows.Add(value);
                }
            }

            results.Tables.Add(table);
        }

        if (Fault == "missing-set")
        {
            results.Tables.RemoveAt(results.Tables.Count - 1);
        }
        else if (Fault == "extra-set")
        {
            results.Tables.Add(new DataTable());
        }

        return results;
    }

    private sealed class ProbeTransaction : DbTransaction
    {
        private readonly CatalogProbeTestConnection _connection;
        private readonly IsolationLevel _isolationLevel;
        private bool _disposed;

        /// <summary>Initializes the transaction probe with its owner and authored isolation level.</summary>
        /// <param name="connection">The owning probe connection.</param>
        /// <param name="isolationLevel">The authored transaction isolation level.</param>
        public ProbeTransaction(
            CatalogProbeTestConnection connection,
            IsolationLevel isolationLevel
        )
        {
            _connection = connection;
            _isolationLevel = isolationLevel;
        }

        /// <inheritdoc />
        public override IsolationLevel IsolationLevel => _isolationLevel;

        /// <inheritdoc />
        protected override DbConnection DbConnection => _connection;

        /// <inheritdoc />
        public override void Commit() { }

        /// <inheritdoc />
        public override void Rollback() { }

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _connection.TransactionDisposals++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ProbeCommand : DbCommand
    {
        private readonly CatalogProbeTestConnection _connection;
        private readonly ProbeParameters _parameters;
        private bool _disposed;
        private DataSet? _results;

        /// <summary>Initializes the command probe while retaining its borrowed connection identity.</summary>
        /// <param name="connection">The owning probe connection.</param>
        public ProbeCommand(
            CatalogProbeTestConnection connection
        )
        {
            _connection = connection;
            _parameters = new ProbeParameters();
            CommandText = string.Empty;
            DbConnection = connection;
        }

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText { get; set; }

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
        protected override DbParameterCollection DbParameterCollection => _parameters;

        /// <inheritdoc />
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Cancel() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Prepare() => throw new NotSupportedException();

        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new NotSupportedException();

        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(
            CommandBehavior behavior
        ) => Read(CancellationToken.None);

        /// <inheritdoc />
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        ) => Task.FromResult<DbDataReader>(Read(cancellationToken));

        private ProbeReader Read(
            CancellationToken cancellationToken
        )
        {
            _results = _connection.Execute(
                [CommandText],
                CommandTimeout,
                DbTransaction,
                native: false,
                cancellationToken);

            return new ProbeReader(_connection, _results.CreateDataReader());
        }

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _results?.Dispose();
                _connection.Disposals++;
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _connection.CommandDisposeStarted?.TrySetResult();
            var completion = _connection.Disposals < _connection.CommandDisposeCompletions.Count
                ? _connection.CommandDisposeCompletions[_connection.Disposals]
                : Task.CompletedTask;

            try
            {
                await completion;
            }
            finally
            {
                // WHY: The fake releases its real DataSet even when its controlled
                // native-disposal result fails, keeping the test's resources owned.
                await base.DisposeAsync();
            }
        }
    }

    private sealed class ProbeBatch : DbBatch
    {
        private readonly ProbeBatchCommands _commands;
        private readonly CatalogProbeTestConnection _connection;
        private bool _disposed;
        private DataSet? _results;

        /// <summary>Initializes the batch probe while retaining its borrowed connection identity.</summary>
        /// <param name="connection">The owning probe connection.</param>
        public ProbeBatch(
            CatalogProbeTestConnection connection
        )
        {
            _connection = connection;
            _commands = new ProbeBatchCommands();
            DbConnection = connection;
        }

        /// <inheritdoc />
        public override int Timeout { get; set; }

        /// <inheritdoc />
        protected override DbConnection? DbConnection { get; set; }

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction { get; set; }

        /// <inheritdoc />
        protected override DbBatchCommandCollection DbBatchCommands => _commands;

        /// <inheritdoc />
        protected override DbBatchCommand CreateDbBatchCommand() => new ProbeBatchCommand();

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
        ) => Read(CancellationToken.None);

        /// <inheritdoc />
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        ) => Task.FromResult<DbDataReader>(Read(cancellationToken));

        private ProbeReader Read(
            CancellationToken cancellationToken
        )
        {
            _results = _connection.Execute(
                _commands
                    .Select(command => command.CommandText)
                    .ToArray(),
                Timeout,
                DbTransaction,
                native: true,
                cancellationToken);

            return new ProbeReader(_connection, _results.CreateDataReader());
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _results?.Dispose();
                _connection.Disposals++;
            }

            base.Dispose();
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                await _connection.BatchDisposeCompletion;
            }
            finally
            {
                await base.DisposeAsync();
            }
        }
    }

    private sealed class ProbeReader : DbDataReader
    {
        private readonly CatalogProbeTestConnection _connection;
        private readonly DataTableReader _inner;
        private bool _disposed;

        /// <summary>Initializes the reader probe with its actual reader and controlled owner callbacks.</summary>
        /// <param name="connection">The owning probe connection.</param>
        /// <param name="inner">The actual table reader retained through controlled disposal.</param>
        public ProbeReader(
            CatalogProbeTestConnection connection,
            DataTableReader inner
        )
        {
            _connection = connection;
            _inner = inner;
        }

        /// <inheritdoc />
        public override object this[
            int ordinal
        ] => _inner[ordinal];

        /// <inheritdoc />
        public override object this[
            string name
        ] => _inner[name];

        /// <inheritdoc />
        public override int Depth => _inner.Depth;

        /// <inheritdoc />
        public override int FieldCount => _inner.FieldCount;

        /// <inheritdoc />
        public override bool HasRows => _inner.HasRows;

        /// <inheritdoc />
        public override bool IsClosed => _inner.IsClosed;

        /// <inheritdoc />
        public override int RecordsAffected => _inner.RecordsAffected;

        /// <inheritdoc />
        public override bool GetBoolean(
            int ordinal
        ) => _inner.GetBoolean(ordinal);

        /// <inheritdoc />
        public override byte GetByte(
            int ordinal
        ) => _inner.GetByte(ordinal);

        /// <inheritdoc />
        public override long GetBytes(
            int ordinal,
            long dataOffset,
            byte[]? buffer,
            int bufferOffset,
            int length
        ) => _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

        /// <inheritdoc />
        public override char GetChar(
            int ordinal
        ) => _inner.GetChar(ordinal);

        /// <inheritdoc />
        public override long GetChars(
            int ordinal,
            long dataOffset,
            char[]? buffer,
            int bufferOffset,
            int length
        ) => _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

        /// <inheritdoc />
        public override string GetDataTypeName(
            int ordinal
        ) => _inner.GetDataTypeName(ordinal);

        /// <inheritdoc />
        public override DateTime GetDateTime(
            int ordinal
        ) => _inner.GetDateTime(ordinal);

        /// <inheritdoc />
        public override decimal GetDecimal(
            int ordinal
        ) => _inner.GetDecimal(ordinal);

        /// <inheritdoc />
        public override double GetDouble(
            int ordinal
        ) => _inner.GetDouble(ordinal);

        /// <inheritdoc />
        public override Type GetFieldType(
            int ordinal
        ) => _inner.GetFieldType(ordinal);

        /// <inheritdoc />
        public override float GetFloat(
            int ordinal
        ) => _inner.GetFloat(ordinal);

        /// <inheritdoc />
        public override Guid GetGuid(
            int ordinal
        ) => _inner.GetGuid(ordinal);

        /// <inheritdoc />
        public override short GetInt16(
            int ordinal
        ) => _inner.GetInt16(ordinal);

        /// <inheritdoc />
        public override int GetInt32(
            int ordinal
        ) => _inner.GetInt32(ordinal);

        /// <inheritdoc />
        public override long GetInt64(
            int ordinal
        ) => _inner.GetInt64(ordinal);

        /// <inheritdoc />
        public override string GetName(
            int ordinal
        ) => _inner.GetName(ordinal);

        /// <inheritdoc />
        public override int GetOrdinal(
            string name
        ) => _inner.GetOrdinal(name);

        /// <inheritdoc />
        public override string GetString(
            int ordinal
        ) => _inner.GetString(ordinal);

        /// <inheritdoc />
        public override object GetValue(
            int ordinal
        ) => _inner.GetValue(ordinal);

        /// <inheritdoc />
        public override int GetValues(
            object[] values
        ) => _inner.GetValues(values);

        /// <inheritdoc />
        public override bool IsDBNull(
            int ordinal
        ) => _inner.IsDBNull(ordinal);

        /// <inheritdoc />
        public override IEnumerator GetEnumerator() => _inner.GetEnumerator();

        /// <inheritdoc />
        public override bool Read() => _inner.Read();

        /// <inheritdoc />
        public override bool NextResult() => _inner.NextResult();

        /// <inheritdoc />
        public override Task<bool> ReadAsync(
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return _inner.ReadAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override Task<bool> NextResultAsync(
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return _inner.NextResultAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override void Close() => _inner.Close();

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _inner.Dispose();
                _connection.ReaderDisposals++;
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            _connection.ReaderDisposeStarted?.TrySetResult();

            try
            {
                await _connection.ReaderDisposeCompletion;
            }
            finally
            {
                await base.DisposeAsync();
            }
        }
    }

    private sealed class ProbeBatchCommand : DbBatchCommand
    {
        private readonly ProbeParameters _parameters = new();

        /// <inheritdoc />
        public override string CommandText { get; set; } = string.Empty;

        /// <inheritdoc />
        public override CommandType CommandType { get; set; }

        /// <inheritdoc />
        public override int RecordsAffected => 0;

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => _parameters;

        /// <inheritdoc />
        public override bool CanCreateParameter => false;

        /// <inheritdoc />
        public override DbParameter CreateParameter() => throw new NotSupportedException();
    }

    private sealed class ProbeBatchCommands : DbBatchCommandCollection
    {
        private readonly List<DbBatchCommand> _items = [];

        /// <inheritdoc />
        public override int Count => _items.Count;

        /// <inheritdoc />
        public override bool IsReadOnly => false;

        /// <inheritdoc />
        public override void Add(
            DbBatchCommand item
        ) => _items.Add(item);

        /// <inheritdoc />
        public override void Clear() => _items.Clear();

        /// <inheritdoc />
        public override bool Contains(
            DbBatchCommand item
        ) => _items.Contains(item);

        /// <inheritdoc />
        public override void CopyTo(
            DbBatchCommand[] array,
            int arrayIndex
        ) => _items.CopyTo(array, arrayIndex);

        /// <inheritdoc />
        public override IEnumerator<DbBatchCommand> GetEnumerator() => _items.GetEnumerator();

        /// <inheritdoc />
        public override int IndexOf(
            DbBatchCommand item
        ) => _items.IndexOf(item);

        /// <inheritdoc />
        public override void Insert(
            int index,
            DbBatchCommand item
        ) => _items.Insert(index, item);

        /// <inheritdoc />
        public override bool Remove(
            DbBatchCommand item
        ) => _items.Remove(item);

        /// <inheritdoc />
        public override void RemoveAt(
            int index
        ) => _items.RemoveAt(index);

        /// <inheritdoc />
        protected override DbBatchCommand GetBatchCommand(
            int index
        ) => _items[index];

        /// <inheritdoc />
        protected override void SetBatchCommand(
            int index,
            DbBatchCommand batchCommand
        ) => _items[index] = batchCommand;
    }

    private sealed class ProbeParameters : DbParameterCollection
    {
        private readonly List<object> _items = [];

        /// <inheritdoc />
        public override int Count => _items.Count;

        /// <inheritdoc />
        public override object SyncRoot => this;

        /// <inheritdoc />
        public override int Add(
            object value
        )
        {
            _items.Add(value);

            return _items.Count - 1;
        }

        /// <inheritdoc />
        public override void AddRange(
            Array values
        )
        {
            foreach (var value in values)
            {
                _items.Add(value);
            }
        }

        /// <inheritdoc />
        public override void Clear() => _items.Clear();

        /// <inheritdoc />
        public override bool Contains(
            object value
        ) => _items.Contains(value);

        /// <inheritdoc />
        public override bool Contains(
            string value
        ) => false;

        /// <inheritdoc />
        public override void CopyTo(
            Array array,
            int index
        ) => ((ICollection)_items).CopyTo(array, index);

        /// <inheritdoc />
        public override IEnumerator GetEnumerator() => _items.GetEnumerator();

        /// <inheritdoc />
        public override int IndexOf(
            object value
        ) => _items.IndexOf(value);

        /// <inheritdoc />
        public override int IndexOf(
            string parameterName
        ) => -1;

        /// <inheritdoc />
        public override void Insert(
            int index,
            object value
        ) => _items.Insert(index, value);

        /// <inheritdoc />
        public override void Remove(
            object value
        ) => _items.Remove(value);

        /// <inheritdoc />
        public override void RemoveAt(
            int index
        ) => _items.RemoveAt(index);

        /// <inheritdoc />
        public override void RemoveAt(
            string parameterName
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbParameter GetParameter(
            int index
        ) => (DbParameter)_items[index];

        /// <inheritdoc />
        protected override DbParameter GetParameter(
            string parameterName
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void SetParameter(
            int index,
            DbParameter value
        ) => _items[index] = value;

        /// <inheritdoc />
        protected override void SetParameter(
            string parameterName,
            DbParameter value
        ) => throw new NotSupportedException();
    }
}
