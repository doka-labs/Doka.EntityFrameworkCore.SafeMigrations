namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies test-local database cleanup independently of the shared fixture's final sweep.</summary>
public sealed class SqlServerTestLocalCleanupTests
{
    private const string RootConnectionString =
        "Server=127.0.0.1,1433;Database=master;Integrated Security=True;Pooling=False;TrustServerCertificate=True;";

    /// <summary>
    /// Attempts every local lease while retaining exact failures and leaving peer ownership untouched.
    /// </summary>
    [Fact]
    public async Task TestCleanup_AttemptsEveryOwnedDatabaseAndAggregatesFailures()
    {
        // Arrange
        var created = new List<string>();
        var attempts = new List<string>();
        var failures = new Dictionary<string, Exception>();
        var firstFailure = new InvalidOperationException("Injected first local cleanup failure.");
        var lastFailure = new InvalidOperationException("Injected last local cleanup failure.");
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (database, _) =>
        {
            created.Add(database);

            return Task.CompletedTask;
        }, (database, _) =>
        {
            attempts.Add(database);

            return failures.TryGetValue(database, out var failure)
                ? Task.FromException(failure)
                : Task.CompletedTask;
        }, () => Task.CompletedTask);

        var owner = new DatabaseOwner(fixture);
        var peer = new DatabaseOwner(fixture);
        await owner.CreateAsync();
        await owner.CreateAsync();
        await owner.CreateAsync();
        await peer.CreateAsync();
        failures.Add(created[0], firstFailure);
        failures.Add(created[2], lastFailure);

        // Act
        var observed = await Record.ExceptionAsync(owner.DisposeAsync);

        // Assert
        var aggregate = Assert.IsType<AggregateException>(observed);

        Assert.Collection(aggregate.InnerExceptions,
            exception => Assert.Same(firstFailure, exception),
            exception => Assert.Same(lastFailure, exception));
        Assert.Equal(created.Take(3), attempts);
        Assert.DoesNotContain(created[3], attempts);
    }

    /// <summary>Retries only the failed local lease after successful releases have transferred ownership.</summary>
    [Fact]
    public async Task TestCleanup_RetryDoesNotRepeatSuccessfulLease()
    {
        // Arrange
        var created = new List<string>();
        var attempts = new List<string>();
        var expected = new InvalidOperationException("Injected retryable local cleanup failure.");
        var failFirstAttempt = true;
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (database, _) =>
        {
            created.Add(database);

            return Task.CompletedTask;
        }, (database, _) =>
        {
            attempts.Add(database);

            if (failFirstAttempt)
            {
                failFirstAttempt = false;

                return Task.FromException(expected);
            }

            return Task.CompletedTask;
        }, () => Task.CompletedTask);

        var owner = new DatabaseOwner(fixture);
        await owner.CreateAsync();
        await owner.CreateAsync();

        // Act
        var observed = await Record.ExceptionAsync(owner.DisposeAsync);
        await owner.DisposeAsync();

        // Assert
        var aggregate = Assert.IsType<AggregateException>(observed);

        Assert.Same(expected, Assert.Single(aggregate.InnerExceptions));
        Assert.Equal([created[0], created[1], created[0]], attempts);
    }

    /// <summary>Leaves a failed lease available for the shared fixture's final retry instead of losing it.</summary>
    [Fact]
    public async Task TestCleanup_FailedLeaseRemainsOwnedForFinalFixtureRetry()
    {
        // Arrange
        var created = new List<string>();
        var attempts = new List<string>();
        var expected = new InvalidOperationException("Injected failure before final fixture cleanup.");
        var failFirstAttempt = true;
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (database, _) =>
        {
            created.Add(database);

            return Task.CompletedTask;
        }, (database, _) =>
        {
            attempts.Add(database);

            if (failFirstAttempt)
            {
                failFirstAttempt = false;

                return Task.FromException(expected);
            }

            return Task.CompletedTask;
        }, () => Task.CompletedTask);

        var owner = new DatabaseOwner(fixture);
        await owner.CreateAsync();
        await owner.CreateAsync();

        // Act
        var observed = await Record.ExceptionAsync(owner.DisposeAsync);
        await fixture.DisposeAsync();

        // Assert
        var aggregate = Assert.IsType<AggregateException>(observed);

        Assert.Same(expected, Assert.Single(aggregate.InnerExceptions));
        Assert.Equal([created[0], created[1], created[0]], attempts);
    }

    /// <summary>
    /// Repeated successful local cleanup neither retries completed leases nor touches an active peer.
    /// </summary>
    [Fact]
    public async Task TestCleanup_SuccessCanBeRepeatedWithoutDroppingPeer()
    {
        // Arrange
        var created = new List<string>();
        var dropped = new List<string>();
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (database, _) =>
        {
            created.Add(database);

            return Task.CompletedTask;
        }, (database, _) =>
        {
            dropped.Add(database);

            return Task.CompletedTask;
        }, () => Task.CompletedTask);

        var owner = new DatabaseOwner(fixture);
        var peer = new DatabaseOwner(fixture);
        await owner.CreateAsync();
        await peer.CreateAsync();

        // Act
        await owner.DisposeAsync();
        await owner.DisposeAsync();
        await peer.CreateAsync();

        // Assert
        Assert.Equal(3, created.Count);
        Assert.Equal([created[0]], dropped);
        Assert.DoesNotContain(created[1], dropped);
        Assert.DoesNotContain(created[2], dropped);
    }

    /// <summary>An empty local owner cannot trigger the shared fixture's database sweep.</summary>
    [Fact]
    public async Task TestCleanup_EmptyOwnerDoesNotDropFixtureDatabases()
    {
        // Arrange
        var dropped = new List<string>();
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (_, _) => Task.CompletedTask,
            (database, _) =>
            {
                dropped.Add(database);

                return Task.CompletedTask;
            }, () => Task.CompletedTask);

        var owner = new DatabaseOwner(fixture);
        var peer = new DatabaseOwner(fixture);
        await peer.CreateAsync();

        // Act
        await owner.DisposeAsync();

        // Assert
        Assert.Empty(dropped);
    }

    /// <summary>Exposes the actual base ownership path without constructing a native server fixture.</summary>
    private sealed class DatabaseOwner(SqlServerContainerFixture fixture) : SqlServerIntegrationTestBase(fixture)
    {
        /// <summary>Creates a database lease through the same path used by live test classes.</summary>
        /// <returns>The unpooled connection string owned by this test instance.</returns>
        public Task<string> CreateAsync() => CreateDatabaseAsync();
    }
}
