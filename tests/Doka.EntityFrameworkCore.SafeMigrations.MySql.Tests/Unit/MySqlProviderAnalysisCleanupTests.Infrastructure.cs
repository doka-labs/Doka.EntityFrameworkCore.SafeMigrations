namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Creates real provider services while borrowing the deterministic test connection.</summary>
    /// <param name="connection">The connection whose lifetime remains owned by the test.</param>
    /// <returns>A context configured for catalog analysis without a live server.</returns>
    private static DbContext CreateContext(
        DbConnection connection
    )
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql(connection.ConnectionString, MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations()
            .Options;

        var context = new DbContext(options);
        context.Database.SetDbConnection(connection);

        return context;
    }

    /// <summary>Creates one catalog-only operation without prerequisite or physical-key probes.</summary>
    /// <param name="context">The context supplying the configured provider identity.</param>
    /// <returns>The single table-drop analysis operation.</returns>
    private static SafeMigrationOperation[] CreateOperations(
        DbContext context
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("items");

        return builder.Operations.Cast<SafeMigrationOperation>().ToArray();
    }

    /// <summary>Injects lifecycle faults while delegating catalog responses to the shared deterministic transport.</summary>
    private sealed class AnalysisConnection : DbConnection
    {
        private readonly CatalogStatementTestConnection _inner;

        /// <summary>Creates a connection with caller-owned or analyzer-owned opening semantics.</summary>
        /// <param name="initiallyOpen">Whether the connection starts inside the caller's open scope.</param>
        public AnalysisConnection(
            bool initiallyOpen
        )
        {
            _inner = new CatalogStatementTestConnection(native: false, ReadClassification)
            {
                Scalar = static sql => sql == "SELECT @@max_allowed_packet;"
                    ? 64L * 1024 * 1024
                    : throw new InvalidOperationException("Unexpected scalar query."),
            };

            if (!initiallyOpen)
            {
                _inner.Close();
            }
        }

        /// <summary>Gets the optional primary failure raised when analysis creates its first command.</summary>
        public Exception? CommandFailure { get; init; }

        /// <summary>Gets the optional secondary failure raised by asynchronous connection closure.</summary>
        public Exception? CloseFailure { get; init; }

        /// <summary>Gets the number of asynchronous close attempts made by the analyzer.</summary>
        public int CloseAttempts { get; private set; }

        /// <summary>Gets the number of classification statements completed before cleanup.</summary>
        public int ClassificationCount { get; private set; }

        /// <inheritdoc />
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } =
            "Server=localhost;Database=analysis_cleanup;AllowUserVariables=true;GuidFormat=Binary16";

        /// <inheritdoc />
        public override string Database => "analysis_cleanup";

        /// <inheritdoc />
        public override string DataSource => "test";

        /// <inheritdoc />
        public override string ServerVersion => "8.4.11";

        /// <inheritdoc />
        public override ConnectionState State => _inner.State;

        /// <inheritdoc />
        public override void Open() => _inner.Open();

        /// <inheritdoc />
        public override void Close() => _inner.Close();

        /// <inheritdoc />
        public override Task CloseAsync()
        {
            CloseAttempts++;
            if (CloseFailure is { } failure)
            {
                return Task.FromException(failure);
            }

            Close();

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override void ChangeDatabase(
            string databaseName
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel
        ) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override DbCommand CreateDbCommand()
        {
            if (CommandFailure is { } failure)
            {
                throw failure;
            }

            return _inner.CreateCommand();
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync();
            }
            finally
            {
                await base.DisposeAsync();
            }
        }

        /// <summary>Returns the exact classifier shape needed to reach successful analysis cleanup.</summary>
        /// <param name="sql">The submitted classifier statement.</param>
        /// <param name="parameters">The classifier's bound catalog values.</param>
        /// <returns>One matching table classification with no repair or diagnostic evidence.</returns>
        private DataTable ReadClassification(
            string sql,
            IReadOnlyList<DbParameter> parameters
        )
        {
            // WHY: A single DropTable plan needs only a classification row; unexpected additional
            // catalog phases must fail rather than accidentally satisfy the cleanup regression.
            Assert.StartsWith("SELECT 0, (CASE ", sql, StringComparison.Ordinal);
            Assert.NotEmpty(parameters);
            ClassificationCount++;
            var result = new DataTable();
            result.Columns.Add("ordinal", typeof(int));
            result.Columns.Add("state", typeof(string));
            result.Columns.Add("postcondition", typeof(bool));
            result.Columns.Add("repair", typeof(bool));
            result.Columns.Add("code", typeof(string));
            result.Columns.Add("row_evidence", typeof(string));
            result.Columns.Add("dependency_counts", typeof(string));
            result.Columns.Add("diagnostics", typeof(string));
            result.Columns.Add("matched_name", typeof(string));
            result.Rows.Add(0, "matching", false, false, DBNull.Value, DBNull.Value, DBNull.Value,
                DBNull.Value, DBNull.Value);

            return result;
        }
    }
}
