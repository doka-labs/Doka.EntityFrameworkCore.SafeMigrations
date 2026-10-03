namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Executes provider catalog readers against explicit statement-local test results.</summary>
internal sealed class CatalogStatementTestConnection : System.Data.Common.DbConnection
{
    private readonly Func<string, IReadOnlyList<System.Data.Common.DbParameter>, System.Data.DataTable> _result;
    private System.Data.ConnectionState _state = System.Data.ConnectionState.Open;

    /// <summary>Creates a native or fallback deterministic catalog transport.</summary>
    public CatalogStatementTestConnection(
        bool native,
        Func<string, IReadOnlyList<System.Data.Common.DbParameter>, System.Data.DataTable> result
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

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;
    /// <inheritdoc />
    public override string Database => "catalog-test";
    /// <inheritdoc />
    public override string DataSource => "catalog-test";
    /// <inheritdoc />
    public override string ServerVersion => "1";
    /// <inheritdoc />
    public override System.Data.ConnectionState State => _state;
    /// <inheritdoc />
    public override bool CanCreateBatch { get; }
    /// <inheritdoc />
    public override void Open() => _state = System.Data.ConnectionState.Open;
    /// <inheritdoc />
    public override void Close() => _state = System.Data.ConnectionState.Closed;
    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    /// <inheritdoc />
    protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel)
        => new TestTransaction(this, isolationLevel);
    /// <inheritdoc />
    protected override System.Data.Common.DbCommand CreateDbCommand() => new TestCommand(this);
    /// <inheritdoc />
    protected override System.Data.Common.DbBatch CreateDbBatch() => new TestBatch(this);

    private Submission Record(
        string sql,
        System.Data.Common.DbParameterCollection parameters,
        int timeout,
        System.Data.Common.DbTransaction? transaction,
        bool native
    )
    {
        var submission = new Submission(sql, parameters.Cast<System.Data.Common.DbParameter>().ToArray(),
            timeout, transaction, native);

        Submissions.Add(submission);

        return submission;
    }

    private TestReader Read(
        IReadOnlyList<Submission> submissions,
        CancellationToken token
    )
    {
        token.ThrowIfCancellationRequested();

        var results = new System.Data.DataSet();
        var ordered = ReverseResults ? submissions.Reverse() : submissions;
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
            results.Tables.Add(new System.Data.DataTable());
        }

        return new TestReader(this, results, results.CreateDataReader());
    }

    /// <summary>Captures one physical statement dispatch without retaining a live command.</summary>
    public sealed record Submission(
        string Sql,
        IReadOnlyList<System.Data.Common.DbParameter> Parameters,
        int Timeout,
        System.Data.Common.DbTransaction? Transaction,
        bool Native
    );

    private sealed class TestTransaction(
        CatalogStatementTestConnection connection,
        System.Data.IsolationLevel isolationLevel
    ) : System.Data.Common.DbTransaction
    {
        /// <inheritdoc />
        public override System.Data.IsolationLevel IsolationLevel => isolationLevel;
        /// <inheritdoc />
        protected override System.Data.Common.DbConnection DbConnection => connection;
        /// <inheritdoc />
        public override void Commit() => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Rollback() => throw new NotSupportedException();
        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                connection.TransactionsDisposed++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TestCommand(CatalogStatementTestConnection connection) : System.Data.Common.DbCommand
    {
        private readonly TestParameters _parameters = new();
        private bool _disposed;

        /// <inheritdoc />
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        /// <inheritdoc />
        public override int CommandTimeout { get; set; }
        /// <inheritdoc />
        public override System.Data.CommandType CommandType { get; set; }
        /// <inheritdoc />
        public override bool DesignTimeVisible { get; set; }
        /// <inheritdoc />
        public override System.Data.UpdateRowSource UpdatedRowSource { get; set; }
        /// <inheritdoc />
        protected override System.Data.Common.DbConnection? DbConnection { get; set; } = connection;
        /// <inheritdoc />
        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }
        /// <inheritdoc />
        protected override System.Data.Common.DbParameterCollection DbParameterCollection => _parameters;
        /// <inheritdoc />
        protected override System.Data.Common.DbParameter CreateDbParameter() => new TestParameter();
        /// <inheritdoc />
        public override void Cancel() => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Prepare() => throw new NotSupportedException();
        /// <inheritdoc />
        public override int ExecuteNonQuery()
        {
            connection.Record(CommandText, _parameters, CommandTimeout, DbTransaction, false);

            return 0;
        }

        /// <inheritdoc />
        public override object? ExecuteScalar()
        {
            connection.Record(CommandText, _parameters, CommandTimeout, DbTransaction, false);

            return connection.Scalar(CommandText);
        }

        /// <inheritdoc />
        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior)
            => connection.Read([connection.Record(CommandText, _parameters, CommandTimeout, DbTransaction, false)],
                CancellationToken.None);

        /// <inheritdoc />
        protected override Task<System.Data.Common.DbDataReader> ExecuteDbDataReaderAsync(
            System.Data.CommandBehavior behavior,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<System.Data.Common.DbDataReader>(connection.Read(
                [connection.Record(CommandText, _parameters, CommandTimeout, DbTransaction, false)],
                cancellationToken));
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                connection.CommandsDisposed++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TestBatch(CatalogStatementTestConnection connection) : System.Data.Common.DbBatch
    {
        private readonly TestBatchCommands _commands = new();
        private bool _disposed;

        /// <inheritdoc />
        public override int Timeout { get; set; }
        /// <inheritdoc />
        protected override System.Data.Common.DbConnection? DbConnection { get; set; } = connection;
        /// <inheritdoc />
        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }
        /// <inheritdoc />
        protected override System.Data.Common.DbBatchCommandCollection DbBatchCommands => _commands;
        /// <inheritdoc />
        protected override System.Data.Common.DbBatchCommand CreateDbBatchCommand() => new TestBatchCommand();
        /// <inheritdoc />
        public override void Cancel() => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Prepare() => throw new NotSupportedException();
        /// <inheritdoc />
        public override Task PrepareAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        /// <inheritdoc />
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new NotSupportedException();
        /// <inheritdoc />
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        /// <inheritdoc />
        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior)
            => Read(CancellationToken.None);
        /// <inheritdoc />
        protected override Task<System.Data.Common.DbDataReader> ExecuteDbDataReaderAsync(
            System.Data.CommandBehavior behavior,
            CancellationToken cancellationToken
        ) => Task.FromResult<System.Data.Common.DbDataReader>(Read(cancellationToken));

        private TestReader Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            connection.BatchCounts.Add(_commands.Count);

            return connection.Read(_commands.Select(command => connection.Record(command.CommandText,
                command.Parameters, Timeout, DbTransaction, true)).ToArray(), token);
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                connection.BatchesDisposed++;
            }

            base.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class TestBatchCommand : System.Data.Common.DbBatchCommand
    {
        private readonly TestParameters _parameters = new();
        /// <inheritdoc />
        public override string CommandText { get; set; } = string.Empty;
        /// <inheritdoc />
        public override System.Data.CommandType CommandType { get; set; }
        /// <inheritdoc />
        public override int RecordsAffected => 0;
        /// <inheritdoc />
        protected override System.Data.Common.DbParameterCollection DbParameterCollection => _parameters;
        /// <inheritdoc />
        public override bool CanCreateParameter => true;
        /// <inheritdoc />
        public override System.Data.Common.DbParameter CreateParameter() => new TestParameter();
    }

    private sealed class TestBatchCommands : System.Data.Common.DbBatchCommandCollection
    {
        private readonly List<System.Data.Common.DbBatchCommand> _items = [];
        /// <inheritdoc />
        public override int Count => _items.Count;
        /// <inheritdoc />
        public override bool IsReadOnly => false;
        /// <inheritdoc />
        public override void Add(System.Data.Common.DbBatchCommand item) => _items.Add(item);
        /// <inheritdoc />
        public override void Clear() => _items.Clear();
        /// <inheritdoc />
        public override bool Contains(System.Data.Common.DbBatchCommand item) => _items.Contains(item);
        /// <inheritdoc />
        public override void CopyTo(
            System.Data.Common.DbBatchCommand[] array,
            int arrayIndex
        )
            => _items.CopyTo(array, arrayIndex);
        /// <inheritdoc />
        public override IEnumerator<System.Data.Common.DbBatchCommand> GetEnumerator() => _items.GetEnumerator();
        /// <inheritdoc />
        public override int IndexOf(System.Data.Common.DbBatchCommand item) => _items.IndexOf(item);
        /// <inheritdoc />
        public override void Insert(
            int index,
            System.Data.Common.DbBatchCommand item
        ) => _items.Insert(index, item);
        /// <inheritdoc />
        public override bool Remove(System.Data.Common.DbBatchCommand item) => _items.Remove(item);
        /// <inheritdoc />
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        /// <inheritdoc />
        protected override System.Data.Common.DbBatchCommand GetBatchCommand(int index) => _items[index];
        /// <inheritdoc />
        protected override void SetBatchCommand(
            int index,
            System.Data.Common.DbBatchCommand batchCommand
        )
            => _items[index] = batchCommand;
    }

    private sealed class TestParameter : System.Data.Common.DbParameter
    {
        /// <inheritdoc />
        public override System.Data.DbType DbType { get; set; }
        /// <inheritdoc />
        public override System.Data.ParameterDirection Direction { get; set; }
        /// <inheritdoc />
        public override bool IsNullable { get; set; }
        /// <inheritdoc />
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        /// <inheritdoc />
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        /// <inheritdoc />
        public override object? Value { get; set; }
        /// <inheritdoc />
        public override bool SourceColumnNullMapping { get; set; }
        /// <inheritdoc />
        public override int Size { get; set; }
        /// <inheritdoc />
        public override void ResetDbType() => DbType = System.Data.DbType.String;
    }

    private sealed class TestParameters : System.Data.Common.DbParameterCollection
    {
        private readonly List<System.Data.Common.DbParameter> _items = [];
        /// <inheritdoc />
        public override int Count => _items.Count;
        /// <inheritdoc />
        public override object SyncRoot => this;
        /// <inheritdoc />
        public override int Add(object value)
        {
            _items.Add((System.Data.Common.DbParameter)value);

            return _items.Count - 1;
        }

        /// <inheritdoc />
        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                Add(value);
            }
        }

        /// <inheritdoc />
        public override void Clear() => _items.Clear();
        /// <inheritdoc />
        public override bool Contains(object value) => _items.Contains((System.Data.Common.DbParameter)value);
        /// <inheritdoc />
        public override bool Contains(string value) => IndexOf(value) >= 0;
        /// <inheritdoc />
        public override void CopyTo(
            Array array,
            int index
        )
            => ((System.Collections.ICollection)_items).CopyTo(array, index);
        /// <inheritdoc />
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        /// <inheritdoc />
        public override int IndexOf(object value) => _items.IndexOf((System.Data.Common.DbParameter)value);
        /// <inheritdoc />
        public override int IndexOf(string parameterName)
            => _items.FindIndex(parameter => parameter.ParameterName == parameterName);
        /// <inheritdoc />
        public override void Insert(
            int index,
            object value
        )
            => _items.Insert(index, (System.Data.Common.DbParameter)value);
        /// <inheritdoc />
        public override void Remove(object value) => _items.Remove((System.Data.Common.DbParameter)value);
        /// <inheritdoc />
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        /// <inheritdoc />
        public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
        /// <inheritdoc />
        protected override System.Data.Common.DbParameter GetParameter(int index) => _items[index];
        /// <inheritdoc />
        protected override System.Data.Common.DbParameter GetParameter(string parameterName)
            => _items[IndexOf(parameterName)];
        /// <inheritdoc />
        protected override void SetParameter(
            int index,
            System.Data.Common.DbParameter value
        ) => _items[index] = value;
        /// <inheritdoc />
        protected override void SetParameter(
            string parameterName,
            System.Data.Common.DbParameter value
        )
            => _items[IndexOf(parameterName)] = value;
    }

    private sealed class TestReader(
        CatalogStatementTestConnection connection,
        System.Data.DataSet results,
        System.Data.DataTableReader inner
    ) : System.Data.Common.DbDataReader
    {
        private bool _disposed;
        /// <inheritdoc />
        public override object this[int ordinal] => inner[ordinal];
        /// <inheritdoc />
        public override object this[string name] => inner[name];
        /// <inheritdoc />
        public override int Depth => inner.Depth;
        /// <inheritdoc />
        public override int FieldCount => inner.FieldCount;
        /// <inheritdoc />
        public override bool HasRows => inner.HasRows;
        /// <inheritdoc />
        public override bool IsClosed => inner.IsClosed;
        /// <inheritdoc />
        public override int RecordsAffected => inner.RecordsAffected;
        /// <inheritdoc />
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        /// <inheritdoc />
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        /// <inheritdoc />
        public override long GetBytes(
            int ordinal,
            long dataOffset,
            byte[]? buffer,
            int bufferOffset,
            int length
        )
            => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        /// <inheritdoc />
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        /// <inheritdoc />
        public override long GetChars(
            int ordinal,
            long dataOffset,
            char[]? buffer,
            int bufferOffset,
            int length
        )
            => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        /// <inheritdoc />
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        /// <inheritdoc />
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        /// <inheritdoc />
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        /// <inheritdoc />
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        /// <inheritdoc />
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        /// <inheritdoc />
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        /// <inheritdoc />
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        /// <inheritdoc />
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        /// <inheritdoc />
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        /// <inheritdoc />
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        /// <inheritdoc />
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        /// <inheritdoc />
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        /// <inheritdoc />
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        /// <inheritdoc />
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        /// <inheritdoc />
        public override int GetValues(object[] values) => inner.GetValues(values);
        /// <inheritdoc />
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        /// <inheritdoc />
        public override System.Collections.IEnumerator GetEnumerator() => inner.GetEnumerator();
        /// <inheritdoc />
        public override bool Read() => inner.Read();
        /// <inheritdoc />
        public override bool NextResult() => inner.NextResult();
        /// <inheritdoc />
        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            connection.BeforeRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            return inner.ReadAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return inner.NextResultAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override void Close() => inner.Close();
        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                inner.Dispose();
                results.Dispose();
                connection.ReadersDisposed++;
            }

            base.Dispose(disposing);
        }
    }
}
