// WHY: Linked provider tests and attribution executables consume different counters, injected faults and submissions.
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable NotAccessedPositionalProperty.Global
// ReSharper disable AutoPropertyCanBeMadeGetOnly.Global

namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Executes provider catalog readers against explicit statement-local test results.</summary>
internal sealed class CatalogStatementTestConnection : DbConnection
{
    private readonly Func<string, IReadOnlyList<DbParameter>, DataTable> _result;
    private ConnectionState _state = ConnectionState.Open;

    /// <summary>Creates a native or fallback deterministic catalog transport.</summary>
    public CatalogStatementTestConnection(
        bool native,
        Func<string, IReadOnlyList<DbParameter>, DataTable> result
    )
    {
        CanCreateBatch = native;
        _result = result;
    }

    /// <summary>Gets submitted SQL, parameters, timeout, transaction, and execution mode.</summary>
    public List<Submission> Submissions { get; } = [];

    /// <summary>Gets each native dispatch's statement cardinality.</summary>
    public List<int> BatchCounts { get; } = [];

    /// <summary>Gets disposed command ownership scopes.</summary>
    public int CommandsDisposed { get; private set; }

    /// <summary>Gets disposed native batch ownership scopes.</summary>
    public int BatchesDisposed { get; private set; }

    /// <summary>Gets disposed result readers.</summary>
    public int ReadersDisposed { get; private set; }

    /// <summary>Gets caller transaction disposals, which catalog reads must not initiate.</summary>
    public int TransactionsDisposed { get; private set; }

    /// <summary>Gets or sets a scalar response for scope occupancy and cleanup qualification.</summary>
    public Func<string, object?> Scalar { get; init; } = static _ => null;

    /// <summary>Gets or sets a callback before each cancellation-aware reader step.</summary>
    public Action? BeforeRead { get; init; }

    /// <summary>Gets or sets whether ordered native result sets are maliciously reversed.</summary>
    public bool ReverseResults { get; init; }

    /// <summary>Gets or sets whether one submitted statement's result set is omitted.</summary>
    public bool OmitLastResult { get; init; }

    /// <summary>Gets or sets whether an unsubmitted result set is appended.</summary>
    public bool ExtraResult { get; init; }

    /// <summary>Gets or sets whether SQL, batch sizes and disposal counts are retained for diagnostics.</summary>
    /// <remarks>
    /// Disable for repeated measurements; result construction, faults and resource cleanup are unchanged.
    /// </remarks>
    public bool CaptureObservations { get; init; } = true;

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string Database => "catalog-test";

    /// <inheritdoc />
    public override string DataSource => "catalog-test";

    /// <inheritdoc />
    public override string ServerVersion => "1";

    /// <inheritdoc />
    // WHY: Keep the getter-only framework override; Open and Close own state changes through the explicit field.
    // ReSharper disable once ConvertToAutoPropertyWithPrivateSetter
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override bool CanCreateBatch { get; }

    /// <summary>Clears observations after all prior probe commands, batches and readers are disposed.</summary>
    public void ResetCaptures()
    {
        Submissions.Clear();
        BatchCounts.Clear();
        CommandsDisposed = 0;
        BatchesDisposed = 0;
        ReadersDisposed = 0;
        TransactionsDisposed = 0;
    }

    /// <inheritdoc />
    public override void Open() => _state = ConnectionState.Open;

    /// <inheritdoc />
    public override void Close() => _state = ConnectionState.Closed;

    /// <inheritdoc />
    public override void ChangeDatabase(
        string databaseName
    ) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(
        IsolationLevel isolationLevel
    ) => new TestTransaction(this, isolationLevel);

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new TestCommand(this);

    /// <inheritdoc />
    protected override DbBatch CreateDbBatch() => new TestBatch(this);

    private Submission Record(
        string sql,
        DbParameterCollection parameters,
        int timeout,
        DbTransaction? transaction,
        bool native
    )
    {
        var submission = new Submission(
            sql,
            parameters
                .Cast<DbParameter>()
                .ToArray(),
            timeout,
            transaction,
            native);

        if (CaptureObservations)
        {
            Submissions.Add(submission);
        }

        return submission;
    }

    private TestReader Read(
        IReadOnlyList<Submission> submissions,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var results = new DataSet();
        var ordered = ReverseResults
            ? submissions.Reverse()
            : submissions;

        foreach (var submission in ordered)
        {
            results.Tables.Add(_result(submission.Sql, submission.Parameters));
        }

        if (OmitLastResult && results.Tables.Count > 1)
        {
            results.Tables.RemoveAt(results.Tables.Count - 1);
        }

        if (ExtraResult)
        {
            results.Tables.Add(new DataTable());
        }

        return new TestReader(this, results, results.CreateDataReader());
    }

    /// <summary>Captures one physical statement dispatch without retaining a live command.</summary>
    public sealed record Submission(
        string Sql,
        IReadOnlyList<DbParameter> Parameters,
        int Timeout,
        DbTransaction? Transaction,
        bool Native
    );

    private sealed class TestTransaction : DbTransaction
    {
        private readonly CatalogStatementTestConnection _connection;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        /// <param name="connection">The supplied connection for this fixture.</param>
        /// <param name="isolationLevel">The supplied isolation level for this fixture.</param>
        public TestTransaction(
            CatalogStatementTestConnection connection,
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
        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing && _connection.CaptureObservations)
            {
                _connection.TransactionsDisposed++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TestCommand : DbCommand
    {
        private readonly CatalogStatementTestConnection _connection;

        private readonly TestParameters _parameters;
        private bool _disposed;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        /// <param name="connection">The supplied connection for this fixture.</param>
        public TestCommand(
            CatalogStatementTestConnection connection
        )
        {
            _connection = connection;
            _parameters = new TestParameters();
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
        protected override DbParameter CreateDbParameter() => new TestParameter();

        /// <inheritdoc />
        public override void Cancel() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Prepare() => throw new NotSupportedException();

        /// <inheritdoc />
        public override int ExecuteNonQuery()
        {
            _connection.Record(CommandText, _parameters, CommandTimeout, DbTransaction, false);

            return 0;
        }

        /// <inheritdoc />
        public override object? ExecuteScalar()
        {
            _connection.Record(CommandText, _parameters, CommandTimeout, DbTransaction, false);

            return _connection.Scalar(CommandText);
        }

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(
            CommandBehavior behavior
        ) => _connection.Read(
            [_connection.Record(CommandText, _parameters, CommandTimeout, DbTransaction, false)],
            CancellationToken.None);

        /// <inheritdoc />
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<DbDataReader>(
                _connection.Read(
                    [_connection.Record(CommandText, _parameters, CommandTimeout, DbTransaction, false)],
                    cancellationToken));
        }

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                if (_connection.CaptureObservations)
                {
                    _connection.CommandsDisposed++;
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TestBatch : DbBatch
    {
        private readonly TestBatchCommands _commands;
        private readonly CatalogStatementTestConnection _connection;
        private bool _disposed;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        /// <param name="connection">The supplied connection for this fixture.</param>
        public TestBatch(
            CatalogStatementTestConnection connection
        )
        {
            _connection = connection;
            _commands = new TestBatchCommands();
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
        protected override DbBatchCommand CreateDbBatchCommand() => new TestBatchCommand();

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
        // WHY: Match DbBatch's nullable result contract even though this deliberately unsupported path always throws.
        // ReSharper disable once ReturnTypeCanBeNotNullable
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

        private TestReader Read(
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_connection.CaptureObservations)
            {
                _connection.BatchCounts.Add(_commands.Count);
            }

            // WHY: Snapshot the submitted native statements before the reader owns their result set.
            // ReSharper disable once UseCollectionExpression
            return _connection.Read(
                _commands
                    .Select(command => _connection.Record(
                        command.CommandText,
                        command.Parameters,
                        Timeout,
                        DbTransaction,
                        true))
                    .ToArray(),
                cancellationToken);
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_connection.CaptureObservations)
                {
                    _connection.BatchesDisposed++;
                }
            }

            base.Dispose();
        }
    }

    private sealed class TestBatchCommand : DbBatchCommand
    {
        private readonly TestParameters _parameters = new();

        /// <inheritdoc />
        public override string CommandText { get; set; } = string.Empty;

        /// <inheritdoc />
        public override CommandType CommandType { get; set; }

        /// <inheritdoc />
        public override int RecordsAffected => 0;

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => _parameters;

        /// <inheritdoc />
        public override bool CanCreateParameter => true;

        /// <inheritdoc />
        public override DbParameter CreateParameter() => new TestParameter();
    }

    private sealed class TestBatchCommands : DbBatchCommandCollection
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

    private sealed class TestParameter : DbParameter
    {
        /// <inheritdoc />
        public override DbType DbType { get; set; }

        /// <inheritdoc />
        public override ParameterDirection Direction { get; set; }

        /// <inheritdoc />
        public override bool IsNullable { get; set; }

        /// <inheritdoc />
        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;

        /// <inheritdoc />
        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;

        /// <inheritdoc />
        public override object? Value { get; set; }

        /// <inheritdoc />
        public override bool SourceColumnNullMapping { get; set; }

        /// <inheritdoc />
        public override int Size { get; set; }

        /// <inheritdoc />
        public override void ResetDbType() => DbType = DbType.String;
    }

    private sealed class TestParameters : DbParameterCollection
    {
        private readonly List<DbParameter> _items = [];

        /// <inheritdoc />
        public override int Count => _items.Count;

        /// <inheritdoc />
        public override object SyncRoot => this;

        /// <inheritdoc />
        public override int Add(
            object value
        )
        {
            _items.Add((DbParameter)value);

            return _items.Count - 1;
        }

        /// <inheritdoc />
        public override void AddRange(
            Array values
        )
        {
            foreach (var value in values)
            {
                Add(value);
            }
        }

        /// <inheritdoc />
        public override void Clear() => _items.Clear();

        /// <inheritdoc />
        public override bool Contains(
            object value
        ) => _items.Contains((DbParameter)value);

        /// <inheritdoc />
        public override bool Contains(
            string value
        ) => IndexOf(value) >= 0;

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
        ) => _items.IndexOf((DbParameter)value);

        /// <inheritdoc />
        public override int IndexOf(
            string parameterName
        ) => _items.FindIndex(parameter => parameter.ParameterName == parameterName);

        /// <inheritdoc />
        public override void Insert(
            int index,
            object value
        ) => _items.Insert(index, (DbParameter)value);

        /// <inheritdoc />
        public override void Remove(
            object value
        ) => _items.Remove((DbParameter)value);

        /// <inheritdoc />
        public override void RemoveAt(
            int index
        ) => _items.RemoveAt(index);

        /// <inheritdoc />
        public override void RemoveAt(
            string parameterName
        ) => _items.RemoveAt(IndexOf(parameterName));

        /// <inheritdoc />
        protected override DbParameter GetParameter(
            int index
        ) => _items[index];

        /// <inheritdoc />
        protected override DbParameter GetParameter(
            string parameterName
        ) => _items[IndexOf(parameterName)];

        /// <inheritdoc />
        protected override void SetParameter(
            int index,
            DbParameter value
        ) => _items[index] = value;

        /// <inheritdoc />
        protected override void SetParameter(
            string parameterName,
            DbParameter value
        ) => _items[IndexOf(parameterName)] = value;
    }

    private sealed class TestReader : DbDataReader
    {
        private readonly CatalogStatementTestConnection _connection;
        private readonly DataTableReader _inner;
        private readonly DataSet _results;

        private bool _disposed;

        /// <summary>Initializes the test helper with its supplied dependencies and values.</summary>
        /// <param name="connection">The supplied connection for this fixture.</param>
        /// <param name="results">The supplied results for this fixture.</param>
        /// <param name="inner">The supplied inner for this fixture.</param>
        public TestReader(
            CatalogStatementTestConnection connection,
            DataSet results,
            DataTableReader inner
        )
        {
            _connection = connection;
            _results = results;
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
            _connection.BeforeRead?.Invoke();
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
                _results.Dispose();
                if (_connection.CaptureObservations)
                {
                    _connection.ReadersDisposed++;
                }
            }

            base.Dispose(disposing);
        }
    }
}
