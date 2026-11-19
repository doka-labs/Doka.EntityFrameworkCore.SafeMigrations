namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Requires actual native integer status evidence at both analysis application-lock readers.</summary>
public sealed class SqlServerApplicationLockStatusCleanupOwnershipTests
{
    /// <summary>An absent or wrong-typed acquisition response cannot publish a verified analysis scope.</summary>
    /// <param name="borrowed">Whether uncertain ownership belongs to a caller transaction.</param>
    /// <param name="scalarKind">The deliberately unproven native scalar shape.</param>
    [Theory]
    [InlineData(false, "missing")]
    [InlineData(false, "dbnull")]
    [InlineData(true, "missing")]
    [InlineData(true, "dbnull")]
    [InlineData(true, "text")]
    [InlineData(true, "boolean")]
    [InlineData(true, "wide")]
    [InlineData(true, "object")]
    public async Task UnprovenAcquisitionStatusRejectsScopePublicationAndPreservesOwnership(
        bool borrowed,
        string scalarKind
    )
    {
        // Arrange
        await using var connection = new SqlServerCleanupOwnershipConnection
        {
            AcquisitionResult = NativeScalar(scalarKind),
        };

        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        await using var callerTransaction = borrowed
            ? await context.Database.UseTransactionAsync(connection.Transaction, CancellationToken.None)
            : null;

        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);

        // Act
        var observed = await Record.ExceptionAsync(() => analyzer.AcquireAnalysisScopeAsync(
            context,
            CancellationToken.None));

        // WHY: A RED control can wrongly publish a scope; drain that actual owner before the test exits.
        await analyzer.DisposeAsync();
        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);

        // Assert
        Assert.IsType<InvalidOperationException>(observed);
        Assert.Equal(1, connection.LockAcquisitionCount);
        Assert.Equal(0, connection.LockReleaseCount);
        Assert.Equal(borrowed
                ? 0
                : 1,
            connection.Transaction.DisposeCount);

        Assert.Equal(0, connection.Transaction.RollbackCount);
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

    /// <summary>A missing or non-native release response cannot be converted into successful release proof.</summary>
    /// <param name="asynchronous">Whether synchronous or asynchronous scope disposal reads the result.</param>
    /// <param name="scalarKind">The deliberately unproven native scalar shape.</param>
    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "dbnull")]
    [InlineData(true, "dbnull")]
    [InlineData(false, "text")]
    [InlineData(true, "text")]
    [InlineData(false, "boolean")]
    [InlineData(true, "boolean")]
    [InlineData(false, "wide")]
    [InlineData(true, "wide")]
    [InlineData(false, "object")]
    [InlineData(true, "object")]
    public async Task UnprovenReleaseStatusRejectsSupportedSessionReuse(
        bool asynchronous,
        string scalarKind
    )
    {
        // Arrange
        await using var connection = new SqlServerCleanupOwnershipConnection();
        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        await using var callerTransaction = await context.Database.UseTransactionAsync(
            connection.Transaction,
            CancellationToken.None);

        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);
        var disposedBeforeRelease = connection.CommandDisposeCount;
        connection.ReleaseResult = NativeScalar(scalarKind);

        // Act
        var observed = asynchronous
            ? await Record.ExceptionAsync(() => scope
                .DisposeAsync()
                .AsTask())
            : Record.Exception(analyzer.Dispose);

        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);
        connection.ReleaseResult = 0;

        // Assert
        Assert.IsType<InvalidOperationException>(observed);
        Assert.IsType<InvalidOperationException>(quarantine);
        Assert.Equal(1, connection.LockReleaseCount);
        Assert.Equal(disposedBeforeRelease + 1, connection.CommandDisposeCount);
        Assert.Equal(0, connection.Transaction.DisposeCount);
        Assert.Equal(0, connection.Transaction.RollbackCount);
        Assert.Equal(CancellationToken.None, connection.CleanupToken);
    }

    /// <summary>
    /// Documented nonnegative status values preserve healthy release and a fresh analysis acquisition.
    /// </summary>
    /// <param name="asynchronous">Whether the first published scope ends asynchronously or synchronously.</param>
    /// <param name="borrowed">Whether both scopes borrow the caller's original transaction.</param>
    /// <param name="nativeStatus">The nonnegative native status; one remains valid after a waited acquisition.</param>
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, true, 2)]
    public async Task HealthyNativeStatusKeepsInitialAndRepeatedOwnershipUsable(
        bool asynchronous,
        bool borrowed,
        int nativeStatus
    )
    {
        // Arrange
        await using var connection = new SqlServerCleanupOwnershipConnection
        {
            AcquisitionResult = nativeStatus,
            ReleaseResult = nativeStatus,
        };

        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        await using var callerTransaction = borrowed
            ? await context.Database.UseTransactionAsync(connection.Transaction, CancellationToken.None)
            : null;

        var analyzer = SqlServerCleanupOwnershipTests.CreateAnalyzer(context);
        var scope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);

        // Act
        if (asynchronous)
        {
            await scope.DisposeAsync();
        }
        else
        {
            // WHY: This branch deliberately tests the synchronous entry point independently of the async control.
            // ReSharper disable once MethodHasAsyncOverload
            analyzer.Dispose();
        }

        var quarantine = Record.Exception(analyzer.ThrowIfConnectionQuarantined);
        var nextScope = await analyzer.AcquireAnalysisScopeAsync(context, CancellationToken.None);
        await nextScope.DisposeAsync();

        // Assert
        Assert.Null(quarantine);
        Assert.Equal(2, connection.LockAcquisitionCount);
        Assert.Equal(borrowed
                ? 2
                : 0,
            connection.LockReleaseCount);

        Assert.Equal(borrowed
                ? 0
                : 2,
            connection.Transaction.DisposeCount);

        Assert.Equal(0, connection.Transaction.RollbackCount);
        Assert.Equal(0, connection.CloseCount);
        Assert.Equal(0, connection.ConnectionDisposeCount);
    }

    /// <summary>Supplies distinct scalar runtime types without relying on numeric common-type inference.</summary>
    private static object? NativeScalar(
        string scalarKind
    ) => scalarKind switch
    {
        "missing" => null,
        "dbnull" => DBNull.Value,
        "text" => "0",
        "boolean" => false,
        "wide" => 0L,
        "object" => new object(),
        _ => throw new ArgumentOutOfRangeException(nameof(scalarKind)),
    };
}
