namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Proves actual cleanup completion and failure identity without wallclock watchdogs.</summary>
public sealed class SqlServerCleanupOwnershipTests
{
    private const string TemporaryTable = "#doka_sm_inventory_00000000000000000000000000000001";
    private const string RecoveryStatusKey = "Doka.SafeMigrations.SqlServer.RecoveryStatus";

    /// <summary>Cleanup cannot inherit cancellation, even when the caller has already been canceled.</summary>
    [Fact]
    public async Task CleanupTemporaryScopeAsync_CanceledCaller_UsesExplicitNone()
    {
        // Arrange
        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var original = new OperationCanceledException(cancellation.Token);

        // Act
        await analyzer.CleanupTemporaryScopeAsync(connection, null, TemporaryTable, 7, original);

        // Assert
        Assert.Equal(CancellationToken.None, connection.CleanupToken);
        Assert.Equal(7, connection.CleanupCommandTimeout);
        Assert.True(connection.ExecutionCompleted);
        Assert.Equal(1, connection.CommandDisposeCount);
        Assert.Equal(0, connection.CloseCount);
        Assert.False(original.Data.Contains(RecoveryStatusKey));
    }

    /// <summary>Each native cleanup boundary retains its owner until actual work completes.</summary>
    /// <param name="boundary">The native boundary deliberately held pending by the driver double.</param>
    [Theory]
    [InlineData("execute")]
    [InlineData("command-dispose")]
    [InlineData("close")]
    [InlineData("connection-dispose")]
    public async Task CleanupTemporaryScopeAsync_PendingNativeWork_DoesNotReturnOrReleaseEarly(
        string boundary
    )
    {
        // Arrange
        await using var connection = new SqlServerCleanupOwnershipConnection
        {
            HeldBoundary = boundary,
            CleanupFailure = boundary is "close" or "connection-dispose"
                ? new InvalidOperationException("Injected cleanup failure.")
                : null,
            CloseFailure = boundary == "connection-dispose"
                ? new InvalidOperationException("Injected close failure.")
                : null,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);
        var original = new OperationCanceledException("The caller was canceled.");
        bool enteredBoundary;
        bool completedBeforeRelease;
        bool boundaryFinishedBeforeRelease;

        // Act
        var cleanup = analyzer.CleanupTemporaryScopeAsync(connection, null, TemporaryTable, null, original);
        try
        {
            await Task.WhenAny(connection.Entered.Task, cleanup);
            enteredBoundary = connection.Entered.Task.IsCompletedSuccessfully;
            completedBeforeRelease = cleanup.IsCompleted;
            boundaryFinishedBeforeRelease = BoundaryCompleted(connection, boundary);
        }
        finally
        {
            connection.Release.TrySetResult();
            await cleanup;
        }

        // Assert
        Assert.True(enteredBoundary);
        Assert.False(completedBeforeRelease);
        Assert.False(boundaryFinishedBeforeRelease);
        Assert.True(BoundaryCompleted(connection, boundary));
        Assert.Equal(CancellationToken.None, connection.CleanupToken);
        Assert.Equal(1, connection.CommandDisposeCount);
    }

    /// <summary>SQL and disposal failures remain secondary to the original cancellation.</summary>
    [Fact]
    public async Task CleanupTemporaryScopeAsync_TwoCleanupFailures_RetainsFirstCleanupAndOriginal()
    {
        // Arrange
        var executionFailure = new InvalidOperationException("Cleanup execution failed.");
        var disposalFailure = new InvalidOperationException("Cleanup disposal failed.");
        await using var connection = new SqlServerCleanupOwnershipConnection
        {
            CleanupFailure = executionFailure,
            CommandDisposeFailure = disposalFailure,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);
        var original = new OperationCanceledException("Original cancellation.");

        // Act
        await analyzer.CleanupTemporaryScopeAsync(connection, null, TemporaryTable, null, original);

        // Assert
        Assert.Same(executionFailure, original.Data["Doka.SafeMigrations.SqlServer.CleanupException"]);
        Assert.Same(disposalFailure, executionFailure.Data["SafeMigrations.AnalysisCommandDisposeException"]);
        Assert.Equal("connection_closed", original.Data[RecoveryStatusKey]);
    }

    /// <summary>Disposal-only cleanup failures fail closed without inventing a successful cleanup result.</summary>
    /// <param name="hasOriginalFailure">Whether cleanup follows failed work or otherwise successful work.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupTemporaryScopeAsync_OnlyDisposalFails_PreservesCorrectFailurePriority(
        bool hasOriginalFailure
    )
    {
        // Arrange
        var disposalFailure = new InvalidOperationException("Cleanup disposal failed.");
        await using var connection = new SqlServerCleanupOwnershipConnection
        {
            CommandDisposeFailure = disposalFailure,
        };
        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);
        Exception? original = hasOriginalFailure
            ? new OperationCanceledException("The caller failed before cleanup.")
            : null;

        // Act
        var observed = await Record.ExceptionAsync(() =>
            analyzer.CleanupTemporaryScopeAsync(connection, null, TemporaryTable, null, original));

        var quarantine = Record.Exception(() => analyzer.ValidateContext(context));

        // Assert
        if (hasOriginalFailure)
        {
            Assert.Null(observed);
            Assert.Same(disposalFailure, original!.Data["Doka.SafeMigrations.SqlServer.CleanupException"]);
            Assert.Equal("connection_closed", original.Data[RecoveryStatusKey]);
        }
        else
        {
            var failure = Assert.IsType<InvalidOperationException>(observed);
            Assert.Same(disposalFailure, failure.InnerException);
            Assert.Equal("connection_closed", failure.Data[RecoveryStatusKey]);
        }

        Assert.True(connection.ExecutionCompleted);
        Assert.Equal(1, connection.CommandDisposeCount);
        Assert.Equal(1, connection.CloseCount);
        Assert.IsType<InvalidOperationException>(quarantine);
    }

    /// <summary>A failed transaction cleanup never replaces the exact acquisition failure.</summary>
    /// <param name="cancellation">Whether acquisition failed by cancellation or a provider-shaped error.</param>
    /// <param name="commandDisposalAlsoFails">Whether the acquisition command also fails to dispose.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AcquireAnalysisScopeAsync_DisposalAlsoFails_PreservesOriginalIdentity(
        bool cancellation,
        bool commandDisposalAlsoFails
    )
    {
        // Arrange
        Exception original = cancellation
            ? new OperationCanceledException("Acquisition canceled.")
            : new InvalidOperationException("Acquisition failed.");

        var secondary = new InvalidOperationException("Owned transaction disposal failed.");
        var commandDisposeFailure = new InvalidOperationException("Owned command disposal failed.");
        await using var connection = new SqlServerCleanupOwnershipConnection
        {
            AcquisitionFailure = original,
            CommandDisposeFailure = commandDisposalAlsoFails
                ? commandDisposeFailure
                : null,
        };

        connection.Transaction.DisposeFailure = secondary;
        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);

        // Act
        var observed = await Record.ExceptionAsync(() =>
            analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None));

        var reuseFailure = await Record.ExceptionAsync(() =>
            analyzer.AnalyzeAsync(context, [], CancellationToken.None));

        // Assert
        Assert.Same(original, observed);
        Assert.Same(secondary, original.Data["SafeMigrations.AnalysisTransactionDisposeException"]);
        Assert.Equal(1, connection.Transaction.DisposeCount);
        Assert.Contains(
            "requires a new context",
            Assert.IsType<InvalidOperationException>(reuseFailure)
                .Message,
            StringComparison.Ordinal);

        if (commandDisposalAlsoFails)
        {
            Assert.Same(commandDisposeFailure, original.Data["SafeMigrations.AnalysisCommandDisposeException"]);
        }
    }

    /// <summary>A fully released failed acquisition does not quarantine a healthy replacement attempt.</summary>
    [Fact]
    public async Task AcquireAnalysisScopeAsync_OwnedTransactionDisposed_DoesNotQuarantineAnalyzer()
    {
        // Arrange
        var original = new InvalidOperationException("Analysis lock acquisition failed.");
        await using var connection = new SqlServerCleanupOwnershipConnection
        {
            AcquisitionFailure = original,
        };
        await using var context = CreateContext(connection);
        var analyzer = CreateAnalyzer(context);

        // Act
        var observed = await Record.ExceptionAsync(() =>
            analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None));

        var reuseFailure = await Record.ExceptionAsync(() =>
            analyzer.AnalyzeAsync(context, [], CancellationToken.None));

        // Assert
        Assert.Same(original, observed);
        Assert.Equal(1, connection.Transaction.DisposeCount);
        Assert.Null(reuseFailure);
    }

    /// <summary>Builds the actual provider services without opening a native server connection.</summary>
    internal static DbContext CreateContext(
        DbConnection connection
    ) => new(new DbContextOptionsBuilder().UseSqlServer(connection)
        .Options);

    /// <summary>Uses real provider type-mapping and SQL-helper dependencies around the controlled transport.</summary>
    internal static SqlServerSafeMigrationProviderAnalyzer CreateAnalyzer(
        DbContext context
    ) => new(context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

    private static bool BoundaryCompleted(
        SqlServerCleanupOwnershipConnection connection,
        string boundary
    ) => boundary switch
    {
        "execute" => connection.ExecutionCompleted,
        "command-dispose" => connection.CommandDisposeCount != 0,
        "close" => connection.CloseCount != 0,
        "connection-dispose" => connection.ConnectionDisposeCount != 0,
        _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
    };
}
