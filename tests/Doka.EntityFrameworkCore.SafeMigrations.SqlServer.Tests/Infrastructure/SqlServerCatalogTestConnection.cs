namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Records catalog transport and supplies deterministic classifications without a live session.</summary>
internal sealed class SqlServerCatalogTestConnection : System.Data.Common.DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

    /// <summary>Creates a connection using native batching or sequential command execution.</summary>
    /// <param name="canCreateBatch">Whether the connection supports a native batch.</param>
    public SqlServerCatalogTestConnection(bool canCreateBatch = true) => CanCreateBatch = canCreateBatch;

    /// <summary>Gets the statements submitted in transport order.</summary>
    public List<string> RecordedStatements { get; } = [];

    /// <summary>Gets the number of native batch execution attempts.</summary>
    public int BatchExecutions { get; private set; }

    /// <summary>Gets each native batch's total UTF-8 statement payload size.</summary>
    public List<int> BatchPayloadBytes { get; } = [];

    /// <summary>Gets the number of statements submitted in each native batch.</summary>
    public List<int> BatchStatementCounts { get; } = [];

    /// <summary>Gets the number of sequential command execution attempts.</summary>
    public int CommandExecutions { get; private set; }

    /// <summary>Gets the timeout supplied to each native or sequential reader execution.</summary>
    public List<int> ObservedTimeouts { get; } = [];

    /// <summary>Gets the transaction supplied to each native or sequential reader execution.</summary>
    public List<System.Data.Common.DbTransaction?> ObservedTransactions { get; } = [];

    /// <summary>Gets the number of disposed sequential commands.</summary>
    public int CommandsDisposed { get; private set; }

    /// <summary>Gets the number of disposed native batches.</summary>
    public int BatchesDisposed { get; private set; }

    /// <summary>Gets the number of disposed caller-created transactions.</summary>
    public int TransactionsDisposed { get; private set; }

    /// <summary>Gets the cancellation token passed to the latest reader execution.</summary>
    public CancellationToken CancellationTokenSeen { get; private set; }

    /// <summary>Gets the successful asynchronous row reads across the connection.</summary>
    public int RowsRead { get; private set; }

    /// <summary>Gets or sets a callback invoked after each successful asynchronous row read.</summary>
    public Action<int>? AfterRowRead { get; set; }

    /// <summary>Gets or sets a callback invoked after each successful asynchronous result-set transition.</summary>
    public Action? AfterNextResult { get; set; }

    /// <summary>Gets or sets a result-ordinal mutation applied to each statement before reader creation.</summary>
    public Func<int[], int[]>? TransformOrdinals { get; set; }

    /// <summary>Gets or sets whether reader execution fails after recording the submitted statements.</summary>
    public bool ThrowOnExecute { get; set; }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string Database => "catalog-test";

    /// <inheritdoc />
    public override string DataSource => "catalog-test";

    /// <inheritdoc />
    public override string ServerVersion => "16.0.0";

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <inheritdoc />
    public override bool CanCreateBatch { get; }

    /// <inheritdoc />
    public override void Open() => _state = ConnectionState.Open;

    /// <inheritdoc />
    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Close() => _state = ConnectionState.Closed;

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override System.Data.Common.DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => new CatalogTransaction(this, isolationLevel);

    /// <inheritdoc />
    protected override System.Data.Common.DbCommand CreateDbCommand() => new CatalogCommand(this);

    /// <inheritdoc />
    protected override System.Data.Common.DbBatch CreateDbBatch()
        => CanCreateBatch ? new CatalogBatch(this) : throw new NotSupportedException();

    private DataSet CreateResults(
        string[] statements,
        bool nativeBatch,
        CancellationToken cancellationToken
    )
    {
        CancellationTokenSeen = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (nativeBatch)
        {
            BatchExecutions++;
        }
        else
        {
            CommandExecutions++;
        }

        RecordedStatements.AddRange(statements);
        if (ThrowOnExecute)
        {
            throw new InvalidOperationException("Injected catalog execution failure.");
        }

        var results = new DataSet { Locale = CultureInfo.InvariantCulture };
        try
        {
            foreach (var statement in statements)
            {
                var table = new DataTable { Locale = CultureInfo.InvariantCulture };
                results.Tables.Add(table);
                table.Columns.Add("ordinal", typeof(int));
                table.Columns.Add("state", typeof(string));
                table.Columns.Add("postcondition", typeof(int));
                table.Columns.Add("repair", typeof(int));
                table.Columns.Add("code", typeof(string));
                table.Columns.Add("row_evidence", typeof(string));
                table.Columns.Add("dependencies", typeof(string));
                table.Columns.Add("diagnostics", typeof(string));
                table.Columns.Add("matched", typeof(string));

                // WHY: Guard fallback SELECTs repeat ordinals without emitting extra rows. Only metadata
                // selections and delayed EXEC arguments identify an operation's actual result once.
                var ordinals = System.Text.RegularExpressions.Regex
                    .Matches(statement, @"\bSELECT\s+(\d+)\s*,\s*\(|@doka_ordinal\s*=\s*(\d+)")
                    .Select(static match => int.Parse(
                        match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
                        CultureInfo.InvariantCulture))
                    .ToArray();

                Array.Sort(ordinals);
                ordinals = TransformOrdinals?.Invoke(ordinals) ?? ordinals;
                foreach (var ordinal in ordinals)
                {
                    var state = (ordinal % 3) switch
                    {
                        0 => "missing",
                        1 => "matching",
                        _ => "different",
                    };

                    table.Rows.Add(ordinal, state, state == "matching" ? 1 : 0, 0,
                        DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
                }
            }

            return results;
        }
        catch
        {
            results.Dispose();
            throw;
        }
    }

    private sealed class CatalogTransaction : System.Data.Common.DbTransaction
    {
        private readonly SqlServerCatalogTestConnection _connection;
        private bool _disposed;

        /// <summary>Creates a transaction carrying the requested connection and isolation level.</summary>
        /// <param name="connection">The recording connection owning the transaction.</param>
        /// <param name="isolationLevel">The isolation level requested by the test.</param>
        public CatalogTransaction(
            SqlServerCatalogTestConnection connection,
            IsolationLevel isolationLevel
        )
        {
            _connection = connection;
            DbConnection = connection;
            IsolationLevel = isolationLevel;
        }

        /// <inheritdoc />
        protected override System.Data.Common.DbConnection DbConnection { get; }

        /// <inheritdoc />
        public override IsolationLevel IsolationLevel { get; }

        /// <inheritdoc />
        public override void Commit()
        {
        }

        /// <inheritdoc />
        public override void Rollback()
        {
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _connection.TransactionsDisposed++;
                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CatalogCommand : System.Data.Common.DbCommand
    {
        private readonly SqlServerCatalogTestConnection _connection;
        private readonly SqlCommand _parameterHost = new();
        private DataSet? _results;
        private bool _disposed;

        /// <summary>Creates a sequential catalog command recording execution on its connection.</summary>
        /// <param name="connection">The recording connection owning the command.</param>
        public CatalogCommand(SqlServerCatalogTestConnection connection)
        {
            _connection = connection;
            DbConnection = connection;
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
        protected override System.Data.Common.DbConnection? DbConnection { get; set; }

        /// <inheritdoc />
        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }

        /// <inheritdoc />
        protected override System.Data.Common.DbParameterCollection DbParameterCollection => _parameterHost.Parameters;

        /// <inheritdoc />
        public override void Cancel() => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Prepare() => throw new NotSupportedException();

        /// <inheritdoc />
        public override int ExecuteNonQuery() => throw new NotSupportedException();

        /// <inheritdoc />
        public override object? ExecuteScalar() => throw new NotSupportedException();

        /// <inheritdoc />
        protected override System.Data.Common.DbParameter CreateDbParameter() => new SqlParameter();

        /// <inheritdoc />
        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => Read(CancellationToken.None);

        /// <inheritdoc />
        protected override Task<System.Data.Common.DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
            => Task.FromResult<System.Data.Common.DbDataReader>(Read(cancellationToken));

        /// <summary>Records one statement and creates its deterministic catalog result reader.</summary>
        /// <param name="cancellationToken">The caller's cancellation token.</param>
        /// <returns>The statement's catalog classifications.</returns>
        private CatalogReader Read(CancellationToken cancellationToken)
        {
            _results?.Dispose();
            _connection.ObservedTimeouts.Add(CommandTimeout);
            _connection.ObservedTransactions.Add(DbTransaction);
            _results = _connection.CreateResults([CommandText], nativeBatch: false, cancellationToken);

            return new CatalogReader(_connection, _results.CreateDataReader());
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _results?.Dispose();
                _parameterHost.Dispose();
                _connection.CommandsDisposed++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CatalogBatch : System.Data.Common.DbBatch
    {
        private readonly SqlServerCatalogTestConnection _connection;
        private readonly CatalogBatchCommands _commands = new();
        private DataSet? _results;
        private bool _disposed;

        /// <summary>Creates a native catalog batch recording execution on its connection.</summary>
        /// <param name="connection">The recording connection owning the batch.</param>
        public CatalogBatch(SqlServerCatalogTestConnection connection)
        {
            _connection = connection;
            DbConnection = connection;
        }

        /// <inheritdoc />
        public override int Timeout { get; set; }

        /// <inheritdoc />
        protected override System.Data.Common.DbConnection? DbConnection { get; set; }

        /// <inheritdoc />
        protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }

        /// <inheritdoc />
        protected override System.Data.Common.DbBatchCommandCollection DbBatchCommands => _commands;

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
        protected override System.Data.Common.DbBatchCommand CreateDbBatchCommand() => new CatalogBatchCommand();

        /// <inheritdoc />
        protected override System.Data.Common.DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => Read(CancellationToken.None);

        /// <inheritdoc />
        protected override Task<System.Data.Common.DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
            => Task.FromResult<System.Data.Common.DbDataReader>(Read(cancellationToken));

        /// <summary>Records native transport bounds and creates one result set per statement.</summary>
        /// <param name="cancellationToken">The caller's cancellation token.</param>
        /// <returns>The batch's catalog classifications in statement order.</returns>
        private CatalogReader Read(CancellationToken cancellationToken)
        {
            _results?.Dispose();
            var statements = _commands.Select(static command => command.CommandText).ToArray();
            _connection.BatchPayloadBytes.Add(
                statements.Sum(static statement => Encoding.UTF8.GetByteCount(statement)));

            _connection.BatchStatementCounts.Add(statements.Length);
            _connection.ObservedTimeouts.Add(Timeout);
            _connection.ObservedTransactions.Add(DbTransaction);
            _results = _connection.CreateResults(statements, nativeBatch: true, cancellationToken);

            return new CatalogReader(_connection, _results.CreateDataReader());
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _results?.Dispose();
                foreach (var command in _commands)
                {
                    ((CatalogBatchCommand)command).Dispose();
                }

                _connection.BatchesDisposed++;
            }

            base.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class CatalogReader : System.Data.Common.DbDataReader
    {
        private readonly SqlServerCatalogTestConnection _connection;
        private readonly DataTableReader _reader;

        /// <summary>Creates a forwarding reader with asynchronous cancellation-test callbacks.</summary>
        /// <param name="connection">The recording connection receiving successful-read callbacks.</param>
        /// <param name="reader">The table-backed reader owned by this wrapper.</param>
        public CatalogReader(
            SqlServerCatalogTestConnection connection,
            DataTableReader reader
        )
        {
            _connection = connection;
            _reader = reader;
        }

        /// <inheritdoc />
        public override int Depth => _reader.Depth;

        /// <inheritdoc />
        public override int FieldCount => _reader.FieldCount;

        /// <inheritdoc />
        public override bool HasRows => _reader.HasRows;

        /// <inheritdoc />
        public override bool IsClosed => _reader.IsClosed;

        /// <inheritdoc />
        public override int RecordsAffected => _reader.RecordsAffected;

        /// <inheritdoc />
        public override object this[int ordinal] => _reader[ordinal];

        /// <inheritdoc />
        public override object this[string name] => _reader[name];

        /// <inheritdoc />
        public override bool Read() => _reader.Read();

        /// <inheritdoc />
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            var read = await _reader.ReadAsync(cancellationToken);
            if (read)
            {
                _connection.RowsRead++;
                _connection.AfterRowRead?.Invoke(_connection.RowsRead);
            }

            return read;
        }

        /// <inheritdoc />
        public override bool NextResult() => _reader.NextResult();

        /// <inheritdoc />
        public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
        {
            var next = await _reader.NextResultAsync(cancellationToken);
            if (next)
            {
                _connection.AfterNextResult?.Invoke();
            }

            return next;
        }

        /// <inheritdoc />
        public override bool GetBoolean(int ordinal) => _reader.GetBoolean(ordinal);

        /// <inheritdoc />
        public override byte GetByte(int ordinal) => _reader.GetByte(ordinal);

        /// <inheritdoc />
        public override long GetBytes(
            int ordinal,
            long dataOffset,
            byte[]? buffer,
            int bufferOffset,
            int length
        )
            => _reader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

        /// <inheritdoc />
        public override char GetChar(int ordinal) => _reader.GetChar(ordinal);

        /// <inheritdoc />
        public override long GetChars(
            int ordinal,
            long dataOffset,
            char[]? buffer,
            int bufferOffset,
            int length
        )
            => _reader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

        /// <inheritdoc />
        public override string GetDataTypeName(int ordinal) => _reader.GetDataTypeName(ordinal);

        /// <inheritdoc />
        public override DateTime GetDateTime(int ordinal) => _reader.GetDateTime(ordinal);

        /// <inheritdoc />
        public override decimal GetDecimal(int ordinal) => _reader.GetDecimal(ordinal);

        /// <inheritdoc />
        public override double GetDouble(int ordinal) => _reader.GetDouble(ordinal);

        /// <inheritdoc />
        public override System.Collections.IEnumerator GetEnumerator() => _reader.GetEnumerator();

        /// <inheritdoc />
        public override Type GetFieldType(int ordinal) => _reader.GetFieldType(ordinal);

        /// <inheritdoc />
        public override float GetFloat(int ordinal) => _reader.GetFloat(ordinal);

        /// <inheritdoc />
        public override Guid GetGuid(int ordinal) => _reader.GetGuid(ordinal);

        /// <inheritdoc />
        public override short GetInt16(int ordinal) => _reader.GetInt16(ordinal);

        /// <inheritdoc />
        public override int GetInt32(int ordinal) => _reader.GetInt32(ordinal);

        /// <inheritdoc />
        public override long GetInt64(int ordinal) => _reader.GetInt64(ordinal);

        /// <inheritdoc />
        public override string GetName(int ordinal) => _reader.GetName(ordinal);

        /// <inheritdoc />
        public override int GetOrdinal(string name) => _reader.GetOrdinal(name);

        /// <inheritdoc />
        public override DataTable? GetSchemaTable() => _reader.GetSchemaTable();

        /// <inheritdoc />
        public override string GetString(int ordinal) => _reader.GetString(ordinal);

        /// <inheritdoc />
        public override object GetValue(int ordinal) => _reader.GetValue(ordinal);

        /// <inheritdoc />
        public override int GetValues(object[] values) => _reader.GetValues(values);

        /// <inheritdoc />
        public override bool IsDBNull(int ordinal) => _reader.IsDBNull(ordinal);

        /// <inheritdoc />
        public override void Close() => _reader.Close();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reader.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CatalogBatchCommand : System.Data.Common.DbBatchCommand, IDisposable
    {
        private readonly SqlCommand _parameterHost = new();

        /// <inheritdoc />
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        /// <inheritdoc />
        public override CommandType CommandType { get; set; }

        /// <inheritdoc />
        public override int RecordsAffected => -1;

        /// <inheritdoc />
        protected override System.Data.Common.DbParameterCollection DbParameterCollection => _parameterHost.Parameters;

        /// <inheritdoc />
        public void Dispose()
        {
            _parameterHost.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class CatalogBatchCommands : System.Data.Common.DbBatchCommandCollection
    {
        private readonly List<System.Data.Common.DbBatchCommand> _commands = [];

        /// <inheritdoc />
        public override int Count => _commands.Count;

        /// <inheritdoc />
        public override bool IsReadOnly => false;

        /// <inheritdoc />
        public override IEnumerator<System.Data.Common.DbBatchCommand> GetEnumerator() => _commands.GetEnumerator();

        /// <inheritdoc />
        public override void Add(System.Data.Common.DbBatchCommand item) => _commands.Add(item);

        /// <inheritdoc />
        public override void Clear() => _commands.Clear();

        /// <inheritdoc />
        public override bool Contains(System.Data.Common.DbBatchCommand item) => _commands.Contains(item);

        /// <inheritdoc />
        public override void CopyTo(
            System.Data.Common.DbBatchCommand[] array,
            int arrayIndex
        )
            => _commands.CopyTo(array, arrayIndex);

        /// <inheritdoc />
        public override int IndexOf(System.Data.Common.DbBatchCommand item) => _commands.IndexOf(item);

        /// <inheritdoc />
        public override void Insert(
            int index,
            System.Data.Common.DbBatchCommand item
        )
            => _commands.Insert(index, item);

        /// <inheritdoc />
        public override bool Remove(System.Data.Common.DbBatchCommand item) => _commands.Remove(item);

        /// <inheritdoc />
        public override void RemoveAt(int index) => _commands.RemoveAt(index);

        /// <inheritdoc />
        protected override System.Data.Common.DbBatchCommand GetBatchCommand(int index) => _commands[index];

        /// <inheritdoc />
        protected override void SetBatchCommand(
            int index,
            System.Data.Common.DbBatchCommand batchCommand
        )
            => _commands[index] = batchCommand;
    }
}
