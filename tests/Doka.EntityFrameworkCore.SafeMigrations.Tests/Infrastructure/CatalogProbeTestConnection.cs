using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

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

    private sealed class ProbeTransaction(
        CatalogProbeTestConnection connection,
        IsolationLevel isolationLevel
    ) : DbTransaction
    {
        private bool _disposed;

        /// <inheritdoc />
        public override IsolationLevel IsolationLevel => isolationLevel;

        /// <inheritdoc />
        protected override DbConnection DbConnection => connection;

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
                connection.TransactionDisposals++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ProbeCommand(CatalogProbeTestConnection connection) : DbCommand
    {
        private readonly ProbeParameters _parameters = new();
        private bool _disposed;
        private DataSet? _results;

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
        protected override DbConnection? DbConnection { get; set; } = connection;

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
            CancellationToken token
        )
        {
            _results = connection.Execute([CommandText], CommandTimeout, DbTransaction, native: false, token);

            return new ProbeReader(connection, _results.CreateDataReader());
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
                connection.Disposals++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ProbeBatch(CatalogProbeTestConnection connection) : DbBatch
    {
        private readonly ProbeBatchCommands _commands = new();
        private bool _disposed;
        private DataSet? _results;

        /// <inheritdoc />
        public override int Timeout { get; set; }

        /// <inheritdoc />
        protected override DbConnection? DbConnection { get; set; } = connection;

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
            CancellationToken token
        )
        {
            _results = connection.Execute(
                _commands
                    .Select(command => command.CommandText)
                    .ToArray(),
                Timeout,
                DbTransaction,
                native: true,
                token);

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
        DataTableReader inner
    ) : DbDataReader
    {
        private bool _disposed;

        /// <inheritdoc />
        public override object this[
            int ordinal
        ] => inner[ordinal];

        /// <inheritdoc />
        public override object this[
            string name
        ] => inner[name];

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
        public override bool GetBoolean(
            int ordinal
        ) => inner.GetBoolean(ordinal);

        /// <inheritdoc />
        public override byte GetByte(
            int ordinal
        ) => inner.GetByte(ordinal);

        /// <inheritdoc />
        public override long GetBytes(
            int ordinal,
            long dataOffset,
            byte[]? buffer,
            int bufferOffset,
            int length
        ) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

        /// <inheritdoc />
        public override char GetChar(
            int ordinal
        ) => inner.GetChar(ordinal);

        /// <inheritdoc />
        public override long GetChars(
            int ordinal,
            long dataOffset,
            char[]? buffer,
            int bufferOffset,
            int length
        ) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

        /// <inheritdoc />
        public override string GetDataTypeName(
            int ordinal
        ) => inner.GetDataTypeName(ordinal);

        /// <inheritdoc />
        public override DateTime GetDateTime(
            int ordinal
        ) => inner.GetDateTime(ordinal);

        /// <inheritdoc />
        public override decimal GetDecimal(
            int ordinal
        ) => inner.GetDecimal(ordinal);

        /// <inheritdoc />
        public override double GetDouble(
            int ordinal
        ) => inner.GetDouble(ordinal);

        /// <inheritdoc />
        public override Type GetFieldType(
            int ordinal
        ) => inner.GetFieldType(ordinal);

        /// <inheritdoc />
        public override float GetFloat(
            int ordinal
        ) => inner.GetFloat(ordinal);

        /// <inheritdoc />
        public override Guid GetGuid(
            int ordinal
        ) => inner.GetGuid(ordinal);

        /// <inheritdoc />
        public override short GetInt16(
            int ordinal
        ) => inner.GetInt16(ordinal);

        /// <inheritdoc />
        public override int GetInt32(
            int ordinal
        ) => inner.GetInt32(ordinal);

        /// <inheritdoc />
        public override long GetInt64(
            int ordinal
        ) => inner.GetInt64(ordinal);

        /// <inheritdoc />
        public override string GetName(
            int ordinal
        ) => inner.GetName(ordinal);

        /// <inheritdoc />
        public override int GetOrdinal(
            string name
        ) => inner.GetOrdinal(name);

        /// <inheritdoc />
        public override string GetString(
            int ordinal
        ) => inner.GetString(ordinal);

        /// <inheritdoc />
        public override object GetValue(
            int ordinal
        ) => inner.GetValue(ordinal);

        /// <inheritdoc />
        public override int GetValues(
            object[] values
        ) => inner.GetValues(values);

        /// <inheritdoc />
        public override bool IsDBNull(
            int ordinal
        ) => inner.IsDBNull(ordinal);

        /// <inheritdoc />
        public override IEnumerator GetEnumerator() => inner.GetEnumerator();

        /// <inheritdoc />
        public override bool Read() => inner.Read();

        /// <inheritdoc />
        public override bool NextResult() => inner.NextResult();

        /// <inheritdoc />
        public override Task<bool> ReadAsync(
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return inner.ReadAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override Task<bool> NextResultAsync(
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            return inner.NextResultAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override void Close() => inner.Close();

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
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
