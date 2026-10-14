namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies overlapping work over independent databases on the assembly-owned SQL Server.</summary>
public sealed class SqlServerParallelDatabaseIsolationTests : SqlServerIntegrationTestBase
{
    /// <summary>Creates the concurrency suite with the assembly-owned fixture.</summary>
    /// <param name="fixture">The shared fixture providing isolated databases.</param>
    public SqlServerParallelDatabaseIsolationTests(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>Preserves colliding identifiers and eager cleanup across concurrently opened databases.</summary>
    [SqlServerLiveFact]
    public async Task ConcurrentDatabases_PreserveIndependentRowsAndOwnedCleanup()
    {
        // Arrange
        var firstConnectionString = await CreateDatabaseAsync();
        var secondConnectionString = await CreateDatabaseAsync();
        var firstDatabase = new SqlConnectionStringBuilder(firstConnectionString).InitialCatalog;
        var secondDatabase = new SqlConnectionStringBuilder(secondConnectionString).InitialCatalog;
        var bothConnectionsOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;

        // Act
        // WHY: The barrier proves two distinct connections are open together;
        // its timeout bounds a deadlock, not a hardware-dependent performance target.
        var rows = await Task.WhenAll(ApplyAndReadAsync(firstConnectionString, 17),
            ApplyAndReadAsync(secondConnectionString, 29));
        await Fixture.ReleaseDatabaseAsync(firstConnectionString);
        var firstStillExists = await ScalarIntAsync(secondConnectionString,
            "SELECT COUNT(*) FROM sys.databases WHERE name = N'" + firstDatabase + "';");
        var survivingRow = await ScalarIntAsync(secondConnectionString, "SELECT Id FROM dbo.parallel_isolation;");

        // Assert
        Assert.NotEqual(firstDatabase, secondDatabase);
        Assert.Equal(2, openCount);
        Assert.True(bothConnectionsOpen.Task.IsCompletedSuccessfully);
        Assert.Equal([17, 29], rows);
        Assert.Equal(0, firstStillExists);
        Assert.Equal(29, survivingRow);

        async Task<int> ApplyAndReadAsync(string connectionString, int id)
        {
            await using var context = CreateContext(connectionString);
            await context.Database.OpenConnectionAsync();

            if (Interlocked.Increment(ref openCount) == 2)
            {
                bothConnectionsOpen.SetResult();
            }

            await bothConnectionsOpen.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.CreateTableIfNotExists("parallel_isolation",
                table => new { Id = table.Column<int>(type: "int", nullable: false) },
                constraints: table => table.PrimaryKey("PK_parallel_isolation", row => row.Id));

            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
            await context.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.parallel_isolation (Id) VALUES ({id});");

            return await ScalarIntAsync(connectionString, "SELECT Id FROM dbo.parallel_isolation;");
        }
    }
}
