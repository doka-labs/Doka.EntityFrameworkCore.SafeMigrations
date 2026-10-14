namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks database ownership and cleanup without constructing a native container.</summary>
public sealed class PostgreSqlDatabaseOwnershipTests
{
    /// <summary>Releasing one test's GUID database leaves the other database owned until final cleanup.</summary>
    [Fact]
    public async Task ReleaseDatabase_DropsOnlyItsOwnedDatabase()
    {
        // Arrange
        var dropped = new List<string>();
        using var fixture = CreateFixture(dropped);
        var first = await fixture.CreateDatabaseAsync(CancellationToken.None);
        var second = await fixture.CreateDatabaseAsync(CancellationToken.None);
        var firstName = DatabaseName(first);
        var secondName = DatabaseName(second);

        // Act
        await fixture.ReleaseDatabaseAsync(first, CancellationToken.None);
        var droppedBeforeFinalCleanup = dropped.ToArray();
        await fixture.DisposeAsync();

        // Assert
        Assert.NotEqual(firstName, secondName);
        Assert.StartsWith("sm_", firstName, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(firstName[3..], "N", out _));
        Assert.Equal([firstName], droppedBeforeFinalCleanup);
        Assert.Equal([firstName, secondName], dropped);
    }

    /// <summary>Neither a foreign endpoint nor a database outside the ownership ledger may be dropped.</summary>
    /// <param name="foreignEndpoint">Whether to reuse an owned name on a different endpoint.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseDatabase_RejectsForeignOwnership(bool foreignEndpoint)
    {
        // Arrange
        var dropped = new List<string>();
        using var fixture = CreateFixture(dropped);
        var owned = await fixture.CreateDatabaseAsync(CancellationToken.None);
        var foreign = new NpgsqlConnectionStringBuilder(owned);
        if (foreignEndpoint)
        {
            foreign.Host = "another-server";
        }
        else
        {
            foreign.Database = "bootstrap";
        }

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ReleaseDatabaseAsync(foreign.ConnectionString, CancellationToken.None));
        var droppedBeforeFinalCleanup = dropped.ToArray();
        await fixture.DisposeAsync();

        // Assert
        Assert.Contains("not owned", failure.Message, StringComparison.Ordinal);
        Assert.Empty(droppedBeforeFinalCleanup);
        Assert.Equal([DatabaseName(owned)], dropped);
    }

    /// <summary>Failed eager cleanup remains in the fixture ledger and is retried at assembly teardown.</summary>
    [Fact]
    public async Task ReleaseDatabase_RetainsFailedDropForFinalRetry()
    {
        // Arrange
        var attempts = new List<string>();
        using var fixture = new PostgreSqlContainerFixture(
            static (_, _) => Task.CompletedTask,
            (database, _) =>
            {
                attempts.Add(database);
                return attempts.Count == 1
                    ? Task.FromException(new InvalidOperationException("drop sentinel"))
                    : Task.CompletedTask;
            },
            ConnectionString);
        var owned = await fixture.CreateDatabaseAsync(CancellationToken.None);

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ReleaseDatabaseAsync(owned, CancellationToken.None));
        await fixture.DisposeAsync();

        // Assert
        Assert.Equal("drop sentinel", failure.Message);
        Assert.Equal([DatabaseName(owned), DatabaseName(owned)], attempts);
    }

    /// <summary>A failed CREATE acknowledgement retains its provisional name for idempotent final cleanup.</summary>
    /// <param name="physicalCreateSucceeded">Whether the server created the database before acknowledgement failed.</param>
    /// <param name="canceled">Whether the acknowledgement failure is cancellation.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateDatabase_RetainsFailedAttemptForFinalCleanup(
        bool physicalCreateSucceeded,
        bool canceled
    )
    {
        // Arrange
        var attempts = new List<string>();
        var dropped = new List<string>();
        var physicalDatabases = new HashSet<string>(StringComparer.Ordinal);
        Exception sentinel = canceled
            ? new OperationCanceledException("CREATE cancellation sentinel")
            : new InvalidOperationException("CREATE acknowledgement sentinel");
        using var fixture = new PostgreSqlContainerFixture(
            (database, _) =>
            {
                attempts.Add(database);
                if (physicalCreateSucceeded)
                {
                    physicalDatabases.Add(database);
                }

                return Task.FromException(sentinel);
            },
            (database, _) =>
            {
                dropped.Add(database);
                physicalDatabases.Remove(database);
                return Task.CompletedTask;
            },
            ConnectionString);

        // Act
        var failure = await Record.ExceptionAsync(() => fixture.CreateDatabaseAsync(CancellationToken.None));
        var physicalDatabasesBeforeCleanup = physicalDatabases.ToArray();
        await fixture.DisposeAsync();

        // Assert
        Assert.Same(sentinel, failure);
        Assert.Single(attempts);
        Assert.Equal(physicalCreateSucceeded ? 1 : 0, physicalDatabasesBeforeCleanup.Length);
        Assert.Equal(attempts, dropped);
        Assert.Empty(physicalDatabases);
    }

    /// <summary>A connection-string factory failure must happen before any physical database creation.</summary>
    [Fact]
    public async Task CreateDatabase_FactoryFailureCannotCreateAnUntrackedDatabase()
    {
        // Arrange
        var creations = 0;
        var drops = 0;
        var preparations = 0;
        var sentinel = new InvalidOperationException("connection factory sentinel");
        using var fixture = new PostgreSqlContainerFixture(
            (_, _) =>
            {
                creations++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                drops++;
                return Task.CompletedTask;
            },
            _ =>
            {
                preparations++;
                throw sentinel;
            });

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateDatabaseAsync(CancellationToken.None));
        await fixture.DisposeAsync();

        // Assert
        Assert.Same(sentinel, failure);
        Assert.Equal(1, preparations);
        Assert.Equal(0, creations);
        Assert.Equal(0, drops);
    }

    /// <summary>Cancellation before the lifecycle gate neither registers a lease nor calls creation delegates.</summary>
    [Fact]
    public async Task CreateDatabase_PreCanceledGateDoesNotRegisterOwnership()
    {
        // Arrange
        var preparations = 0;
        var creations = 0;
        var drops = 0;
        using var fixture = new PostgreSqlContainerFixture(
            (_, _) =>
            {
                creations++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                drops++;
                return Task.CompletedTask;
            },
            database =>
            {
                preparations++;
                return ConnectionString(database);
            });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var failure = await Record.ExceptionAsync(() => fixture.CreateDatabaseAsync(cancellation.Token));
        await fixture.DisposeAsync();

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(0, preparations);
        Assert.Equal(0, creations);
        Assert.Equal(0, drops);
    }

    /// <summary>Database and container cleanup failures both survive while every owned database is attempted.</summary>
    [Fact]
    public async Task FinalCleanup_PreservesDatabaseAndContainerFailures()
    {
        // Arrange
        var stages = new List<string>();
        var databaseFailure = new InvalidOperationException("database cleanup sentinel");
        var containerFailure = new InvalidOperationException("container cleanup sentinel");
        using var fixture = new PostgreSqlContainerFixture(
            static (_, _) => Task.CompletedTask,
            (database, _) =>
            {
                stages.Add(database);
                return stages.Count == 1 ? Task.FromException(databaseFailure) : Task.CompletedTask;
            },
            ConnectionString,
            disposeContainer: () =>
            {
                stages.Add("container");
                return Task.FromException(containerFailure);
            });
        var first = await fixture.CreateDatabaseAsync(CancellationToken.None);
        var second = await fixture.CreateDatabaseAsync(CancellationToken.None);

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(fixture.DisposeAsync);
        var retainedFailures = Assert.IsType<AggregateException>(failure.InnerException).Flatten().InnerExceptions;
        await fixture.DisposeAsync();

        // Assert
        Assert.Equal([DatabaseName(first), DatabaseName(second), "container"], stages);
        Assert.Equal(2, retainedFailures.Count);
        Assert.Contains(databaseFailure, retainedFailures);
        Assert.Contains(containerFailure, retainedFailures);
    }

    /// <summary>Independent test instances eagerly release only their own databases.</summary>
    [Fact]
    public async Task TestCleanup_DoesNotReleaseAnotherTestInstance()
    {
        // Arrange
        var dropped = new List<string>();
        using var fixture = CreateFixture(dropped);
        using var firstOwner = new DatabaseOwner(fixture);
        using var secondOwner = new DatabaseOwner(fixture);
        var databases = await Task.WhenAll(firstOwner.CreateAsync(), secondOwner.CreateAsync());

        // Act
        await firstOwner.DisposeAsync();
        var droppedBeforeOtherTestCleanup = dropped.ToArray();
        await secondOwner.DisposeAsync();
        await fixture.DisposeAsync();

        // Assert
        Assert.Equal([DatabaseName(databases[0])], droppedBeforeOtherTestCleanup);
        Assert.Equal([DatabaseName(databases[0]), DatabaseName(databases[1])], dropped);
    }

    /// <summary>A failed test-local drop does not prevent releasing its other databases or final retry.</summary>
    [Fact]
    public async Task TestCleanup_AttemptsRemainingDatabasesAndRetainsFailure()
    {
        // Arrange
        var attempts = new List<string>();
        using var fixture = new PostgreSqlContainerFixture(
            static (_, _) => Task.CompletedTask,
            (database, _) =>
            {
                attempts.Add(database);
                return attempts.Count == 1
                    ? Task.FromException(new InvalidOperationException("test cleanup sentinel"))
                    : Task.CompletedTask;
            },
            ConnectionString);
        using var owner = new DatabaseOwner(fixture);
        var first = await owner.CreateAsync();
        var second = await owner.CreateAsync();

        // Act
        var failure = await Assert.ThrowsAsync<AggregateException>(owner.DisposeAsync);
        var attemptsBeforeFinalCleanup = attempts.ToArray();
        await fixture.DisposeAsync();

        // Assert
        Assert.Equal("test cleanup sentinel", Assert.Single(failure.InnerExceptions).Message);
        Assert.Equal([DatabaseName(first), DatabaseName(second)], attemptsBeforeFinalCleanup);
        Assert.Equal([DatabaseName(first), DatabaseName(second), DatabaseName(first)], attempts);
    }

    /// <summary>Cancellation while waiting for a lifecycle operation cannot acquire or leak a database.</summary>
    [Fact]
    public async Task CreateDatabase_CanceledWaitDoesNotLeakOwnership()
    {
        // Arrange
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new List<string>();
        var dropped = new List<string>();
        using var fixture = new PostgreSqlContainerFixture(
            async (database, _) =>
            {
                created.Add(database);
                entered.TrySetResult(true);
                await release.Task;
            },
            (database, _) =>
            {
                dropped.Add(database);
                return Task.CompletedTask;
            },
            ConnectionString);
        var first = fixture.CreateDatabaseAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        var canceled = fixture.CreateDatabaseAsync(cancellation.Token);

        // Act
        await cancellation.CancelAsync();
        var failure = await Record.ExceptionAsync(() => canceled.WaitAsync(TimeSpan.FromSeconds(30)));
        release.TrySetResult(true);
        var owned = await first;
        await fixture.DisposeAsync();

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal([DatabaseName(owned)], created);
        Assert.Equal(created, dropped);
    }

    /// <summary>A completed test owner cannot allocate a database after its cleanup boundary.</summary>
    [Fact]
    public async Task TestCleanup_RejectsDatabaseCreationAfterCompletion()
    {
        // Arrange
        var creations = 0;
        using var fixture = new PostgreSqlContainerFixture(
            (_, _) =>
            {
                creations++;
                return Task.CompletedTask;
            },
            static (_, _) => Task.CompletedTask,
            ConnectionString);
        using var owner = new DatabaseOwner(fixture);
        await owner.DisposeAsync();

        // Act
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(owner.CreateAsync);
        await fixture.DisposeAsync();

        // Assert
        Assert.Contains("completed test", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, creations);
    }

    private static PostgreSqlContainerFixture CreateFixture(
        List<string> dropped
    ) => new(
        static (_, _) => Task.CompletedTask,
        (database, _) =>
        {
            dropped.Add(database);
            return Task.CompletedTask;
        },
        ConnectionString);

    private static string ConnectionString(
        string database
    ) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Database = database,
        Pooling = false,
    }.ConnectionString;

    private static string DatabaseName(
        string connectionString
    ) => new NpgsqlConnectionStringBuilder(connectionString).Database
        ?? throw new InvalidOperationException("The fake fixture did not assign a database.");

    private sealed class DatabaseOwner(
        PostgreSqlContainerFixture fixture
    ) : PostgreSqlIntegrationTestBase(fixture)
    {
        public Task<string> CreateAsync() => CreateDatabaseAsync(CancellationToken.None);
    }
}
