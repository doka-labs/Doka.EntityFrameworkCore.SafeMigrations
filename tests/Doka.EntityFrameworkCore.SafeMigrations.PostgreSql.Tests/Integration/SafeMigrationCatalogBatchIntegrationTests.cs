namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class SafeMigrationCatalogBatchIntegrationTests : IClassFixture<PostgreSqlContainerFixture>
{
    private readonly PostgreSqlContainerFixture _fixture;

    public SafeMigrationCatalogBatchIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) => _fixture = fixture;

    [Fact]
    public async Task ConnectionWithoutBatchSupport_ExecutesCommandsSequentially()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new NonBatchingDbConnection(new NpgsqlConnection(connectionString));
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: 19);

        AddScalarCommand(batch, 17);
        AddScalarCommand(batch, 29);
        var discarded = batch.CreateCommand();
        discarded.CommandText = "SELECT 41;";
        batch.RemoveLastCommand(discarded);

        var values = new List<int>();
        await batch.ForEachResultSetAsync(
            async (
                    reader,
                    cancellationToken
                ) =>
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    values.Add(reader.GetInt32(0));
                }
            },
            CancellationToken.None);

        Assert.False(connection.CanCreateBatch);
        Assert.Equal(2, batch.Count);
        Assert.Equal([17, 29], values);
    }

    [Fact]
    public async Task ConnectionWithoutBatchSupport_PropagatesCancellation()
    {
        var connectionString = await _fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new NonBatchingDbConnection(new NpgsqlConnection(connectionString));
        await connection.OpenAsync(CancellationToken.None);
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: 19);
        var command = batch.CreateCommand();
        command.CommandText = "SELECT pg_catalog.pg_sleep(5);";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            batch.ForEachResultSetAsync(
                static (_, _) => Task.CompletedTask,
                cancellation.Token));
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

    private sealed class NonBatchingDbConnection : System.Data.Common.DbConnection
    {
        private readonly NpgsqlConnection _inner;

        public NonBatchingDbConnection(
            NpgsqlConnection inner
        ) => _inner = inner;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString
        {
            get => _inner.ConnectionString;
            set => _inner.ConnectionString = value ?? string.Empty;
        }

        public override string Database => _inner.Database;

        public override string DataSource => _inner.DataSource;

        public override string ServerVersion => _inner.ServerVersion;

        public override System.Data.ConnectionState State => _inner.State;

        public override void ChangeDatabase(
            string databaseName
        ) => _inner.ChangeDatabase(databaseName);

        public override void Close() => _inner.Close();

        public override void Open() => _inner.Open();

        public override Task OpenAsync(
            CancellationToken cancellationToken
        ) => _inner.OpenAsync(cancellationToken);

        protected override System.Data.Common.DbTransaction BeginDbTransaction(
            System.Data.IsolationLevel isolationLevel
        ) => _inner.BeginTransaction(isolationLevel);

        protected override System.Data.Common.DbCommand CreateDbCommand() => _inner.CreateCommand();

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
