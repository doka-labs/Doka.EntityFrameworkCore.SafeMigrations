namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlCatalogSnapshotTests
{
    /// <summary>Models session-owned temporary tables and deterministic command failures without a database.</summary>
    private sealed class SnapshotConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;

        /// <summary>Gets the temporary tables currently owned by the simulated session.</summary>
        public HashSet<string> Tables { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the ordered executed commands and their timeout and cancellation settings.</summary>
        public List<(string Sql, int Timeout, CancellationToken Token)> Commands { get; } = [];

        /// <summary>Gets or sets the failure injected for an execution ordinal and SQL text.</summary>
        public Func<int, string, Exception?>? Failure { get; set; }

        /// <summary>Gets or sets a callback that changes session state immediately before an execution.</summary>
        public Action<int, string>? BeforeExecute { get; set; }

        /// <summary>Gets or sets the failure injected after execution when its command wrapper is disposed.</summary>
        public Func<string, Exception?>? DisposalFailure { get; set; }

        /// <inheritdoc />
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        /// <inheritdoc />
        public override string Database => "catalog_snapshot_tests";

        /// <inheritdoc />
        public override string DataSource => "test";

        /// <inheritdoc />
        public override string ServerVersion => "11.8.8-MariaDB";

        /// <inheritdoc />
        public override ConnectionState State => _state;

        /// <inheritdoc />
        public override void ChangeDatabase(
            string databaseName
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Close()
        {
            _state = ConnectionState.Closed;
            Tables.Clear();
        }

        /// <inheritdoc />
        public override Task CloseAsync()
        {
            Close();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override void Open() => _state = ConnectionState.Open;

        /// <inheritdoc />
        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbCommand CreateDbCommand() => new SnapshotCommand(this);

        /// <summary>Reads the first delimited table name from a snapshot CREATE or DROP command.</summary>
        /// <param name="sql">The generated command text.</param>
        /// <returns>The unquoted temporary table name.</returns>
        public static string ReadTableName(
            string sql
        )
        {
            var start = sql.IndexOf('`');
            var end = sql.IndexOf('`', start + 1);

            return sql[(start + 1)..end];
        }

        /// <summary>Applies one scripted command while retaining failure and cancellation ordering.</summary>
        private int Execute(
            SnapshotCommand command,
            CancellationToken cancellationToken
        )
        {
            Assert.Equal(ConnectionState.Open, State);
            var sql = command.CommandText;
            Commands.Add((sql, command.CommandTimeout, cancellationToken));
            BeforeExecute?.Invoke(Commands.Count, sql);
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure?.Invoke(Commands.Count, sql) is { } failure)
            {
                throw failure;
            }

            if (sql.StartsWith("CREATE TEMPORARY TABLE ", StringComparison.Ordinal))
            {
                if (!Tables.Add(ReadTableName(sql)))
                {
                    throw CreateServerException(MySqlErrorCode.TableExists);
                }
            }
            else
            {
                Assert.StartsWith("DROP TEMPORARY TABLE IF EXISTS ", sql, StringComparison.Ordinal);
                foreach (var name in sql["DROP TEMPORARY TABLE IF EXISTS ".Length..].TrimEnd(';').Split(','))
                {
                    Assert.True(Tables.Remove(name.Trim().Trim('`')));
                }
            }

            return 0;
        }

        /// <summary>Forwards nonquery execution to the simulated session and rejects unused command APIs.</summary>
        private sealed class SnapshotCommand : DbCommand
        {
            private readonly SnapshotConnection _connection;

            /// <summary>Initializes a command bound to its simulated owning connection.</summary>
            /// <param name="connection">The session that owns execution and failure injection.</param>
            public SnapshotCommand(
                SnapshotConnection connection
            )
            {
                _connection = connection;
                DbConnection = connection;
            }

            /// <inheritdoc />
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string CommandText { get; set; } = string.Empty;

            /// <inheritdoc />
            public override int CommandTimeout { get; set; } = 30;

            /// <inheritdoc />
            public override CommandType CommandType { get; set; } = CommandType.Text;

            /// <inheritdoc />
            public override bool DesignTimeVisible { get; set; }

            /// <inheritdoc />
            public override UpdateRowSource UpdatedRowSource { get; set; }

            /// <inheritdoc />
            protected override DbConnection? DbConnection { get; set; }

            /// <inheritdoc />
            protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

            /// <inheritdoc />
            protected override DbTransaction? DbTransaction { get; set; }

            /// <inheritdoc />
            public override void Cancel() => throw new NotSupportedException();

            /// <inheritdoc />
            public override int ExecuteNonQuery() => _connection.Execute(this, CancellationToken.None);

            /// <inheritdoc />
            public override Task<int> ExecuteNonQueryAsync(
                CancellationToken cancellationToken
            ) => Task.FromResult(_connection.Execute(this, cancellationToken));

            /// <inheritdoc />
            public override object? ExecuteScalar() => throw new NotSupportedException();

            /// <inheritdoc />
            public override ValueTask DisposeAsync()
            {
                if (_connection.DisposalFailure?.Invoke(CommandText) is { } failure)
                {
                    return ValueTask.FromException(failure);
                }

                return base.DisposeAsync();
            }

            /// <inheritdoc />
            public override void Prepare() => throw new NotSupportedException();

            /// <inheritdoc />
            protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

            /// <inheritdoc />
            protected override DbDataReader ExecuteDbDataReader(
                CommandBehavior behavior
            ) => throw new NotSupportedException();
        }
    }
}
