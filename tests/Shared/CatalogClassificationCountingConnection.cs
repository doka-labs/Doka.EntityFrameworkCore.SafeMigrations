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

    /// <summary>Returns an ordinal belonging to no submitted prerequisite candidate.</summary>
    UnsubmittedPrerequisiteOrdinal,

    /// <summary>Omits every row from one dispatched prerequisite statement.</summary>
    MissingPrerequisiteRows,
}

/// <summary>Counts real classifier statements through the supported sequential connection fallback.</summary>
internal sealed class CatalogClassificationCountingConnection : DbConnection
{
    private readonly DbConnection _inner;
    private readonly CatalogClassificationResultFault _fault;
    private readonly bool _nativeBatch;
    private bool _innerDisposed;

    /// <summary>Wraps a test-owned provider connection without retaining its SQL or result values.</summary>
    /// <param name="inner">The provider connection disposed together with this wrapper.</param>
    /// <param name="fault">The optional malformed result injected once after real classifier dispatch.</param>
    /// <param name="nativeBatch">Whether provider-native batches should be forwarded and counted.</param>
    public CatalogClassificationCountingConnection(
        DbConnection inner,
        CatalogClassificationResultFault fault = CatalogClassificationResultFault.None,
        bool nativeBatch = false
    )
    {
        _inner = inner;
        _fault = fault;
        _nativeBatch = nativeBatch;
    }

    /// <summary>Gets the number of dispatched statements returning the nine classifier facets.</summary>
    public int ClassificationStatementCount { get; private set; }

    /// <summary>Gets attempts to create test-observed MariaDB catalog snapshot tables.</summary>
    public int CatalogSnapshotCreateCount { get; private set; }

    /// <summary>Gets classifier statements that actually read MariaDB snapshot relations.</summary>
    public int CatalogSnapshotClassificationStatementCount { get; private set; }

    /// <summary>Gets dispatched nonconstant prerequisite statements.</summary>
    public int PrerequisiteStatementCount { get; private set; }

    /// <summary>Gets dispatched catalog-only narrowing eligibility statements.</summary>
    public int NarrowingEligibilityStatementCount { get; private set; }

    /// <summary>Gets dispatched qualified narrowing row-probe statements.</summary>
    public int NarrowingDataStatementCount { get; private set; }

    /// <summary>Gets dispatched deferred column diagnostic statements.</summary>
    public int ColumnDiagnosticStatementCount { get; private set; }

    /// <summary>Gets native batch dispatches containing qualified narrowing row probes, not wire roundtrips.</summary>
    public int NativeNarrowingBatchExecutionCount { get; private set; }

    /// <summary>Gets qualified narrowing statements submitted through real provider-native batches.</summary>
    public int NativeNarrowingStatementCount { get; private set; }

    /// <summary>Gets the largest number of statements submitted in one provider-native batch.</summary>
    public int LargestNativeBatchStatementCount { get; private set; }

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
    public override bool CanCreateBatch => _nativeBatch && _inner.CanCreateBatch;

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
    protected override DbBatch CreateDbBatch() => new CountingBatch(this, _inner.CreateBatch());

    private static bool IsNarrowingDataStatement(string commandText) =>
        commandText.Contains("char_length(", StringComparison.OrdinalIgnoreCase)
        && (commandText.StartsWith("SELECT EXISTS(SELECT 1 FROM ", StringComparison.Ordinal)
            || commandText.StartsWith("SELECT COALESCE(", StringComparison.Ordinal));

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
        public override int ExecuteNonQuery()
        {
            CountSnapshotCreation();

            return _innerCommand.ExecuteNonQuery();
        }

        /// <inheritdoc />
        public override Task<int> ExecuteNonQueryAsync(
            CancellationToken cancellationToken
        )
        {
            CountSnapshotCreation();

            return _innerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>Records snapshot activation without keeping SQL or relying on elapsed time.</summary>
        private void CountSnapshotCreation()
        {
            if (CommandText.Contains("CREATE TEMPORARY TABLE `__doka_sm_cat_", StringComparison.Ordinal))
            {
                _owner.CatalogSnapshotCreateCount++;
            }
        }

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
                var fieldCount = reader.FieldCount;
                reader.Dispose();

                return CreateFaultReader(fieldCount);
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
                var fieldCount = reader.FieldCount;
                await reader.DisposeAsync();

                return CreateFaultReader(fieldCount);
            }

            return reader;
        }

        /// <summary>Limits malformed results to one real classifier statement in this test connection.</summary>
        private bool ShouldInjectFault(
            DbDataReader reader
        ) => !_owner.FaultWasInjected
            && ((reader.FieldCount is 9 or 10
                    && _owner._fault is CatalogClassificationResultFault.UnsubmittedOrdinal
                        or CatalogClassificationResultFault.MissingRows)
                || (IsPrerequisite(reader)
                    && _owner._fault is CatalogClassificationResultFault.UnsubmittedPrerequisiteOrdinal
                        or CatalogClassificationResultFault.MissingPrerequisiteRows));

        /// <summary>Creates only synthetic ordinal evidence; real provider rows are never copied.</summary>
        private DataTableReader CreateFaultReader(
            int fieldCount
        )
        {
            _owner.FaultWasInjected = true;
            var table = new DataTable();
            table.Columns.Add("ordinal", typeof(int));

            for (var column = 1; column < fieldCount; column++)
            {
                table.Columns.Add($"facet_{column}", typeof(object));
            }

            if (_owner._fault == CatalogClassificationResultFault.UnsubmittedPrerequisiteOrdinal)
            {
                table.Rows.Add(-1, "prerequisite_missing");
            }
            else if (_owner._fault == CatalogClassificationResultFault.UnsubmittedOrdinal)
            {
                var row = table.NewRow();
                row[0] = -1;
                row[1] = "matching";
                row[2] = true;
                row[3] = false;
                if (fieldCount == 10)
                {
                    row[9] = false;
                }

                table.Rows.Add(row);
            }

            return table.CreateDataReader();
        }

        /// <summary>Identifies classifier traffic without parsing or retaining SQL.</summary>
        private void CountClassifier(
            DbDataReader reader
        )
        {
            // WHY: Prerequisite, probe, and diagnostic statements have other
            // shapes. PostgreSQL appends NULL-proof eligibility to the nine
            // existing classifier facets; both complete shapes are counted.
            if (reader.FieldCount is 9 or 10)
            {
                _owner.ClassificationStatementCount++;
                if (CommandText.Contains("`__doka_sm_cat_", StringComparison.Ordinal))
                {
                    _owner.CatalogSnapshotClassificationStatementCount++;
                }
            }
            else if (IsPrerequisite(reader))
            {
                _owner.PrerequisiteStatementCount++;
            }
            else if (reader.FieldCount == 3 && CommandText.Contains("ORDER BY 1;", StringComparison.Ordinal))
            {
                _owner.NarrowingEligibilityStatementCount++;
            }
            else if (reader.FieldCount == 2 && CommandText.Contains("column_store_type", StringComparison.Ordinal))
            {
                _owner.ColumnDiagnosticStatementCount++;
            }
            else if (IsNarrowingDataStatement(CommandText))
            {
                _owner.NarrowingDataStatementCount++;
            }
        }

        private bool IsPrerequisite(DbDataReader reader) => reader.FieldCount == 2
            && CommandText.Contains("THEN 'prerequisite_missing'", StringComparison.Ordinal);

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

    /// <summary>Forwards provider batches while counting only statement categories and bounded cardinalities.</summary>
    private sealed class CountingBatch : DbBatch
    {
        private readonly CatalogClassificationCountingConnection _owner;
        private readonly DbBatch _innerBatch;
        private bool _innerDisposed;

        /// <summary>Creates a counting adapter without recording SQL or values.</summary>
        public CountingBatch(
            CatalogClassificationCountingConnection owner,
            DbBatch innerBatch
        )
        {
            _owner = owner;
            _innerBatch = innerBatch;
        }

        /// <inheritdoc />
        public override int Timeout
        {
            get => _innerBatch.Timeout;
            set => _innerBatch.Timeout = value;
        }

        /// <inheritdoc />
        protected override DbConnection? DbConnection
        {
            get => _owner;
            set => _innerBatch.Connection = value is CatalogClassificationCountingConnection wrapper
                ? wrapper._inner
                : value;
        }

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction
        {
            get => _innerBatch.Transaction;
            set => _innerBatch.Transaction = value;
        }

        /// <inheritdoc />
        protected override DbBatchCommandCollection DbBatchCommands => _innerBatch.BatchCommands;

        /// <inheritdoc />
        protected override DbBatchCommand CreateDbBatchCommand() => _innerBatch.CreateBatchCommand();

        /// <inheritdoc />
        public override void Cancel() => _innerBatch.Cancel();

        /// <inheritdoc />
        public override void Prepare() => _innerBatch.Prepare();

        /// <inheritdoc />
        public override Task PrepareAsync(CancellationToken cancellationToken = default)
            => _innerBatch.PrepareAsync(cancellationToken);

        /// <inheritdoc />
        public override int ExecuteNonQuery() => _innerBatch.ExecuteNonQuery();

        /// <inheritdoc />
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
            => _innerBatch.ExecuteNonQueryAsync(cancellationToken);

        /// <inheritdoc />
        public override object? ExecuteScalar() => _innerBatch.ExecuteScalar();

        /// <inheritdoc />
        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
            => _innerBatch.ExecuteScalarAsync(cancellationToken);

        /// <inheritdoc />
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            CountStatements();

            return _innerBatch.ExecuteReader(behavior);
        }

        /// <inheritdoc />
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken
        )
        {
            CountStatements();

            return _innerBatch.ExecuteReaderAsync(behavior, cancellationToken);
        }

        private void CountStatements()
        {
            _owner.LargestNativeBatchStatementCount = Math.Max(
                _owner.LargestNativeBatchStatementCount, _innerBatch.BatchCommands.Count);

            var narrowing = 0;
            foreach (var command in _innerBatch.BatchCommands)
            {
                if (IsNarrowingDataStatement(command.CommandText))
                {
                    narrowing++;
                }
            }

            if (narrowing > 0)
            {
                _owner.NativeNarrowingBatchExecutionCount++;
                _owner.NativeNarrowingStatementCount += narrowing;
            }
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (!_innerDisposed)
            {
                _innerDisposed = true;
                _innerBatch.Dispose();
            }

            base.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (!_innerDisposed)
                {
                    // WHY: Base asynchronous disposal calls this wrapper's
                    // synchronous override. Establish ownership before awaiting.
                    _innerDisposed = true;
                    await _innerBatch.DisposeAsync();
                }
            }
            finally
            {
                await base.DisposeAsync();
                GC.SuppressFinalize(this);
            }
        }
    }
}
