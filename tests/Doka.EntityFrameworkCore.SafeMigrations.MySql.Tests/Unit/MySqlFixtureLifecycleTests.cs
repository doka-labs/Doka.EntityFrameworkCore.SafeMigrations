namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises provisional ownership and all cleanup failure paths without constructing a container.</summary>
public sealed class MySqlFixtureLifecycleTests
{
    private static readonly string[] s_expectedCreationEvents = ["registered", "created"];
    private static readonly string[] s_expectedCleanupEvents = ["database", "container", "synchronization"];

    /// <summary>Ownership and its empty login set must be recorded before the native CREATE callback executes.</summary>
    [Fact]
    public async Task SuccessfulCreateRegistersOwnershipBeforeReturningLease()
    {
        // Arrange
        var events = new List<string>();
        var databaseOwned = false;
        var usersOwned = false;
        var registeredBeforeCreate = false;

        // Act
        await MySqlFixtureLifecycle.CreateDatabaseAsync(
            () =>
            {
                databaseOwned = true;
                usersOwned = true;
                events.Add("registered");
            },
            _ =>
            {
                registeredBeforeCreate = databaseOwned && usersOwned;
                events.Add("created");

                return Task.CompletedTask;
            },
            CancellationToken.None);

        // Assert
        Assert.True(registeredBeforeCreate);
        Assert.Equal(s_expectedCreationEvents, events);
    }

    /// <summary>An acknowledgement failure after server creation retains provisional ownership for final cleanup.</summary>
    /// <param name="cancel">Whether the failed acknowledgement is cancellation rather than a native failure.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCreateAcknowledgementRetainsOwnershipWithoutReturningLease(bool cancel)
    {
        // Arrange
        const string database = "sm_provisional";
        const string lease = "Server=localhost;Database=sm_provisional;User ID=root";
        using var cancellation = new CancellationTokenSource();
        var databases = new Dictionary<string, string>(StringComparer.Ordinal);
        var users = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var nativeFailure = new InvalidOperationException("The server created the database but lost its acknowledgement.");
        var serverDatabaseExists = false;
        var ownedAtCreate = false;
        var cleanupCalls = 0;
        string? returnedLease = null;

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            await MySqlFixtureLifecycle.CreateDatabaseAsync(
                () =>
                {
                    databases.Add(database, lease);
                    users.Add(database, new HashSet<string>(StringComparer.Ordinal));
                },
                token =>
                {
                    ownedAtCreate = databases.TryGetValue(database, out var ownedLease) && ownedLease == lease
                        && users.ContainsKey(database);
                    serverDatabaseExists = true;
                    if (cancel)
                    {
                        cancellation.Cancel();

                        return Task.FromCanceled(token);
                    }

                    return Task.FromException(nativeFailure);
                },
                cancellation.Token);
            returnedLease = lease;
        });
        var existedAfterFailedAcknowledgement = serverDatabaseExists;
        var retainedLease = databases.GetValueOrDefault(database);
        var retainedEmptyUsers = users.TryGetValue(database, out var ownedUsers) && ownedUsers.Count == 0;
        await MySqlFixtureLifecycle.DisposeAsync(
            () =>
            {
                foreach (var ownedDatabase in databases.Keys.ToArray())
                {
                    cleanupCalls++;
                    serverDatabaseExists = false;
                    databases.Remove(ownedDatabase);
                    users.Remove(ownedDatabase);
                }

                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            () => { });

        // Assert
        Assert.True(ownedAtCreate);
        Assert.True(existedAfterFailedAcknowledgement);
        Assert.Null(returnedLease);
        Assert.Equal(lease, retainedLease);
        Assert.True(retainedEmptyUsers);
        Assert.Equal(1, cleanupCalls);
        Assert.False(serverDatabaseExists);
        Assert.Empty(databases);
        Assert.Empty(users);
        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
        }
        else
        {
            Assert.Same(nativeFailure, failure);
        }
    }

    /// <summary>Cancellation before entering creation must neither register ownership nor call the server.</summary>
    [Fact]
    public async Task CancellationBeforeCreationDoesNotRegisterOrExecute()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var registrations = 0;
        var creates = 0;

        // Act
        var failure = await Record.ExceptionAsync(() => MySqlFixtureLifecycle.CreateDatabaseAsync(
            () => registrations++,
            _ =>
            {
                creates++;

                return Task.CompletedTask;
            },
            cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(0, registrations);
        Assert.Equal(0, creates);
    }

    /// <summary>Independent database and container failures remain observable after all cleanup callbacks run.</summary>
    [Fact]
    public async Task DatabaseAndContainerCleanupFailuresAreBothPreserved()
    {
        // Arrange
        var databaseFailure = new InvalidOperationException("database cleanup failed");
        var containerFailure = new IOException("container cleanup failed");
        var events = new List<string>();

        // Act
        var failure = await Record.ExceptionAsync(() => MySqlFixtureLifecycle.DisposeAsync(
            () =>
            {
                events.Add("database");

                return Task.FromException(databaseFailure);
            },
            () =>
            {
                events.Add("container");

                return Task.FromException(containerFailure);
            },
            () => events.Add("synchronization")));

        // Assert
        var wrapper = Assert.IsType<InvalidOperationException>(failure);
        var aggregate = Assert.IsType<AggregateException>(wrapper.InnerException);
        Assert.Equal(s_expectedCleanupEvents, events);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Same(databaseFailure, aggregate.InnerExceptions[0]);
        Assert.Same(containerFailure, aggregate.InnerExceptions[1]);
    }

    /// <summary>The all-success control executes every cleanup step without inventing a failure.</summary>
    [Fact]
    public async Task SuccessfulCleanupExecutesAllStepsWithoutFailure()
    {
        // Arrange
        var events = new List<string>();

        // Act
        var failure = await Record.ExceptionAsync(() => MySqlFixtureLifecycle.DisposeAsync(
            () =>
            {
                events.Add("database");

                return Task.CompletedTask;
            },
            () =>
            {
                events.Add("container");

                return Task.CompletedTask;
            },
            () => events.Add("synchronization")));

        // Assert
        Assert.Null(failure);
        Assert.Equal(s_expectedCleanupEvents, events);
    }
}
