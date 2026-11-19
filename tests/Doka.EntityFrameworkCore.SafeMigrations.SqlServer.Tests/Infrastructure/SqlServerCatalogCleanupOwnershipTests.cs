namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Protects actual SQL catalog entry points from outer transport and parameter-factory cleanup failures.
/// </summary>
public sealed class SqlServerCatalogCleanupOwnershipTests
{
    private const string BatchFailureKey = "SafeMigrations.AnalysisBatchDisposeException";
    private const string FactoryFailureKey = "SafeMigrations.AnalysisCommandDisposeException";

    /// <summary>Outer transport disposal cannot replace the precise read failure or cancellation.</summary>
    /// <param name="tableTargets">Whether the occupancy prepass or classifier capture is exercised.</param>
    /// <param name="nativeBatch">Whether the native batch or sequential fallback owns disposal.</param>
    /// <param name="cancellation">Whether the read callback reports cancellation.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ReadAndTransportDisposalFail_RetainsReadPriority(
        bool tableTargets,
        bool nativeBatch,
        bool cancellation
    )
    {
        // Arrange
        using var cancellationSource = new CancellationTokenSource();
        Exception original = cancellation
            ? new OperationCanceledException(cancellationSource.Token)
            : new InvalidOperationException("Catalog read failed.");

        var secondary = new InvalidOperationException("Transport disposal failed.");
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            ReturnPresenceRows = tableTargets,
            AfterRowRead = _ => throw original,
            TransportDisposeFailure = secondary,
        };

        // Act
        var observed = await Record.ExceptionAsync(() => CaptureAsync(
            connection,
            tableTargets,
            CancellationToken.None));

        // Assert
        Assert.Same(original, observed);
        Assert.Same(secondary, original.Data[BatchFailureKey]);
        Assert.Equal(1, connection.RowsRead);
        Assert.Equal(nativeBatch
                ? 1
                : 0,
            connection.BatchesDisposed);

        Assert.Equal(nativeBatch
                ? 0
                : 1,
            connection.CommandsDisposed);

        Assert.Equal(nativeBatch && tableTargets
                ? 1
                : 0,
            connection.ParameterFactoriesDisposed);
    }

    /// <summary>
    /// Cleanup-only failures remain failures instead of being silently discarded after successful reads.
    /// </summary>
    /// <param name="tableTargets">Whether the occupancy prepass or classifier capture is exercised.</param>
    /// <param name="nativeBatch">Whether native or sequential transport owns cleanup.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OnlyTransportDisposalFails_PropagatesCleanupFailure(
        bool tableTargets,
        bool nativeBatch
    )
    {
        // Arrange
        var failure = new InvalidOperationException("Transport disposal failed.");
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            ReturnPresenceRows = tableTargets,
            TransportDisposeFailure = failure,
        };

        // Act
        var observed = await Record.ExceptionAsync(() => CaptureAsync(
            connection,
            tableTargets,
            CancellationToken.None));

        // Assert
        Assert.Same(failure, observed);
        Assert.Equal(1, connection.RowsRead);
        Assert.Equal(nativeBatch
                ? 1
                : 0,
            connection.BatchesDisposed);

        Assert.Equal(nativeBatch
                ? 0
                : 1,
            connection.CommandsDisposed);
    }

    /// <summary>
    /// Neither successful nor failed reads may return before actual owner-controlled disposal completes.
    /// </summary>
    /// <param name="tableTargets">Whether occupancy or classifier transport is exercised.</param>
    /// <param name="nativeBatch">Whether native or sequential transport owns disposal.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PendingTransportDisposal_RetainsActualOwnership(
        bool tableTargets,
        bool nativeBatch
    )
    {
        // Arrange
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new SqlServerCatalogTestConnection(nativeBatch)
        {
            ReturnPresenceRows = tableTargets,
            DisposalStarted = entered,
            DisposalCompletion = release.Task,
        };

        bool reached;
        bool returnedBeforeRelease;
        bool disposedBeforeRelease;
        Exception? observed;

        // Act
        var capture = Record.ExceptionAsync(() => CaptureAsync(connection, tableTargets, CancellationToken.None));
        try
        {
            await Task.WhenAny(entered.Task, capture);
            reached = entered.Task.IsCompletedSuccessfully;
            returnedBeforeRelease = capture.IsCompleted;
            disposedBeforeRelease = connection.CommandsDisposed + connection.BatchesDisposed != 0;
        }
        finally
        {
            release.TrySetResult();
            observed = await capture;
        }

        // Assert
        Assert.True(reached);
        Assert.False(returnedBeforeRelease);
        Assert.False(disposedBeforeRelease);
        Assert.Null(observed);
        Assert.Equal(nativeBatch
                ? 1
                : 0,
            connection.BatchesDisposed);

        Assert.Equal(nativeBatch
                ? 0
                : 1,
            connection.CommandsDisposed);
    }

    /// <summary>
    /// A native parameter factory remains secondary to read failure even when batch cleanup also fails.
    /// </summary>
    /// <param name="batchAlsoFails">Whether the later native batch disposal supplies a third failure.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OccupancyReadAndFactoryDisposalFail_RetainsEveryCleanupBoundary(
        bool batchAlsoFails
    )
    {
        // Arrange
        var original = new InvalidOperationException("Occupancy read failed.");
        var factoryFailure = new InvalidOperationException("Parameter factory disposal failed.");
        var batchFailure = new InvalidOperationException("Native batch disposal failed.");
        await using var connection = new SqlServerCatalogTestConnection
        {
            ReturnPresenceRows = true,
            AfterRowRead = _ => throw original,
            ParameterFactoryDisposeFailure = factoryFailure,
            TransportDisposeFailure = batchAlsoFails
                ? batchFailure
                : null,
        };

        // Act
        var observed = await Record.ExceptionAsync(() => CaptureAsync(
            connection,
            tableTargets: true,
            CancellationToken.None));

        // Assert
        Assert.Same(original, observed);
        Assert.Same(factoryFailure, original.Data[FactoryFailureKey]);
        Assert.Equal(1, connection.ParameterFactoriesDisposed);
        Assert.Equal(1, connection.BatchesDisposed);
        if (batchAlsoFails)
        {
            Assert.Same(batchFailure, original.Data[BatchFailureKey]);
        }
    }

    /// <summary>The first cleanup-only fault remains primary while the later batch is still disposed.</summary>
    [Fact]
    public async Task OnlyFactoryAndBatchDisposalFail_RetainsFirstCleanupFailure()
    {
        // Arrange
        var factoryFailure = new InvalidOperationException("Parameter factory disposal failed.");
        var batchFailure = new InvalidOperationException("Native batch disposal failed.");
        await using var connection = new SqlServerCatalogTestConnection
        {
            ReturnPresenceRows = true,
            ParameterFactoryDisposeFailure = factoryFailure,
            TransportDisposeFailure = batchFailure,
        };

        // Act
        var observed = await Record.ExceptionAsync(() => CaptureAsync(
            connection,
            tableTargets: true,
            CancellationToken.None));

        // Assert
        Assert.Same(factoryFailure, observed);
        Assert.Same(batchFailure, factoryFailure.Data[BatchFailureKey]);
        Assert.Equal(1, connection.RowsRead);
        Assert.Equal(1, connection.ParameterFactoriesDisposed);
        Assert.Equal(1, connection.BatchesDisposed);
    }

    /// <summary>
    /// A factory generation failure retains priority over synchronous factory disposal and outer cleanup.
    /// </summary>
    /// <param name="cleanupAlsoFails">Whether synchronous factory disposal fails after parameter construction.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParameterGenerationAndFactoryDisposalFail_RetainsGenerationFailure(
        bool cleanupAlsoFails
    )
    {
        // Arrange
        var original = new InvalidOperationException("Parameter creation failed.");
        var secondary = new InvalidOperationException("Parameter factory disposal failed.");
        await using var connection = new SqlServerCatalogTestConnection
        {
            ParameterCreationFailure = original,
            ParameterFactoryDisposeFailure = cleanupAlsoFails
                ? secondary
                : null,
        };

        await using var context = SqlServerCleanupOwnershipTests.CreateContext(connection);
        var mapping = context
            .GetService<IRelationalTypeMappingSource>()
            .FindMapping(typeof(string))!;

        SqlServerSafeMigrationRuntimePlan?[] plans =
        [
            new("N'missing'", "0", SafeMigrationRepairCapability.None, "0")
            {
                AnalysisParameters = [new SqlServerCatalogParameterValue("@source", "@source", "value", mapping, 5)],
            },
        ];

        var results = new SafeMigrationProviderAnalysis[1];

        // Act
        var observed = await Record.ExceptionAsync(() => SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection,
            null,
            73,
            plans,
            0,
            results,
            CancellationToken.None));

        // Assert
        Assert.Same(original, observed);
        if (cleanupAlsoFails)
        {
            Assert.Same(secondary, original.Data[FactoryFailureKey]);
        }

        Assert.Equal(1, connection.ParameterFactoriesDisposed);
        Assert.Equal(1, connection.BatchesDisposed);
        Assert.Equal(0, connection.BatchExecutions);
        Assert.Null(results[0]);
    }

    /// <summary>Calls the real bounded SQL Server provider entry point for the selected responsibility.</summary>
    private static async Task CaptureAsync(
        SqlServerCatalogTestConnection connection,
        bool tableTargets,
        CancellationToken cancellationToken
    )
    {
        if (tableTargets)
        {
            SafeMigrationOperation[] operations =
            [
                new(new EnsureTableIntent(new ExpectedTableDefinition(
                            "owned_target",
                            [new ExpectedColumnDefinition("Id", typeof(int), false, "int")]),
                        SafeMigrationTableMode.StrictDefinition),
                    SafeMigrationPolicy.ThrowIfDifferent),
            ];

            _ = await SqlServerSafeMigrationProviderAnalyzer.ReadAbsentTableTargetsAsync(
                connection,
                null,
                operations,
                0,
                1,
                73,
                cancellationToken);

            return;
        }

        SqlServerSafeMigrationRuntimePlan?[] plans =
        [
            new("N'missing'", "0", SafeMigrationRepairCapability.None, "0"),
        ];

        await SqlServerSafeMigrationProviderAnalyzer.ReadCatalogCaptureAsync(
            connection,
            null,
            73,
            plans,
            0,
            new SafeMigrationProviderAnalysis[1],
            cancellationToken);
    }
}
