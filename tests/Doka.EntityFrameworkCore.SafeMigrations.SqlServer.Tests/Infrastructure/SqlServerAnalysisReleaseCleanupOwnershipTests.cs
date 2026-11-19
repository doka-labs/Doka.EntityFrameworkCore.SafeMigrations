namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Protects session reuse and error priority when a published analysis scope cannot release ownership.
/// </summary>
public sealed class SqlServerAnalysisReleaseCleanupOwnershipTests
{
    private const string CommandFailureKey = "SafeMigrations.AnalysisCommandDisposeException";
    private const string ScopeFailureKey = "SafeMigrations.AnalysisScopeDisposeException";

    /// <summary>
    /// A failed borrowed release cannot authorize another lock acquisition or mutate caller ownership.
    /// </summary>
    /// <param name="asynchronous">Whether the actual asynchronous or synchronous disposal boundary is used.</param>
    /// <param name="failureKind">The native execution, cancellation, rejection or command-disposal failure.</param>
    [Theory]
    [InlineData(false, "execute")]
    [InlineData(true, "execute")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "negative")]
    [InlineData(true, "negative")]
    [InlineData(false, "dispose")]
    [InlineData(true, "dispose")]
    public async Task FailedBorrowedReleaseRejectsReuseAndPreservesCallerTransaction(
        bool asynchronous,
        string failureKind
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        Exception original = failureKind == "cancel"
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Injected release boundary failure.");

        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        context.Database.SetCommandTimeout(73);
        await using var callerTransaction = await context.Database.UseTransactionAsync(
            connection.Transaction,
            CancellationToken.None);

        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);
        connection.ReleaseFailure = failureKind is "execute" or "cancel"
            ? original
            : null;

        connection.ReleaseResult = failureKind == "negative"
            ? -999
            : 0;

        connection.CommandDisposeFailure = failureKind == "dispose"
            ? original
            : null;

        // Act
        var observed = asynchronous
            ? await Record.ExceptionAsync(() => scope
                .DisposeAsync()
                .AsTask())
            : Record.Exception(analyzer.Dispose);

        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);
        ResetReleaseFailures(connection);
        var retry = await ReacquireAsync(analyzer, context, CancellationToken.None);

        // Assert
        if (failureKind == "negative")
        {
            Assert.Equal("SQL Server did not release the borrowed analysis lock.",
                Assert.IsType<InvalidOperationException>(observed)
                    .Message);
        }
        else
        {
            Assert.Same(original, observed);
        }

        Assert.Equal(1, connection.LockAcquisitionCount);
        Assert.IsType<InvalidOperationException>(quarantine);
        Assert.IsType<InvalidOperationException>(retry);
        Assert.Equal(1, connection.LockReleaseCount);
        Assert.Equal(0, connection.Transaction.DisposeCount);
        Assert.Equal(0, connection.Transaction.RollbackCount);
        Assert.Same(connection.Transaction, context.Database.CurrentTransaction!.GetDbTransaction());
        Assert.Same(connection.Transaction, connection.ReleaseTransaction);
        Assert.Equal(CancellationToken.None, connection.CleanupToken);
        Assert.Equal(73, connection.CleanupCommandTimeout);
        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(0, connection.ConnectionDisposeCount);
    }

    /// <summary>
    /// Execution and status failures retain their identity while actual command-disposal evidence survives.
    /// </summary>
    /// <param name="asynchronous">Whether the real asynchronous or synchronous release is used.</param>
    /// <param name="failureKind">The original release boundary that fails before command disposal.</param>
    [Theory]
    [InlineData(false, "execute")]
    [InlineData(true, "execute")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "negative")]
    [InlineData(true, "negative")]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    public async Task ReleaseAndCommandDisposalFailuresRetainPrimaryAndSecondaryEvidence(
        bool asynchronous,
        string failureKind
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        Exception original = failureKind == "cancel"
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Injected native release failure.");

        var secondary = new InvalidOperationException("Injected release-command disposal failure.");
        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        await using var callerTransaction = await context.Database.UseTransactionAsync(
            connection.Transaction,
            CancellationToken.None);

        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);
        var disposedBeforeRelease = connection.CommandDisposeCount;
        connection.ReleaseFailure = failureKind is "execute" or "cancel"
            ? original
            : null;

        connection.ReleaseResult = failureKind switch
        {
            "negative" => -999,
            "invalid" => "not a native integer",
            _ => 0,
        };

        connection.CommandDisposeFailure = secondary;

        // Act
        var observed = asynchronous
            ? await Record.ExceptionAsync(() => scope
                .DisposeAsync()
                .AsTask())
            : Record.Exception(analyzer.Dispose);

        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);
        ResetReleaseFailures(connection);

        // Assert
        if (failureKind is "execute" or "cancel")
        {
            Assert.Same(original, observed);
        }

        Assert.Same(secondary,
            Assert
                .IsType<Exception>(observed, exactMatch: false)
                .Data[CommandFailureKey]);

        Assert.IsType<InvalidOperationException>(quarantine);
        Assert.Equal(disposedBeforeRelease + 1, connection.CommandDisposeCount);
        Assert.Equal(0, connection.Transaction.DisposeCount);
        Assert.Equal(0, connection.Transaction.RollbackCount);
    }

    /// <summary>Every acquired release command remains owned before its fallible provider setters run.</summary>
    /// <param name="asynchronous">Whether command disposal must use its asynchronous boundary.</param>
    /// <param name="property">The transaction, timeout or text setter that rejects configuration.</param>
    /// <param name="disposalAlsoFails">Whether the acquired command supplies secondary disposal evidence.</param>
    [Theory]
    [InlineData(false, "transaction", false)]
    [InlineData(true, "transaction", false)]
    [InlineData(false, "timeout", false)]
    [InlineData(true, "timeout", false)]
    [InlineData(false, "text", false)]
    [InlineData(true, "text", false)]
    [InlineData(false, "transaction", true)]
    [InlineData(true, "transaction", true)]
    [InlineData(false, "timeout", true)]
    [InlineData(true, "timeout", true)]
    [InlineData(false, "text", true)]
    [InlineData(true, "text", true)]
    public async Task ReleaseCommandConfigurationFailureDisposesTheUnpublishedCandidate(
        bool asynchronous,
        string property,
        bool disposalAlsoFails
    )
    {
        // Arrange
        var original = new InvalidOperationException("Injected release-command configuration failure.");
        var secondary = new InvalidOperationException("Injected unpublished-command disposal failure.");
        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        context.Database.SetCommandTimeout(73);
        await using var callerTransaction = await context.Database.UseTransactionAsync(
            connection.Transaction,
            CancellationToken.None);

        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);
        var disposedBeforeRelease = connection.CommandDisposeCount;
        connection.FailingCommandInitializationProperty = property;
        connection.CommandInitializationFailure = original;
        connection.CommandDisposeFailure = disposalAlsoFails
            ? secondary
            : null;

        // Act
        var observed = asynchronous
            ? await Record.ExceptionAsync(() => scope
                .DisposeAsync()
                .AsTask())
            : Record.Exception(analyzer.Dispose);

        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);
        ResetReleaseFailures(connection);

        // Assert
        Assert.Same(original, observed);
        Assert.Equal(disposedBeforeRelease + 1, connection.CommandDisposeCount);
        Assert.IsType<InvalidOperationException>(quarantine);
        Assert.Equal(0, connection.LockReleaseCount);
        Assert.Equal(0, connection.Transaction.DisposeCount);
        Assert.Equal(0, connection.Transaction.RollbackCount);
        if (disposalAlsoFails)
        {
            Assert.Same(secondary, original.Data[CommandFailureKey]);
        }
    }

    /// <summary>A failed owned transaction end cannot authorize reuse of an uncertain analysis lifetime.</summary>
    /// <param name="asynchronous">Whether the owned EF transaction ends asynchronously or synchronously.</param>
    /// <param name="cancellation">Whether the exact native disposal failure is a cancellation exception.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedOwnedTransactionEndRejectsReuseAndPreservesTheNativeFailure(
        bool asynchronous,
        bool cancellation
    )
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource();
        Exception original = cancellation
            ? new OperationCanceledException(cancellationSource.Token)
            : new InvalidOperationException("Injected owned-transaction disposal failure.");

        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);
        connection.Transaction.DisposeFailure = original;

        // Act
        var observed = asynchronous
            ? await Record.ExceptionAsync(() => scope
                .DisposeAsync()
                .AsTask())
            : Record.Exception(analyzer.Dispose);

        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);
        connection.Transaction.DisposeFailure = null;
        var retry = await ReacquireAsync(analyzer, context, CancellationToken.None);

        // Assert
        Assert.Same(original, observed);
        Assert.Equal(1, connection.LockAcquisitionCount);
        Assert.IsType<InvalidOperationException>(quarantine);
        Assert.IsType<InvalidOperationException>(retry);
        Assert.Equal(1, connection.Transaction.DisposeCount);
        Assert.Equal(0, connection.Transaction.RollbackCount);
        Assert.Equal(0, connection.LockReleaseCount);
    }

    /// <summary>The actual native release or transaction end must complete before scope disposal returns.</summary>
    /// <param name="borrowed">
    /// Whether the held boundary releases a borrowed lock instead of ending an owned transaction.
    /// </param>
    /// <param name="fails">Whether the native boundary fails after its explicit completion signal.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ScopeDisposalDrainsTheActualOwnedBoundary(
        bool borrowed,
        bool fails
    )
    {
        // Arrange
        var failure = new InvalidOperationException("Injected completed native release failure.");
        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        await using var callerTransaction = borrowed
            ? await context.Database.UseTransactionAsync(connection.Transaction, CancellationToken.None)
            : null;

        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);
        connection.HeldBoundary = borrowed
            ? "lock-release"
            : "transaction-dispose";

        connection.ReleaseFailure = borrowed && fails
            ? failure
            : null;

        connection.Transaction.DisposeFailure = !borrowed && fails
            ? failure
            : null;

        bool entered;
        bool returnedBeforeCompletion;
        Exception? observed;

        // Act
        var disposal = Record.ExceptionAsync(() => scope
            .DisposeAsync()
            .AsTask());

        try
        {
            await Task.WhenAny(connection.Entered.Task, disposal);
            entered = connection.Entered.Task.IsCompletedSuccessfully;
            returnedBeforeCompletion = disposal.IsCompleted;
        }
        finally
        {
            connection.Release.TrySetResult();
            observed = await disposal;
            ResetReleaseFailures(connection);
            connection.Transaction.DisposeFailure = null;
        }

        // Assert
        Assert.True(entered);
        Assert.False(returnedBeforeCompletion);
        if (fails)
        {
            Assert.Same(failure, observed);
        }
        else
        {
            Assert.Null(observed);
        }

        Assert.Equal(borrowed
                ? 0
                : 1,
            connection.Transaction.DisposeCount);

        Assert.Equal(borrowed
                ? 1
                : 0,
            connection.LockReleaseCount);

        Assert.Equal(0, connection.Transaction.RollbackCount);
    }

    /// <summary>
    /// A known primary failure retains an owned-disposal fault even when the session was already quarantined.
    /// </summary>
    /// <param name="cancellation">Whether the preserved original failure is cancellation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuarantinedScopeRetainsOwnedDisposalEvidenceUnderTheExistingPrimary(
        bool cancellation
    )
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource();
        Exception original = cancellation
            ? new OperationCanceledException(cancellationSource.Token)
            : new InvalidOperationException("Original work failure.");

        var secondary = new InvalidOperationException("Owned transaction cleanup failed after quarantine.");
        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);
        analyzer.QuarantineConnection();
        connection.Transaction.DisposeFailure = secondary;

        // Act
        var observed = await Record.ExceptionAsync(async () =>
        {
            try
            {
                throw original;
            }
            finally
            {
                await SafeMigrationAnalysisCleanup.DisposeAsync(scope, original, ScopeFailureKey);
            }
        });

        connection.Transaction.DisposeFailure = null;

        // Assert
        Assert.Same(original, observed);
        Assert.Same(secondary, original.Data[ScopeFailureKey]);
        Assert.Equal(1, connection.Transaction.DisposeCount);
        Assert.Equal(0, connection.LockReleaseCount);
        Assert.Equal(0, connection.Transaction.RollbackCount);
    }

    /// <summary>
    /// Raw synchronous and asynchronous unwinding retains the first known primary, not a later error.
    /// </summary>
    /// <param name="asynchronous">Whether the actual scope disposer is asynchronous.</param>
    /// <param name="cancellation">Whether the first known work failure is cancellation.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task KnownPrimarySurvivesRawScopeDisposalAndRetainsOwnedCleanupEvidence(
        bool asynchronous,
        bool cancellation
    )
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource();
        Exception original = cancellation
            ? new OperationCanceledException(cancellationSource.Token)
            : new InvalidOperationException("First known work failure.");

        var later = new InvalidOperationException("A later failure must not replace provenance.");
        var secondary = new InvalidOperationException("Owned cleanup failed during raw scope unwinding.");
        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);
        analyzer.QuarantineConnection(original);
        analyzer.QuarantineConnection(later);
        connection.Transaction.DisposeFailure = secondary;

        // Act
        var observed = await Record.ExceptionAsync(async () =>
        {
            try
            {
                throw original;
            }
            finally
            {
                if (asynchronous)
                {
                    await scope.DisposeAsync();
                }
                else
                {
                    // WHY: This branch isolates the synchronous entry point from the async control.
                    // ReSharper disable once MethodHasAsyncOverload
                    analyzer.Dispose();
                }
            }
        });

        connection.Transaction.DisposeFailure = null;

        // Assert
        Assert.Same(original, observed);
        Assert.Same(secondary, original.Data["SafeMigrations.AnalysisTransactionDisposeException"]);
        Assert.False(later.Data.Contains("SafeMigrations.AnalysisTransactionDisposeException"));
        Assert.Equal(1, connection.Transaction.DisposeCount);
        Assert.Equal(0, connection.LockReleaseCount);
        Assert.Equal(0, connection.Transaction.RollbackCount);
    }

    /// <summary>Disarms only injected failures before an adversarial retry could create another scope.</summary>
    private static void ResetReleaseFailures(
        SqlServerCleanupOwnershipConnection connection
    )
    {
        connection.ReleaseFailure = null;
        connection.ReleaseResult = 0;
        connection.CommandDisposeFailure = null;
        connection.CommandInitializationFailure = null;
        connection.FailingCommandInitializationProperty = null;
        connection.HeldBoundary = null;
    }

    /// <summary>
    /// Observes a real second acquisition and drains an unexpectedly admitted scope for the RED control.
    /// </summary>
    private static async Task<Exception?> ReacquireAsync(
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        DbContext context,
        CancellationToken cancellationToken
    )
    {
        return await Record.ExceptionAsync(async () =>
        {
            var scope = await analyzer.AcquireAnalysisScopeAsync(context, cancellationToken);

            await scope.DisposeAsync();
        });
    }
}
