namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies SafeMigrations Catalog Batch Integration contracts in independently owned test state.</summary>
public sealed class SafeMigrationCatalogBatchIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Initializes the fixture with its configured dependencies and ownership boundary.</summary>
    public SafeMigrationCatalogBatchIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Connection Without Batch Support Executes Commands Sequentially.</summary>
    [Fact]
    public async Task ConnectionWithoutBatchSupport_ExecutesCommandsSequentially()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new NonBatchingDbConnection(new NpgsqlConnection(connectionString));
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: 19);

        AddScalarCommand(batch, 17);
        AddScalarCommand(batch, 29);
        var discarded = batch.CreateCommand();
        discarded.CommandText = "SELECT 41;";
        batch.RemoveLastCommand(discarded);

        var values = new List<int>();

        // Act
        await batch.ForEachResultSetAsync(
            async (reader,
                cancellationToken) =>
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    values.Add(reader.GetInt32(0));
                }
            },
            CancellationToken.None);

        // Assert
        Assert.False(connection.CanCreateBatch);
        Assert.Equal(2, batch.Count);
        Assert.Equal([17, 29], values);
    }

    /// <summary>Connection Without Batch Support Propagates Cancellation.</summary>
    [Fact]
    public async Task ConnectionWithoutBatchSupport_PropagatesCancellation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var nativeConnection = new NpgsqlConnection(connectionString);
        await using var connection = new NonBatchingDbConnection(nativeConnection);
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: 19);
        var command = batch.CreateCommand();
        command.CommandText = "SELECT pg_catalog.pg_advisory_xact_lock(171072);";
        var observerConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        }.ConnectionString;

        await using var observer = new NpgsqlConnection(observerConnectionString);
        await observer.OpenAsync(CancellationToken.None);
        await using var rendezvous = observer.CreateCommand();
        rendezvous.CommandText = "SELECT pg_catalog.pg_advisory_lock(171072);";
        await rendezvous.ExecuteNonQueryAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Exception? failure;
        Exception? primaryFailure = null;

        // Act
        var execution = batch.ForEachResultSetAsync(static (_, _) => Task.CompletedTask, cancellation.Token);
        try
        {
            await PostgreSqlNativeAdmission.WaitForAdvisoryLockAsync(
                observer,
                nativeConnection.ProcessID,
                execution,
                CancellationToken.None);

            await cancellation.CancelAsync();
            failure = await Record.ExceptionAsync(() => execution);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            rendezvous.CommandText = "SELECT pg_catalog.pg_advisory_unlock(171072);";
            await PostgreSqlNativeAdmission.CompleteAsync(
                primaryFailure,
                execution,
                observer,
                rendezvous,
                cancellation);
        }

        await using var reuse = nativeConnection.CreateCommand();
        reuse.CommandText = "SELECT 17;";
        var reusedValue = await reuse.ExecuteScalarAsync(CancellationToken.None);

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(PostgresErrorCodes.QueryCanceled,
            Assert.IsType<PostgresException>(canceled.InnerException)
                .SqlState);

        Assert.Equal(17, reusedValue);
    }

    /// <summary>Pre-canceled execution remains a distinct case from cancellation after native admission.</summary>
    [Fact]
    public async Task ConnectionWithoutBatchSupport_RejectsPreCanceledExecution()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new NonBatchingDbConnection(new NpgsqlConnection(connectionString));
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: 19);
        batch.CreateCommand()
            .CommandText = "SELECT 1 / 0;";

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var failure = await Record.ExceptionAsync(() => batch.ForEachResultSetAsync(
            static (_, _) => throw new InvalidOperationException("Pre-canceled execution must not admit a reader."),
            cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
    }

    private static void AddScalarCommand(
        SafeMigrationCatalogBatch batch,
        int value
    )
    {
        var command = batch.CreateCommand();
        var parameter = command.CreateParameter();
        parameter.ParameterName = "value";
        parameter.Value = value;
        command.Parameters.Add(parameter);
        command.CommandText = "SELECT @value::integer;";

        Assert.Equal(19, command.SequentialCommand!.CommandTimeout);
    }

    private sealed class NonBatchingDbConnection : DbConnection
    {
        private readonly NpgsqlConnection _inner;

        public NonBatchingDbConnection(
            NpgsqlConnection inner
        )
        {
            _inner = inner;
        }

        [AllowNull]
        public override string ConnectionString
        {
            get => _inner.ConnectionString;
            set => _inner.ConnectionString = value ?? string.Empty;
        }

        public override string Database => _inner.Database;

        public override string DataSource => _inner.DataSource;

        public override string ServerVersion => _inner.ServerVersion;

        public override ConnectionState State => _inner.State;

        public override void ChangeDatabase(
            string databaseName
        ) => _inner.ChangeDatabase(databaseName);

        public override void Close() => _inner.Close();

        public override void Open() => _inner.Open();

        public override Task OpenAsync(
            CancellationToken cancellationToken
        ) => _inner.OpenAsync(cancellationToken);

        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel
        ) => _inner.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() => _inner.CreateCommand();

        protected override void Dispose(
            bool disposing
        )
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
