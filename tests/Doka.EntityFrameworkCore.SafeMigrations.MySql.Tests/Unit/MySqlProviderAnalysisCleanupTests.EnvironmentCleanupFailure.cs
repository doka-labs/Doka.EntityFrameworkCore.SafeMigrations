namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Environment cleanup cannot replace a metadata failure or its original cancellation identity.</summary>
    /// <param name="canceled">Whether metadata access cancels the actual operation token before failing.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetEnvironmentAsync_MetadataAndCloseFailurePreservePrimary(
        bool canceled
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Native version metadata failed.");

        var secondary = new InvalidOperationException("Native close failed.");
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            EnvironmentFailure = primary,
            CloseFailure = secondary,
            BeforeVersionRead = canceled
                ? cancellation.Cancel
                : null,
        };

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.GetEnvironmentAsync(context, cancellation.Token));

        // Assert
        Assert.Same(primary, failure);
        Assert.Same(secondary, primary.Data["SafeMigrations.AnalysisConnectionCloseException"]);
        Assert.Equal(cancellation.Token, connection.OpenToken);
        Assert.Equal(1, connection.CloseAttempts);
        if (canceled)
        {
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(cancellation.Token,
                Assert.IsType<OperationCanceledException>(failure)
                    .CancellationToken);
        }
    }

    /// <summary>A close-only error remains an observable environment failure after valid metadata.</summary>
    [Fact]
    public async Task GetEnvironmentAsync_CloseOnlyFailureIsNotSuppressed()
    {
        // Arrange
        var secondary = new InvalidOperationException("Native close failed.");
        await using var connection = new EnvironmentCleanupFailureConnection { CloseFailure = secondary };
        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.GetEnvironmentAsync(context, CancellationToken.None));

        // Assert
        Assert.Same(secondary, failure);
        Assert.Equal(2, connection.VersionReads);
        Assert.Equal(1, connection.CloseAttempts);
    }

    /// <summary>Environment reads close only the connection scope opened by the analyzer.</summary>
    /// <param name="initiallyOpen">Whether the caller already owns the open connection scope.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetEnvironmentAsync_PreservesCallerConnectionOwnership(
        bool initiallyOpen
    )
    {
        // Arrange
        await using var connection = new EnvironmentCleanupFailureConnection();
        if (initiallyOpen)
        {
            connection.Open();
        }

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var environment = await analyzer.GetEnvironmentAsync(context, CancellationToken.None);

        // Assert
        Assert.Equal("doka_mysql", environment.ProviderId);
        Assert.Equal("mysql", environment.EngineFamily);
        Assert.Equal("8.4.13", environment.ServerVersion);
        Assert.Equal(
            initiallyOpen
                ? 0
                : 1,
            connection.CloseAttempts);

        Assert.Equal(
            initiallyOpen
                ? ConnectionState.Open
                : ConnectionState.Closed,
            connection.State);
    }

    /// <summary>Actual pending close keeps environment analysis alive until native completion.</summary>
    /// <param name="metadataFails">Whether metadata already produced a primary failure.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetEnvironmentAsync_AwaitsActualCloseBeforeLeavingOwnership(
        bool metadataFails
    )
    {
        // Arrange
        var primary = new InvalidOperationException("Native version metadata failed.");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            EnvironmentFailure = metadataFails
                ? primary
                : null,
            CloseCompletion = release.Task,
        };

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        bool closeEntered;
        bool completedBeforeRelease;
        Exception? failure;

        // Act
        var attempt = Record.ExceptionAsync(() => analyzer.GetEnvironmentAsync(context, CancellationToken.None));
        try
        {
            _ = await Task.WhenAny(connection.CloseStarted.Task, attempt);
            closeEntered = connection.CloseStarted.Task.IsCompleted;
            completedBeforeRelease = attempt.IsCompleted;
        }
        finally
        {
            release.TrySetResult();
            failure = await attempt;
        }

        // Assert
        Assert.True(closeEntered);
        Assert.False(completedBeforeRelease);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Same(
            metadataFails
                ? primary
                : null,
            failure);
    }

    /// <summary>Diagnostic dictionary failures cannot replace environment or analysis work failures.</summary>
    /// <param name="environmentRead">
    /// Whether the original error comes from version metadata instead of a command.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalyzerCleanup_ThrowingDiagnosticDataPreservesOriginalFailure(
        bool environmentRead
    )
    {
        // Arrange
        var primary = new ThrowingCleanupDataException();
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            EnvironmentFailure = environmentRead
                ? primary
                : null,
            CommandFailure = environmentRead
                ? null
                : primary,
            CloseFailure = new InvalidOperationException("Native close failed."),
        };

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = CreateOperations(context);

        // Act
        var failure = await Record.ExceptionAsync(async () =>
        {
            if (environmentRead)
            {
                _ = await analyzer.GetEnvironmentAsync(context, CancellationToken.None);
            }
            else
            {
                _ = await analyzer.AnalyzeAsync(context, operations, CancellationToken.None);
            }
        });

        // Assert
        Assert.Same(primary, failure);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Equal(1, primary.DataAccesses);
    }
}
