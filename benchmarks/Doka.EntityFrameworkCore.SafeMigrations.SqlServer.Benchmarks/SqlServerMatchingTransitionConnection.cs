namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks;

/// <summary>Supplies the matching nine-column catalog contract without a native connection or test assembly.</summary>
internal sealed class SqlServerMatchingTransitionConnection : DbConnection
{
    /// <summary>Gets or sets the number of statements dispatched by the current benchmark invocation.</summary>
    internal int StatementCount { get; set; }

    /// <inheritdoc />
    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    /// <inheritdoc />
    public override string Database => "matching_transition_capture";

    /// <inheritdoc />
    public override string DataSource => "managed-only";

    /// <inheritdoc />
    public override string ServerVersion => "16.0";

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
    ) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new TransitionCommand(this);

    private sealed class TransitionCommand : DbCommand
    {
        private readonly SqlServerMatchingTransitionConnection _connection;
        private readonly SqlCommand _parameterHost = new();
        private DataSet? _results;

        /// <summary>Owns the provider parameter host and synthetic result sets for one submitted command.</summary>
        internal TransitionCommand(
            SqlServerMatchingTransitionConnection connection
        )
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
        protected override DbConnection? DbConnection { get; set; }

        /// <inheritdoc />
        protected override DbTransaction? DbTransaction { get; set; }

        /// <inheritdoc />
        protected override DbParameterCollection DbParameterCollection => _parameterHost.Parameters;

        /// <inheritdoc />
        protected override DbParameter CreateDbParameter() => new SqlParameter();

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

        /// <summary>Returns one exact classifier set per delayed ordinal and empty dependency sets.</summary>
        private DataTableReader Read(
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            _connection.StatementCount++;
            _results = new DataSet { Locale = CultureInfo.InvariantCulture };
            var ordinals = Regex.Matches(
                CommandText,
                @"@doka_ordinal\s*=\s*(?<parameter>@doka_ordinal\d+)|@doka_ordinal\s*=\s*(?<literal>\d+)");

            if (ordinals.Count == 0)
            {
                // WHY: This fixture has no named dependencies. The dependency query still owns one empty result.
                _results.Tables.Add(new DataTable { Locale = CultureInfo.InvariantCulture });
            }

            foreach (Match match in ordinals)
            {
                var ordinal = match.Groups["parameter"].Success
                    ? (int)_parameterHost.Parameters[match.Groups["parameter"].Value].Value
                    : int.Parse(match.Groups["literal"].Value, CultureInfo.InvariantCulture);

                var table = new DataTable { Locale = CultureInfo.InvariantCulture };
                _results.Tables.Add(table);
                table.Columns.Add("ordinal", typeof(int));
                table.Columns.Add("state", typeof(string));
                table.Columns.Add("postcondition", typeof(int));
                table.Columns.Add("repair", typeof(int));
                table.Columns.Add("code", typeof(string));
                table.Columns.Add("row_evidence", typeof(string));
                table.Columns.Add("dependencies", typeof(string));
                table.Columns.Add("diagnostics", typeof(string));
                table.Columns.Add("matched", typeof(string));
                table.Rows.Add(ordinal,
                    "matching",
                    1,
                    0,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value,
                    DBNull.Value);
            }

            return _results.CreateDataReader();
        }

        /// <inheritdoc />
        protected override void Dispose(
            bool disposing
        )
        {
            try
            {
                if (disposing)
                {
                    _results?.Dispose();
                    _parameterHost.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
