namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Supplies deterministic independent-probe result sets without provider dependencies.</summary>
internal sealed class CatalogProbeTestConnection : System.Data.Common.DbConnection
{
    private bool _disposed;

    /// <summary>Creates a native or sequential test transport.</summary>
    public CatalogProbeTestConnection(bool native) => CanCreateBatch = native;

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
    public List<System.Data.Common.DbTransaction?> Transactions { get; } = [];

    /// <summary>Gets or sets one synthetic response corruption.</summary>
    public string? Fault { get; init; }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string Database => "probe-test";

    /// <inheritdoc />
    public override string DataSource => "probe-test";

    /// <inheritdoc />
    public override string ServerVersion => "1";

    /// <inheritdoc />
    public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;

    /// <inheritdoc />
    public override bool CanCreateBatch { get; }

    /// <inheritdoc />
    public override void Open() { }

    /// <inheritdoc />
    public override void Close() { }

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel)
        => new ProbeTransaction(this, isolationLevel);

    /// <inheritdoc />
    protected override System.Data.Common.DbCommand CreateDbCommand() => new ProbeCommand(this);

    /// <inheritdoc />
    protected override System.Data.Common.DbBatch CreateDbBatch() => new ProbeBatch(this);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            ConnectionDisposals++;
        }

        base.Dispose(disposing);
    }

    private System.Data.DataSet Execute(
        string[] statements,
        int timeout,
        System.Data.Common.DbTransaction? transaction,
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

        var results = new System.Data.DataSet();
        foreach (var statement in statements)
        {
            var table = new System.Data.DataTable();
            table.Columns.Add("ordinal", typeof(int));
            var values = statement.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
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
            results.Tables.Add(new System.Data.DataTable());
        }

        return results;
    }

    private sealed class ProbeTransaction(
        CatalogProbeTestConnection connection,
        System.Data.IsolationLevel isolationLevel
    ) : System.Data.Common.DbTransaction
    {
        private bool _disposed;

        /// <inheritdoc />
        public override System.Data.IsolationLevel IsolationLevel => isolationLevel;
        /// <inheritdoc />
        protected override System.Data.Common.DbConnection DbConnection => connection;
        /// <inheritdoc />
        public override void Commit() { }
        /// <inheritdoc />
        public override void Rollback() { }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                connection.TransactionDisposals++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ProbeCommand(CatalogProbeTestConnection connection) : System.Data.Common.DbCommand
    {
        private readonly ProbeParameters _parameters = new();
        private System.Data.DataSet? _results;
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
        protected override System.Data.Common.DbParameter CreateDbParameter() => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Cancel() => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Prepare() => throw new NotSupportedException();
        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new NotSupportedException();
        /// <inheritdoc />
        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior)
            => Read(CancellationToken.None);
        /// <inheritdoc />
        protected override Task<System.Data.Common.DbDataReader> ExecuteDbDataReaderAsync(
            System.Data.CommandBehavior behavior,
            CancellationToken cancellationToken
        ) => Task.FromResult<System.Data.Common.DbDataReader>(Read(cancellationToken));

        private ProbeReader Read(CancellationToken token)
        {
            _results = connection.Execute([CommandText], CommandTimeout, DbTransaction, native: false, token);

            return new ProbeReader(connection, _results.CreateDataReader());
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _results?.Dispose();
                connection.Disposals++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ProbeBatch(CatalogProbeTestConnection connection) : System.Data.Common.DbBatch
    {
        private readonly ProbeBatchCommands _commands = new();
        private System.Data.DataSet? _results;
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
        protected override System.Data.Common.DbBatchCommand CreateDbBatchCommand() => new ProbeBatchCommand();
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

        private ProbeReader Read(CancellationToken token)
        {
            _results = connection.Execute(
                _commands.Select(command => command.CommandText).ToArray(),
                Timeout, DbTransaction, native: true, token);

            return new ProbeReader(connection, _results.CreateDataReader());
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _results?.Dispose();
                connection.Disposals++;
            }

            base.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class ProbeReader(
        CatalogProbeTestConnection connection,
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
                connection.ReaderDisposals++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ProbeBatchCommand : System.Data.Common.DbBatchCommand
    {
        private readonly ProbeParameters _parameters = new();

        /// <inheritdoc />
        public override string CommandText { get; set; } = string.Empty;
        /// <inheritdoc />
        public override System.Data.CommandType CommandType { get; set; }
        /// <inheritdoc />
        public override int RecordsAffected => 0;
        /// <inheritdoc />
        protected override System.Data.Common.DbParameterCollection DbParameterCollection => _parameters;
        /// <inheritdoc />
        public override bool CanCreateParameter => false;
        /// <inheritdoc />
        public override System.Data.Common.DbParameter CreateParameter() => throw new NotSupportedException();
    }

    private sealed class ProbeBatchCommands : System.Data.Common.DbBatchCommandCollection
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

    private sealed class ProbeParameters : System.Data.Common.DbParameterCollection
    {
        private readonly List<object> _items = [];

        /// <inheritdoc />
        public override int Count => _items.Count;
        /// <inheritdoc />
        public override object SyncRoot => this;
        /// <inheritdoc />
        public override int Add(object value)
        {
            _items.Add(value);

            return _items.Count - 1;
        }

        /// <inheritdoc />
        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                _items.Add(value);
            }
        }

        /// <inheritdoc />
        public override void Clear() => _items.Clear();
        /// <inheritdoc />
        public override bool Contains(object value) => _items.Contains(value);
        /// <inheritdoc />
        public override bool Contains(string value) => false;
        /// <inheritdoc />
        public override void CopyTo(
            Array array,
            int index
        )
            => ((System.Collections.ICollection)_items).CopyTo(array, index);
        /// <inheritdoc />
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        /// <inheritdoc />
        public override int IndexOf(object value) => _items.IndexOf(value);
        /// <inheritdoc />
        public override int IndexOf(string parameterName) => -1;
        /// <inheritdoc />
        public override void Insert(
            int index,
            object value
        ) => _items.Insert(index, value);
        /// <inheritdoc />
        public override void Remove(object value) => _items.Remove(value);
        /// <inheritdoc />
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        /// <inheritdoc />
        public override void RemoveAt(string parameterName) => throw new NotSupportedException();
        /// <inheritdoc />
        protected override System.Data.Common.DbParameter GetParameter(int index)
            => (System.Data.Common.DbParameter)_items[index];
        /// <inheritdoc />
        protected override System.Data.Common.DbParameter GetParameter(string parameterName)
            => throw new NotSupportedException();
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
            => throw new NotSupportedException();
    }
}
