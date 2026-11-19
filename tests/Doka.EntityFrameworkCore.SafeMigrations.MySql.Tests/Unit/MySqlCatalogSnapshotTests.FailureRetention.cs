namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlCatalogSnapshotTests
{
    /// <summary>Native CREATE failure survives command disposal when diagnostic evidence cannot be attached.</summary>
    /// <param name="throwing">Whether Data throws on access instead of exposing a read-only dictionary.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotCreation_HostileDataCannotReplaceExecutionFailure(
        bool throwing
    )
    {
        // Arrange
        var primary = new MySqlCleanupEvidenceException(throwing);
        var secondary = new InvalidOperationException("Native snapshot command disposal failed.");
        await using var connection = new SnapshotConnection
        {
            Failure = (_, _) => primary,
            DisposalFailure = _ => secondary,
        };

        // Act
        var failure = await Record.ExceptionAsync(() =>
            MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        // Assert
        Assert.Same(primary, failure);
        Assert.Equal(1, primary.DataAccesses);
        Assert.Single(connection.Commands);
        Assert.Empty(connection.Tables);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Actual failed snapshot disposal cannot replace an already established analysis failure.</summary>
    /// <param name="throwing">Whether Data throws on access instead of exposing a read-only dictionary.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotFailureCleanup_HostileDataCannotReplacePrimaryFailure(
        bool throwing
    )
    {
        // Arrange
        var primary = new MySqlCleanupEvidenceException(throwing);
        var secondary = new InvalidOperationException("Native snapshot DROP failed.");
        await using var connection = new SnapshotConnection();
        await using var snapshot = Assert.IsType<MySqlCatalogSnapshot>(
            await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        connection.Failure = (_, sql) => sql.StartsWith("DROP TEMPORARY TABLE", StringComparison.Ordinal)
            ? secondary
            : null;

        bool? cleanupSucceeded = null;

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            cleanupSucceeded = await snapshot.DisposeAfterFailureAsync(primary);
        });

        // Assert
        Assert.Null(failure);
        Assert.False(cleanupSucceeded);
        Assert.Equal(1, primary.DataAccesses);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(connection.Tables);
        Assert.Single(connection.Commands,
            static command =>
                command.Sql.StartsWith("DROP TEMPORARY TABLE", StringComparison.Ordinal));
    }

    /// <summary>
    /// A failed quarantine close retains the original DROP failure even when Data rejects its evidence.
    /// </summary>
    /// <param name="throwing">Whether Data throws on access instead of exposing a read-only dictionary.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotQuarantine_HostileDataCannotReplaceOriginalDropFailure(
        bool throwing
    )
    {
        // Arrange
        var primary = new MySqlCleanupEvidenceException(throwing);
        await using var inner = new SnapshotConnection();
        await using var connection = new FailedSnapshotCloseConnection(inner);
        await using var snapshot = Assert.IsType<MySqlCatalogSnapshot>(
            await MySqlCatalogSnapshot.TryCreateAsync(connection, null, CancellationToken.None));

        inner.Failure = (_, sql) => sql.StartsWith("DROP TEMPORARY TABLE", StringComparison.Ordinal)
            ? primary
            : null;

        // Act
        var failure = await Record.ExceptionAsync(async () => await snapshot.DisposeAsync());

        // Assert
        Assert.Same(primary, failure);
        Assert.Equal(1, primary.DataAccesses);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Equal(9, inner.Tables.Count);
    }

    /// <summary>Injects only quarantine close failure while retaining the existing actual snapshot fake.</summary>
    private sealed class FailedSnapshotCloseConnection : DbConnection
    {
        private readonly SnapshotConnection _inner;

        /// <summary>Borrows the fake session whose command, ownership and DROP behavior remain unchanged.</summary>
        /// <param name="inner">The existing deterministic snapshot session.</param>
        internal FailedSnapshotCloseConnection(
            SnapshotConnection inner
        )
        {
            _inner = inner;
        }

        /// <summary>Gets actual quarantine close attempts, excluding the test owner's final disposal.</summary>
        internal int CloseAttempts { get; private set; }

        /// <inheritdoc />
        [AllowNull]
        public override string ConnectionString
        {
            get => _inner.ConnectionString;
            set => _inner.ConnectionString = value;
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
        public override void Open() => _inner.Open();

        /// <inheritdoc />
        public override void Close() => _inner.Close();

        /// <inheritdoc />
        public override Task CloseAsync()
        {
            CloseAttempts++;

            return Task.FromException(new InvalidOperationException("Native snapshot quarantine close failed."));
        }

        /// <inheritdoc />
        public override void ChangeDatabase(
            string databaseName
        ) => _inner.ChangeDatabase(databaseName);

        /// <inheritdoc />
        protected override DbCommand CreateDbCommand() => _inner.CreateCommand();

        /// <inheritdoc />
        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel
        ) => throw new NotSupportedException();
    }
}
