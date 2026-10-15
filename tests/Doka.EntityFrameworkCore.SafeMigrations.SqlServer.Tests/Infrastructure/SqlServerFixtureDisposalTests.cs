namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies both actual fixture cleanup stages and their failure evidence without a container.</summary>
public sealed class SqlServerFixtureDisposalTests
{
    private const string RootConnectionString =
        "Server=127.0.0.1,1433;Database=master;Integrated Security=True;Pooling=False;TrustServerCertificate=True;";

    /// <summary>Attempts container disposal after a failed database drop and preserves both exact failures.</summary>
    [Fact]
    public async Task Dispose_BothFailuresAreRetainedAndEachCleanupIsAttempted()
    {
        // Arrange
        var databaseFailure = new InvalidOperationException("Injected database cleanup failure.");
        var containerFailure = new InvalidOperationException("Injected container cleanup failure.");
        var attempts = new List<string>();
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (_, _) =>
        {
            attempts.Add("create");

            return Task.CompletedTask;
        }, (_, _) =>
        {
            attempts.Add("database");

            return Task.FromException(databaseFailure);
        }, () =>
        {
            attempts.Add("container");

            return Task.FromException(containerFailure);
        });
        await fixture.CreateDatabaseAsync();

        // Act
        var observed = await Record.ExceptionAsync(fixture.DisposeAsync);

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(observed);
        var aggregate = Assert.IsType<AggregateException>(failure.InnerException);

        Assert.Collection(aggregate.InnerExceptions,
            exception => Assert.Same(databaseFailure, exception),
            exception => Assert.Same(containerFailure, exception));
        Assert.Equal(["create", "database", "container"], attempts);
    }

    /// <summary>Preserves a lone failing resource without abandoning the other cleanup stage.</summary>
    /// <param name="failDatabaseCleanup">Whether database rather than container cleanup fails.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_SingleFailureRemainsVisibleAndBothStagesRun(
        bool failDatabaseCleanup
    )
    {
        // Arrange
        var expected = new InvalidOperationException("Injected single cleanup failure.");
        var attempts = new List<string>();
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (_, _) => Task.CompletedTask,
            (_, _) =>
            {
                attempts.Add("database");

                return failDatabaseCleanup ? Task.FromException(expected) : Task.CompletedTask;
            }, () =>
            {
                attempts.Add("container");

                return failDatabaseCleanup ? Task.CompletedTask : Task.FromException(expected);
            });
        await fixture.CreateDatabaseAsync();

        // Act
        var observed = await Record.ExceptionAsync(fixture.DisposeAsync);

        // Assert
        Assert.Same(expected, Assert.IsType<InvalidOperationException>(observed).InnerException);
        Assert.Equal(["database", "container"], attempts);
    }

    /// <summary>Completes successful database cleanup before disposing the container exactly once.</summary>
    [Fact]
    public async Task Dispose_SuccessAttemptsEachStageAndDropsEveryOwnedDatabase()
    {
        // Arrange
        var createdNames = new List<string>();
        var droppedNames = new List<string>();
        var databasesAtContainerDisposal = -1;
        var containerDisposals = 0;
        using var fixture = new SqlServerContainerFixture(RootConnectionString, (database, _) =>
        {
            createdNames.Add(database);

            return Task.CompletedTask;
        }, (database, _) =>
        {
            droppedNames.Add(database);

            return Task.CompletedTask;
        }, () =>
        {
            databasesAtContainerDisposal = createdNames.Count - droppedNames.Count;
            containerDisposals++;

            return Task.CompletedTask;
        });
        await fixture.CreateDatabaseAsync();
        await fixture.CreateDatabaseAsync();

        // Act
        await fixture.DisposeAsync();

        // Assert
        Assert.Equal(2, createdNames.Count);
        Assert.Equal(createdNames, droppedNames);
        Assert.Equal(0, databasesAtContainerDisposal);
        Assert.Equal(1, containerDisposals);
    }
}
