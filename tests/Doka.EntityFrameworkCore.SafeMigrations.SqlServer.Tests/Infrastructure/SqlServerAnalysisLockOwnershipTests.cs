namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Protects analysis-lock ownership across virtual session-identity capture and scope handoff.</summary>
public sealed class SqlServerAnalysisLockOwnershipTests
{
    /// <summary>Identity getters cannot become fallible handoff work after a successful native acquisition.</summary>
    /// <param name="borrowed">Whether the caller, rather than the analyzer, owns the transaction.</param>
    /// <param name="property">The virtual identity getter that would fail after native acquisition.</param>
    [Theory]
    [InlineData(false, "database")]
    [InlineData(false, "connection-string")]
    [InlineData(true, "database")]
    [InlineData(true, "connection-string")]
    public async Task IdentityCapturePrecedesNativeAcquisitionAndPreservesReleaseOwnership(
        bool borrowed,
        string property
    )
    {
        // Arrange
        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        await using var callerTransaction = borrowed
            ? await context.Database.UseTransactionAsync(connection.Transaction, CancellationToken.None)
            : null;

        connection.FailingIdentityProperty = property;
        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);

        // Act
        var observed = await Record.ExceptionAsync(() => AcquireAndReleaseAsync(
            analyzer,
            context,
            CancellationToken.None));

        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);

        // Assert
        Assert.Null(observed);
        Assert.Null(quarantine);
        Assert.Equal(1, connection.LockAcquisitionCount);
        Assert.Equal(0, connection.IdentityReadAfterAcquisitionCount);
        Assert.Equal(borrowed
                ? 1
                : 0,
            connection.LockReleaseCount);

        Assert.Equal(borrowed
                ? 0
                : 1,
            connection.Transaction.DisposeCount);

        Assert.Equal(borrowed
                ? 0
                : 1,
            connection.TransactionBeginCount);

        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(0, connection.ConnectionDisposeCount);
        if (borrowed)
        {
            Assert.Same(connection.Transaction, connection.ReleaseTransaction);
            Assert.Equal(CancellationToken.None, connection.CleanupToken);
            Assert.Same(connection.Transaction, context.Database.CurrentTransaction!.GetDbTransaction());
        }
    }

    /// <summary>
    /// A rejected identity capture never acquires a borrowed lock or disposes the caller transaction.
    /// </summary>
    /// <param name="borrowed">Whether the caller already owns the transaction.</param>
    /// <param name="property">The virtual identity getter rejecting pre-acquisition capture.</param>
    [Theory]
    [InlineData(false, "database")]
    [InlineData(false, "connection-string")]
    [InlineData(true, "database")]
    [InlineData(true, "connection-string")]
    public async Task FailedIdentityCaptureDoesNotAcquireOrAbandonAnApplicationLock(
        bool borrowed,
        string property
    )
    {
        // Arrange
        await using var connection = new SqlServerCleanupOwnershipConnection { FailIdentityBeforeAcquisition = true };
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        await using var callerTransaction = borrowed
            ? await context.Database.UseTransactionAsync(connection.Transaction, CancellationToken.None)
            : null;

        connection.FailingIdentityProperty = property;
        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);

        // Act
        var observed = await Record.ExceptionAsync(() => AcquireAndReleaseAsync(
            analyzer,
            context,
            CancellationToken.None));

        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);

        // Assert
        Assert.Same(connection.IdentityFailure, observed);
        Assert.Null(quarantine);
        Assert.Equal(0, connection.LockAcquisitionCount);
        Assert.Equal(0, connection.LockReleaseCount);
        Assert.Equal(connection.TransactionBeginCount, connection.Transaction.DisposeCount);
        if (borrowed)
        {
            Assert.Equal(0, connection.Transaction.DisposeCount);
            Assert.Same(connection.Transaction, context.Database.CurrentTransaction!.GetDbTransaction());
        }
    }

    /// <summary>Healthy identity capture retains normal owned and borrowed transaction release behavior.</summary>
    /// <param name="borrowed">Whether the caller owns the transaction.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyIdentityCaptureReleasesExactlyItsAcquiredOwnership(
        bool borrowed
    )
    {
        // Arrange
        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        await using var callerTransaction = borrowed
            ? await context.Database.UseTransactionAsync(connection.Transaction, CancellationToken.None)
            : null;

        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);

        // Act
        await AcquireAndReleaseAsync(analyzer, context, CancellationToken.None);

        // Assert
        Assert.Equal(1, connection.LockAcquisitionCount);
        Assert.Equal(borrowed
                ? 1
                : 0,
            connection.LockReleaseCount);

        Assert.Equal(borrowed
                ? 0
                : 1,
            connection.Transaction.DisposeCount);

        Assert.Equal(borrowed
                ? 0
                : 1,
            connection.TransactionBeginCount);

        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(0, connection.ConnectionDisposeCount);
    }

    /// <summary>
    /// An unobserved native result cannot authorize borrowed-session reuse or an unproven lock release.
    /// </summary>
    /// <param name="borrowed">Whether the caller owns the transaction containing the uncertain lock.</param>
    /// <param name="cancellation">Whether the native response fails by cancellation.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnknownAcquisitionResultRetainsPrimaryAndQuarantinesOnlyBorrowedOwnership(
        bool borrowed,
        bool cancellation
    )
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource();
        Exception original = cancellation
            ? new OperationCanceledException(cancellationSource.Token)
            : new InvalidOperationException("The native acquisition response was lost.");

        await using var connection = new SqlServerCleanupOwnershipConnection
        {
            AcquisitionFailure = original,
            AcquisitionFailureAfterDispatch = true,
        };

        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        await using var callerTransaction = borrowed
            ? await context.Database.UseTransactionAsync(connection.Transaction, CancellationToken.None)
            : null;

        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);

        // Act
        var observed = await Record.ExceptionAsync(() => AcquireAndReleaseAsync(
            analyzer,
            context,
            CancellationToken.None));

        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);

        // Assert
        Assert.Same(original, observed);
        Assert.Equal(1, connection.LockAcquisitionCount);
        Assert.Equal(0, connection.LockReleaseCount);
        Assert.Equal(borrowed
                ? 0
                : 1,
            connection.Transaction.DisposeCount);

        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(0, connection.ConnectionDisposeCount);
        if (borrowed)
        {
            Assert.IsType<InvalidOperationException>(quarantine);
            Assert.Same(connection.Transaction, context.Database.CurrentTransaction!.GetDbTransaction());
        }
        else
        {
            Assert.Null(quarantine);
        }
    }

    /// <summary>
    /// A known negative native result proves no new lock ownership without altering caller transactions.
    /// </summary>
    /// <param name="borrowed">Whether the rejected request used the caller's transaction.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownNegativeAcquisitionResultDoesNotQuarantineAReleasedAttempt(
        bool borrowed
    )
    {
        // Arrange
        await using var connection = new SqlServerCleanupOwnershipConnection { AcquisitionResult = -1 };
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        await using var callerTransaction = borrowed
            ? await context.Database.UseTransactionAsync(connection.Transaction, CancellationToken.None)
            : null;

        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);

        // Act
        var observed = await Record.ExceptionAsync(() => AcquireAndReleaseAsync(
            analyzer,
            context,
            CancellationToken.None));

        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);

        // Assert
        Assert.Equal(
            "SQL Server SafeMigrations could not acquire its analysis lock.",
            Assert.IsType<InvalidOperationException>(observed)
                .Message);

        Assert.Null(quarantine);
        Assert.Equal(0, connection.LockAcquisitionCount);
        Assert.Equal(0, connection.LockReleaseCount);
        Assert.Equal(borrowed
                ? 0
                : 1,
            connection.Transaction.DisposeCount);

        if (borrowed)
        {
            Assert.Same(connection.Transaction, context.Database.CurrentTransaction!.GetDbTransaction());
        }
    }

    /// <summary>Drains the actual returned scope without taking ownership of the caller's transaction.</summary>
    private static async Task AcquireAndReleaseAsync(
        SqlServerSafeMigrationProviderAnalyzer analyzer,
        DbContext context,
        CancellationToken cancellationToken
    )
    {
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, cancellationToken);

        await scope.DisposeAsync();
    }
}
