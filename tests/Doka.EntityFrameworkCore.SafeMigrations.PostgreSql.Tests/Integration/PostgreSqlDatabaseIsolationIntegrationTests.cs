namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks overlapping native database work and test-local teardown without a speed threshold.</summary>
public sealed class PostgreSqlDatabaseIsolationIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the assembly-owned native PostgreSQL fixture.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlDatabaseIsolationIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Concurrent owners share SQL identities without leaking data or terminating peer sessions.</summary>
    [Fact]
    public async Task ConcurrentDatabaseOwners_RemainIsolatedDuringEagerCleanup()
    {
        // Arrange
        using var firstOwner = new DatabaseOwner(Fixture);
        using var secondOwner = new DatabaseOwner(Fixture);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var bothWriting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        try
        {
            var connectionStrings = await Task.WhenAll(firstOwner.CreateAsync(), secondOwner.CreateAsync());

            // Act
            var values = await Task.WhenAll(WriteAndReadAsync(connectionStrings[0], 17),
                WriteAndReadAsync(connectionStrings[1], 29));
            await using var survivor = new NpgsqlConnection(connectionStrings[1]);
            await survivor.OpenAsync(deadline.Token);
            await firstOwner.DisposeAsync();
            await using var survivingQuery = survivor.CreateCommand();
            survivingQuery.CommandText = "SELECT value FROM parallel_owner;";
            var remainingValue = Convert.ToInt32(await survivingQuery.ExecuteScalarAsync(deadline.Token),
                CultureInfo.InvariantCulture);
            var firstDatabaseName = new NpgsqlConnectionStringBuilder(connectionStrings[0]).Database;
            await using var absenceQuery = survivor.CreateCommand();
            absenceQuery.CommandText = "SELECT COUNT(*) FROM pg_catalog.pg_database WHERE datname = @database;";
            absenceQuery.Parameters.AddWithValue("database", firstDatabaseName
                ?? throw new InvalidOperationException("The first owner has no database name."));
            var releasedDatabaseCount = Convert.ToInt32(await absenceQuery.ExecuteScalarAsync(deadline.Token),
                CultureInfo.InvariantCulture);

            // Assert
            Assert.NotEqual(connectionStrings[0], connectionStrings[1]);
            Assert.Equal(2, Volatile.Read(ref ready));
            Assert.Equal([17, 29], values);
            Assert.Equal(29, remainingValue);
            Assert.Equal(0, releasedDatabaseCount);
        }
        finally
        {
            try
            {
                await firstOwner.DisposeAsync();
            }
            finally
            {
                await secondOwner.DisposeAsync();
            }
        }

        async Task<int> WriteAndReadAsync(string connectionString, int value)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(deadline.Token);
            await using var setup = connection.CreateCommand();
            setup.CommandText = "CREATE TABLE parallel_owner (value integer NOT NULL); "
                + "INSERT INTO parallel_owner (value) VALUES (@value);";
            setup.Parameters.AddWithValue("value", value);
            await setup.ExecuteNonQueryAsync(deadline.Token);

            // WHY: Both sessions must be alive after writes before either read is allowed;
            // the barrier proves overlap without measuring machine-dependent elapsed time.
            if (Interlocked.Increment(ref ready) == 2)
            {
                bothWriting.TrySetResult(true);
            }

            await bothWriting.Task.WaitAsync(deadline.Token);
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT value FROM parallel_owner;";
            return Convert.ToInt32(await query.ExecuteScalarAsync(deadline.Token), CultureInfo.InvariantCulture);
        }
    }

    private sealed class DatabaseOwner(
        PostgreSqlContainerFixture fixture
    ) : PostgreSqlIntegrationTestBase(fixture)
    {
        public Task<string> CreateAsync() => CreateDatabaseAsync(CancellationToken.None);
    }
}
