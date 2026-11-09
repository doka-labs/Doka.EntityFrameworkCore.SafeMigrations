namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks owned helper cancellation without opening a database or abandoning shared initialization.</summary>
public sealed class PostgreSqlAsyncHelperCancellationTests
{
    /// <summary>Pre-canceled SQL helpers stop before connection parsing or database access.</summary>
    /// <param name="helper">The independent SQL helper whose cancellation boundary is exercised.</param>
    [Theory]
    [InlineData("execute")]
    [InlineData("integer")]
    [InlineData("text")]
    [InlineData("check")]
    public async Task SqlHelpersObserveCancellationBeforeConnectionParsing(
        string helper
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exception = await Record.ExceptionAsync(() => InvokeSqlHelperAsync(
            helper,
            "not a connection string",
            cancellation.Token));

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    /// <summary>
    /// An explicit uncancelable choice preserves ordinary connection validation rather than masking it.
    /// </summary>
    /// <param name="helper">The independent SQL helper whose normal validation is exercised.</param>
    [Theory]
    [InlineData("execute")]
    [InlineData("integer")]
    [InlineData("text")]
    [InlineData("check")]
    public async Task SqlHelpersWithNoneRetainConnectionValidation(
        string helper
    )
    {
        // Arrange
        const string invalidConnection = "not a connection string";

        // Act
        var exception = await Record.ExceptionAsync(() => InvokeSqlHelperAsync(
            helper,
            invalidConnection,
            CancellationToken.None));

        // Assert
        Assert.IsAssignableFrom<ArgumentException>(exception);
    }

    /// <summary>A canceled first observer cannot initialize either shared tooling profile.</summary>
    /// <param name="legacy">Whether the strict or legacy initialization owner is selected.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledObserverDoesNotStartSharedInitialization(
        bool legacy
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var starts = 0;
        var fixture = new GeneratedInitialMigrationFixture((_, _) =>
        {
            starts++;

            return Task.FromResult(Observation());
        });

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.GetInitializedAsync(legacy, cancellation.Token));

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(0, starts);
    }

    /// <summary>Canceling one waiter preserves the retained owner and another observer's eventual result.</summary>
    [Fact]
    public async Task CanceledObserverDoesNotAbandonSharedInitializationOrPeer()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<GeneratedInitialMigrationObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var expected = Observation();
        var starts = 0;
        var ownerToken = cancellation.Token;
        var fixture = new GeneratedInitialMigrationFixture((_, token) =>
        {
            starts++;
            ownerToken = token;

            return completion.Task;
        });

        var canceledObserver = fixture.GetInitializedAsync(false, cancellation.Token);
        var peer = fixture.GetInitializedAsync(false, CancellationToken.None);

        // Act
        bool observerFinishedBeforeOwner;
        bool ownerStillRunning;
        try
        {
            cancellation.Cancel();
            observerFinishedBeforeOwner = canceledObserver.IsCompleted;
            ownerStillRunning = !completion.Task.IsCompleted;
        }
        finally
        {
            // WHY: The negative control must also drain the old owner-task-only path;
            // otherwise awaiting its ignored cancellation would hang instead of failing.
            completion.TrySetResult(expected);
        }

        var exception = await Record.ExceptionAsync(() => canceledObserver);
        var result = await peer;

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.True(observerFinishedBeforeOwner);
        Assert.True(ownerStillRunning);
        Assert.False(ownerToken.CanBeCanceled);
        Assert.Equal(1, starts);
        Assert.Same(expected, result);
        Assert.Same(completion.Task, peer);
    }

    /// <summary>Independent uncancelable observers reuse the exact shared task and initialize only once.</summary>
    [Fact]
    public async Task UncancelableObserversReuseTheOwnedInitializationTask()
    {
        // Arrange
        var completion = new TaskCompletionSource<GeneratedInitialMigrationObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var expected = Observation();
        var starts = 0;
        var fixture = new GeneratedInitialMigrationFixture((_, _) =>
        {
            starts++;

            return completion.Task;
        });

        // Act
        var first = fixture.GetInitializedAsync(true, CancellationToken.None);
        var second = fixture.GetInitializedAsync(true, CancellationToken.None);
        completion.SetResult(expected);
        var results = await Task.WhenAll(first, second);

        // Assert
        Assert.Equal(1, starts);
        Assert.Same(completion.Task, first);
        Assert.Same(first, second);
        Assert.All(results, result => Assert.Same(expected, result));
    }

    /// <summary>
    /// Selects the actual protected helper without instantiating its native fixture-owning test base.
    /// </summary>
    /// <param name="helper">The independently exercised SQL helper.</param>
    /// <param name="connectionString">The deliberately invalid connection used to distinguish pre-cancellation.</param>
    /// <param name="cancellationToken">The explicit token forwarded to the selected helper.</param>
    /// <returns>The selected helper's task.</returns>
    private static Task InvokeSqlHelperAsync(
        string helper,
        string connectionString,
        CancellationToken cancellationToken
    ) => helper switch
    {
        "execute" => HelperAccess.ExecuteFixtureSqlAsync(connectionString, cancellationToken),
        "integer" => HelperAccess.ReadIntegerAsync(connectionString, cancellationToken),
        "text" => HelperAccess.ReadTextAsync(connectionString, cancellationToken),
        "check" => HelperAccess.ReadCheckAsync(connectionString, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(helper)),
    };

    /// <summary>Creates valid immutable report data while keeping every test independent of a live provider.</summary>
    /// <returns>The distinct sentinel observation shared by the observers of one initialization.</returns>
    private static GeneratedInitialMigrationObservation Observation()
    {
        var report = new SafeMigrationRunReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.NoOperations,
            DateTimeOffset.UnixEpoch,
            "fixture-cancellation",
            new SafeMigrationProviderEnvironment("npgsql_postgresql", "postgresql", "test"),
            null,
            "safe-relational-model:v1:postgresql:sha256:" + new string('a', 64),
            new string('b', 64),
            []);

        return new GeneratedInitialMigrationObservation(
            report,
            report,
            report,
            0,
            static () => new DbContext(new DbContextOptionsBuilder().Options));
    }

    /// <summary>Exposes only protected SQL helpers; no instance or native container is created.</summary>
    private abstract class HelperAccess(
        PostgreSqlContainerFixture fixture
    ) : PostgreSqlIntegrationTestBase(fixture)
    {
        /// <summary>Forwards cancellation to real SQL execution.</summary>
        /// <param name="connectionString">The isolated connection configuration.</param>
        /// <param name="cancellationToken">The caller's cancellation scope.</param>
        /// <returns>The source-controlled SQL execution task.</returns>
        public static Task ExecuteFixtureSqlAsync(
            string connectionString,
            CancellationToken cancellationToken
        ) => ExecuteSqlAsync(connectionString, "SELECT 1;", cancellationToken);

        /// <summary>Forwards cancellation to real integer scalar reading.</summary>
        /// <param name="connectionString">The isolated connection configuration.</param>
        /// <param name="cancellationToken">The caller's cancellation scope.</param>
        /// <returns>The native integer scalar task.</returns>
        public static Task<int> ReadIntegerAsync(
            string connectionString,
            CancellationToken cancellationToken
        ) => ScalarIntAsync(connectionString, "SELECT 1;", cancellationToken);

        /// <summary>Forwards cancellation to real text scalar reading.</summary>
        /// <param name="connectionString">The isolated connection configuration.</param>
        /// <param name="cancellationToken">The caller's cancellation scope.</param>
        /// <returns>The native text scalar task.</returns>
        public static Task<string> ReadTextAsync(
            string connectionString,
            CancellationToken cancellationToken
        ) => ScalarStringAsync(connectionString, "SELECT 'value';", cancellationToken);

        /// <summary>Forwards cancellation to real CHECK catalog reading.</summary>
        /// <param name="connectionString">The isolated connection configuration.</param>
        /// <param name="cancellationToken">The caller's cancellation scope.</param>
        /// <returns>The native CHECK catalog task.</returns>
        public static Task<string> ReadCheckAsync(
            string connectionString,
            CancellationToken cancellationToken
        ) => ReadCheckExpressionAsync(connectionString, "ck_probe", cancellationToken);
    }
}
