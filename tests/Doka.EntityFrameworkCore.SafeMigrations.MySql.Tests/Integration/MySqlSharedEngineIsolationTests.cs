namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Proves independent class owners overlap on one engine without sharing database or login state.</summary>
public sealed class MySqlSharedEngineIsolationTests : MySqlIntegrationTestBase
{
    /// <summary>Uses the assembly-owned native fixture for the isolation counterprobe.</summary>
    /// <param name="fixture">The initialized shared engine.</param>
    public MySqlSharedEngineIsolationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    /// <summary>Two distinct class owners must see their own identical table names and retain independent cleanup.</summary>
    [Fact]
    public async Task TwoConcurrentClassOwnersHaveDistinctDatabaseAndLeastPrivilegeBoundaries()
    {
        // Arrange
        var first = new FirstDatabaseOwner(Fixture);
        var second = new SecondDatabaseOwner(Fixture);
        var rendezvous = new DatabaseRendezvous();

        try
        {
            // Act
            var results = await Task.WhenAll(
                first.SeedAndObserveAsync(11, rendezvous),
                second.SeedAndObserveAsync(22, rendezvous));
            var firstLogin = await Fixture.CreateLeastPrivilegeConnectionStringAsync(results[0].ConnectionString,
                CancellationToken.None);
            var secondLogin = await Fixture.CreateLeastPrivilegeConnectionStringAsync(results[1].ConnectionString,
                CancellationToken.None);
            var foreignLogin = new MySqlConnectionStringBuilder(firstLogin)
            {
                Database = new MySqlConnectionStringBuilder(secondLogin).Database,
            }.ConnectionString;
            var denied = await Record.ExceptionAsync(() => ScalarIntAsync(foreignLogin,
                "SELECT `value` FROM `parallel_owner_probe`;"));
            var foreignLease = new MySqlConnectionStringBuilder(results[0].ConnectionString)
            {
                Database = "bootstrap",
            }.ConnectionString;
            var rejectedLease = await Record.ExceptionAsync(() => Fixture.ReleaseDatabaseAsync(foreignLease,
                CancellationToken.None));
            using var cancelledCleanup = new CancellationTokenSource();
            cancelledCleanup.Cancel();
            var cancelledRelease = await Record.ExceptionAsync(() => Fixture.ReleaseDatabaseAsync(
                results[0].ConnectionString, cancelledCleanup.Token));
            var retainedValue = await ScalarIntAsync(firstLogin, "SELECT `value` FROM `parallel_owner_probe`;");
            await first.DisposeAsync();
            var survivingValue = await ScalarIntAsync(secondLogin, "SELECT `value` FROM `parallel_owner_probe`;");
            var released = await ScalarIntAsync(results[1].ConnectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '"
                + new MySqlConnectionStringBuilder(results[0].ConnectionString).Database + "';");
            var releasedLogin = await Record.ExceptionAsync(() => ScalarIntAsync(firstLogin, "SELECT 1;"));

            // Assert
            Assert.NotEqual(first.GetType(), second.GetType());
            Assert.Equal(2, rendezvous.Arrivals);
            Assert.NotEqual(results[0].Database, results[1].Database);
            Assert.Equal(11, results[0].Value);
            Assert.Equal(22, results[1].Value);
            Assert.NotEqual(new MySqlConnectionStringBuilder(firstLogin).UserID,
                new MySqlConnectionStringBuilder(secondLogin).UserID);
            Assert.Equal(1044, Assert.IsType<MySqlException>(denied).Number);
            Assert.IsType<ArgumentException>(rejectedLease);
            Assert.IsAssignableFrom<OperationCanceledException>(cancelledRelease);
            Assert.Equal(11, retainedValue);
            Assert.Equal(22, survivingValue);
            Assert.Equal(0, released);
            Assert.IsType<MySqlException>(releasedLogin);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    private abstract class DatabaseOwner(MySqlEngineContainerFixture fixture) : MySqlIntegrationTestBase(fixture)
    {
        internal async Task<DatabaseObservation> SeedAndObserveAsync(int value, DatabaseRendezvous rendezvous)
        {
            var connectionString = string.Empty;

            try
            {
                connectionString = await CreateDatabaseAsync(CancellationToken.None);
                await ExecuteSqlAsync(connectionString, "CREATE TABLE `parallel_owner_probe` (`value` int NOT NULL); "
                    + "INSERT INTO `parallel_owner_probe` VALUES (" + value.ToString(CultureInfo.InvariantCulture) + ");");
            }
            finally
            {
                // WHY: Both owners must remain alive together; a failing setup still releases its waiting peer.
                rendezvous.Arrive();
            }

            await rendezvous.Ready;

            return new DatabaseObservation(connectionString,
                await ScalarStringAsync(connectionString, "SELECT DATABASE();"),
                await ScalarIntAsync(connectionString, "SELECT `value` FROM `parallel_owner_probe`;"));
        }
    }

    private sealed class FirstDatabaseOwner(MySqlEngineContainerFixture fixture) : DatabaseOwner(fixture);

    private sealed class SecondDatabaseOwner(MySqlEngineContainerFixture fixture) : DatabaseOwner(fixture);

    private sealed record DatabaseObservation(string ConnectionString, string Database, int Value);

    private sealed class DatabaseRendezvous
    {
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        internal int Arrivals => Volatile.Read(ref _arrivals);

        internal Task Ready => _ready.Task;

        internal void Arrive()
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _ready.SetResult(true);
            }
        }
    }
}
