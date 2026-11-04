namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Separates observer cancellation from shared initialization and mandatory cleanup ownership.</summary>
public sealed class MySqlGeneratedInitialMigrationFixtureCancellationTests
{
    /// <summary>A canceled waiter cannot cancel or restart the retained initialization used by a peer.</summary>
    /// <param name="legacy">Whether the legacy or strict profile is observed.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStopsOnlyTheObserverWhileThePeerRetainsTheOwner(bool legacy)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<GeneratedInitialMigrationObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var observation = CreateObservation();
        var calls = 0;
        var ownerToken = new CancellationToken(canceled: true);
        var fixture = new GeneratedInitialMigrationFixture((_, token) =>
        {
            calls++;
            ownerToken = token;

            return completion.Task;
        });

        // Act
        var observer = fixture.GetInitializedAsync(legacy, cancellation.Token);
        var peer = fixture.GetInitializedAsync(legacy, CancellationToken.None);
        cancellation.Cancel();
        var canceledBeforeOwnerCompleted = observer.IsCanceled;
        var peerWasPending = !peer.IsCompleted;
        completion.SetResult(observation);
        var canceled = await Record.ExceptionAsync(() => observer);
        var peerResult = await peer;
        var replay = await fixture.GetInitializedAsync(legacy, CancellationToken.None);

        // Assert
        Assert.True(canceledBeforeOwnerCompleted);
        Assert.True(peerWasPending);
        var exception = Assert.IsAssignableFrom<OperationCanceledException>(canceled);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(ownerToken.CanBeCanceled);
        Assert.Equal(1, calls);
        Assert.Same(observation, peerResult);
        Assert.Same(observation, replay);
    }

    /// <summary>A canceled request cannot construct or mutate the profile's retained initializer.</summary>
    /// <param name="legacy">Whether the legacy or strict profile is requested.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreCanceledObserverDoesNotStartInitialization(bool legacy)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var fixture = new GeneratedInitialMigrationFixture((_, _) =>
        {
            calls++;

            return Task.FromResult(CreateObservation());
        });

        // Act
        var exception = Record.Exception(() =>
        {
            _ = fixture.GetInitializedAsync(legacy, cancellation.Token);
        });

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(0, calls);
    }

    /// <summary>An uncancelable observer reuses the original shared task and initializer.</summary>
    [Fact]
    public async Task UncancelableObserversReuseTheSameOwnerTask()
    {
        // Arrange
        var observation = CreateObservation();
        var owner = Task.FromResult(observation);
        var calls = 0;
        var fixture = new GeneratedInitialMigrationFixture((_, _) =>
        {
            calls++;

            return owner;
        });

        // Act
        var first = fixture.GetInitializedAsync(false, CancellationToken.None);
        var second = fixture.GetInitializedAsync(false, CancellationToken.None);
        var result = await second;

        // Assert
        Assert.Same(owner, first);
        Assert.Same(first, second);
        Assert.Same(observation, result);
        Assert.Equal(1, calls);
    }

    /// <summary>Owner failures remain cached after an individual observer has canceled.</summary>
    [Fact]
    public async Task CanceledObserverDoesNotHideOrRetryTheOwnersFailure()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<GeneratedInitialMigrationObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var failure = new InvalidOperationException("Retained initialization failure.");
        var calls = 0;
        var fixture = new GeneratedInitialMigrationFixture((_, _) =>
        {
            calls++;

            return completion.Task;
        });

        // Act
        var observer = fixture.GetInitializedAsync(false, cancellation.Token);
        var peer = fixture.GetInitializedAsync(false, CancellationToken.None);
        cancellation.Cancel();
        completion.SetException(failure);
        var observedCancellation = await Record.ExceptionAsync(() => observer);
        var peerFailure = await Record.ExceptionAsync(() => peer);
        var replayFailure = await Record.ExceptionAsync(() =>
            fixture.GetInitializedAsync(false, CancellationToken.None));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(observedCancellation);
        Assert.Same(failure, peerFailure);
        Assert.Same(failure, replayFailure);
        Assert.Equal(1, calls);
    }

    /// <summary>SQL helpers reject canceled work before connection parsing or EF service resolution.</summary>
    /// <param name="operation">The independent SQL, script or scalar helper to exercise.</param>
    [Theory]
    [InlineData("sql")]
    [InlineData("script")]
    [InlineData("integer")]
    [InlineData("string")]
    [InlineData("context")]
    public async Task PreCanceledSqlHelpersDoNotBeginDatabaseWork(string operation)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var context = new DbContext(new DbContextOptionsBuilder().Options);

        // Act
        var exception = await Record.ExceptionAsync(() => InvokeHelper(operation, context, cancellation.Token));

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    /// <summary>The explicit uncancelable choice still reaches normal input/provider validation.</summary>
    /// <param name="operation">The SQL or scalar helper whose ordinary validation must remain reachable.</param>
    [Theory]
    [InlineData("sql")]
    [InlineData("script")]
    [InlineData("integer")]
    [InlineData("string")]
    [InlineData("context")]
    public async Task UncancelableSqlHelpersRetainNormalValidation(string operation)
    {
        // Arrange
        using var context = new DbContext(new DbContextOptionsBuilder().Options);

        // Act
        var exception = await Record.ExceptionAsync(() => InvokeHelper(operation, context, CancellationToken.None));

        // Assert
        if (operation == "context")
        {
            Assert.IsType<InvalidOperationException>(exception);
        }
        else
        {
            Assert.IsAssignableFrom<ArgumentException>(exception);
        }
    }

    /// <summary>The guard assertion forwards the caller token and rejects unrelated action failures.</summary>
    [Fact]
    public async Task GuardActionReceivesTheExactCallerToken()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var observedToken = CancellationToken.None;
        var unrelated = new InvalidOperationException("Not an owned guard failure.");

        // Act
        var exception = await Record.ExceptionAsync(() => HelperProbe.ObserveGuard(operationToken =>
        {
            calls++;
            observedToken = operationToken;

            return Task.FromException(unrelated);
        }, cancellation.Token));

        // Assert
        Assert.Equal(1, calls);
        Assert.Equal(cancellation.Token, observedToken);
        Assert.IsType<Xunit.Sdk.ThrowsException>(exception);
    }

    /// <summary>Pre-cancellation prevents entering an owned asynchronous action.</summary>
    [Fact]
    public async Task PreCanceledGuardDoesNotInvokeTheAction()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;

        // Act
        var exception = await Record.ExceptionAsync(() => HelperProbe.ObserveGuard(_ =>
        {
            calls++;

            return Task.CompletedTask;
        }, cancellation.Token));

        // Assert
        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(0, calls);
    }

    /// <summary>Creates inert identity evidence without provider reports or a database context.</summary>
    /// <returns>A distinct immutable result whose identity can be asserted independently.</returns>
    private static GeneratedInitialMigrationObservation CreateObservation() => new(
        null!, null!, null!, 1, static () => throw new NotSupportedException("The observer owns no database context."));

    /// <summary>Routes one helper without a parseable connection or configured EF provider.</summary>
    /// <param name="operation">The independently tested helper kind.</param>
    /// <param name="context">An intentionally unconfigured context used only by the context scalar helper.</param>
    /// <param name="cancellationToken">The caller cancellation that must take precedence over database work.</param>
    /// <returns>The actual asynchronous helper operation.</returns>
    private static Task InvokeHelper(
        string operation,
        DbContext context,
        CancellationToken cancellationToken
    ) => operation switch
    {
        "sql" => HelperProbe.ExecuteStatement(cancellationToken),
        "script" => HelperProbe.ExecuteScript(cancellationToken),
        "integer" => HelperProbe.ReadInteger(cancellationToken),
        "string" => HelperProbe.ReadString(cancellationToken),
        "context" => HelperProbe.ReadContext(context, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    /// <summary>Exposes protected helpers without constructing a fixture or creating native resources.</summary>
    private abstract class HelperProbe(
        MySqlEngineContainerFixture fixture
    ) : MySqlSafeMigrationIntegrationTests(fixture)
    {
        /// <summary>Executes statement validation against intentionally malformed connection settings.</summary>
        /// <param name="cancellationToken">The caller cancellation forwarded to the SQL helper.</param>
        /// <returns>The actual SQL helper task.</returns>
        internal static Task ExecuteStatement(CancellationToken cancellationToken) =>
            ExecuteSqlAsync("not a connection string", "SELECT 1;", cancellationToken);

        /// <summary>Executes script validation against intentionally malformed connection settings.</summary>
        /// <param name="cancellationToken">The caller cancellation forwarded to the script helper.</param>
        /// <returns>The actual migration script helper task.</returns>
        internal static Task ExecuteScript(CancellationToken cancellationToken) =>
            ExecuteMigrationScriptAsync("not a connection string", "SELECT 1;", cancellationToken);

        /// <summary>Reads integer validation against intentionally malformed connection settings.</summary>
        /// <param name="cancellationToken">The caller cancellation forwarded to the scalar helper.</param>
        /// <returns>The actual integer observation task.</returns>
        internal static Task<int> ReadInteger(CancellationToken cancellationToken) =>
            ScalarIntAsync("not a connection string", "SELECT 1;", cancellationToken);

        /// <summary>Reads string validation against intentionally malformed connection settings.</summary>
        /// <param name="cancellationToken">The caller cancellation forwarded to the scalar helper.</param>
        /// <returns>The actual string observation task.</returns>
        internal static Task<string> ReadString(CancellationToken cancellationToken) =>
            ScalarStringAsync("not a connection string", "SELECT 1;", cancellationToken);

        /// <summary>Reads context scalar validation without a configured relational provider.</summary>
        /// <param name="context">The intentionally unconfigured context.</param>
        /// <param name="cancellationToken">The caller cancellation forwarded to the scalar helper.</param>
        /// <returns>The actual context observation task.</returns>
        internal static Task<int> ReadContext(DbContext context, CancellationToken cancellationToken) =>
            ContextScalarIntAsync(context, "SELECT 1;", cancellationToken);

        /// <summary>Invokes the production test assertion with a token-aware action.</summary>
        /// <param name="action">The action receiving the exact caller token.</param>
        /// <param name="cancellationToken">The caller cancellation forwarded through the assertion.</param>
        /// <returns>The actual owned-guard assertion task.</returns>
        internal static Task<MySqlException> ObserveGuard(
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken
        ) => AssertOwnedGuardFailureAsync(action, cancellationToken);
    }
}
