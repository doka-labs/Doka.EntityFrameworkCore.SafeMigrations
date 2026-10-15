namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies actual fixture ownership across uncertain database creation without a container.</summary>
public sealed class SqlServerDatabaseCreationTests
{
    private const string RootConnectionString =
        "Server=127.0.0.1,1433;Database=master;Integrated Security=True;Pooling=True;TrustServerCertificate=False;";

    /// <summary>Retains uncertain server outcomes until idempotent fixture cleanup confirms absence.</summary>
    /// <param name="serverCreatedDatabase">Whether the server applied DDL before the response failed.</param>
    /// <param name="cancelResponse">Whether cancellation replaces the creation acknowledgement.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Create_UncertainResponseRetainsProvisionalOwnershipForCleanup(
        bool serverCreatedDatabase,
        bool cancelResponse
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var expected = new InvalidOperationException("Injected lost CREATE acknowledgement.");
        var physicalDatabases = new HashSet<string>(StringComparer.Ordinal);
        var createdNames = new List<string>();
        var droppedNames = new List<string>();
        var cleanupTokens = new List<CancellationToken>();
        var containerDisposals = 0;
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (database, token) =>
        {
            createdNames.Add(database);

            if (serverCreatedDatabase)
            {
                physicalDatabases.Add(database);
            }

            if (!cancelResponse)
            {
                return Task.FromException(expected);
            }

            cancellation.Cancel();

            return Task.FromCanceled(token);
        }, (database, token) =>
        {
            droppedNames.Add(database);
            cleanupTokens.Add(token);
            physicalDatabases.Remove(database);

            return Task.CompletedTask;
        }, () =>
        {
            containerDisposals++;

            return Task.CompletedTask;
        });

        // Act
        var observed = await Record.ExceptionAsync(() => fixture.CreateDatabaseAsync(cancellation.Token));
        var createdBeforeCleanup = physicalDatabases.Count;
        await fixture.DisposeAsync();

        // Assert
        if (cancelResponse)
        {
            Assert.Equal(cancellation.Token,
                Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
        }
        else
        {
            Assert.Same(expected, observed);
        }

        var database = Assert.Single(createdNames);

        Assert.Equal(serverCreatedDatabase ? 1 : 0, createdBeforeCleanup);
        Assert.StartsWith("sm_", database, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(database.AsSpan(3), "N", out _));
        Assert.Equal(createdNames, droppedNames);
        Assert.Equal([CancellationToken.None], cleanupTokens);
        Assert.Empty(physicalDatabases);
        Assert.Equal(1, containerDisposals);
    }

    /// <summary>Validates the target before calling CREATE and keeps independent database names.</summary>
    [Fact]
    public async Task Create_SuccessReturnsValidatedDistinctTargetsAndCleanupOwnsBoth()
    {
        // Arrange
        var createdNames = new List<string>();
        var droppedNames = new List<string>();
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (database, _) =>
        {
            createdNames.Add(database);

            return Task.CompletedTask;
        }, (database, _) =>
        {
            droppedNames.Add(database);

            return Task.CompletedTask;
        }, () => Task.CompletedTask);

        // Act
        var first = new SqlConnectionStringBuilder(await fixture.CreateDatabaseAsync());
        var second = new SqlConnectionStringBuilder(await fixture.CreateDatabaseAsync());
        await fixture.DisposeAsync();

        // Assert
        Assert.NotEqual(first.InitialCatalog, second.InitialCatalog);
        Assert.Equal([first.InitialCatalog, second.InitialCatalog], createdNames);
        Assert.Equal(createdNames, droppedNames);
        Assert.False(first.Pooling);
        Assert.False(second.Pooling);
        Assert.True(first.TrustServerCertificate);
        Assert.True(second.TrustServerCertificate);
    }

    /// <summary>Rejects an invalid target before server work or provisional ownership begins.</summary>
    [Fact]
    public async Task Create_InvalidConnectionStringDoesNotInvokeCreateOrOwnADatabase()
    {
        // Arrange
        var createAttempts = 0;
        var dropAttempts = 0;
        var containerDisposals = 0;
        using var fixture = new SqlServerContainerFixture("unsupported_fixture_setting=true", (_, _) =>
        {
            createAttempts++;

            return Task.CompletedTask;
        }, (_, _) =>
        {
            dropAttempts++;

            return Task.CompletedTask;
        }, () =>
        {
            containerDisposals++;

            return Task.CompletedTask;
        });

        // Act
        var observed = await Record.ExceptionAsync(() => fixture.CreateDatabaseAsync());
        await fixture.DisposeAsync();

        // Assert
        Assert.IsType<ArgumentException>(observed);
        Assert.Equal(0, createAttempts);
        Assert.Equal(0, dropAttempts);
        Assert.Equal(1, containerDisposals);
    }

    /// <summary>Cancellation before acquiring the lifecycle lock never owns a database.</summary>
    [Fact]
    public async Task Create_AlreadyCanceledRequestDoesNotInvokeCreateOrOwnADatabase()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var createAttempts = 0;
        var dropAttempts = 0;
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (_, _) =>
        {
            createAttempts++;

            return Task.CompletedTask;
        }, (_, _) =>
        {
            dropAttempts++;

            return Task.CompletedTask;
        }, () => Task.CompletedTask);

        // Act
        var observed = await Record.ExceptionAsync(() => fixture.CreateDatabaseAsync(cancellation.Token));
        await fixture.DisposeAsync();

        // Assert
        Assert.Equal(cancellation.Token,
            Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
        Assert.Equal(0, createAttempts);
        Assert.Equal(0, dropAttempts);
    }

    /// <summary>A cancelled lock waiter neither creates a database nor releases the first caller's lock.</summary>
    [Fact]
    public async Task Create_CanceledLockWaiterDoesNotStealOwnershipOrInvokeServerWork()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createdNames = new List<string>();
        var droppedNames = new List<string>();
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (database, _) =>
        {
            createdNames.Add(database);
            firstEntered.SetResult();

            return finishFirst.Task;
        }, (database, _) =>
        {
            droppedNames.Add(database);

            return Task.CompletedTask;
        }, () => Task.CompletedTask);

        // Act
        var firstCreation = fixture.CreateDatabaseAsync();
        await firstEntered.Task;
        var waitingCreation = fixture.CreateDatabaseAsync(cancellation.Token);
        var waitedForLock = !waitingCreation.IsCompleted;
        cancellation.Cancel();
        var observed = await Record.ExceptionAsync(() => waitingCreation);
        var attemptsBeforeFirstCompleted = createdNames.Count;
        finishFirst.SetResult();
        var first = new SqlConnectionStringBuilder(await firstCreation);
        await fixture.DisposeAsync();

        // Assert
        Assert.True(waitedForLock);
        Assert.Equal(cancellation.Token,
            Assert.IsAssignableFrom<OperationCanceledException>(observed).CancellationToken);
        Assert.Equal(1, attemptsBeforeFirstCompleted);
        Assert.Equal([first.InitialCatalog], createdNames);
        Assert.Equal(createdNames, droppedNames);
    }
}
