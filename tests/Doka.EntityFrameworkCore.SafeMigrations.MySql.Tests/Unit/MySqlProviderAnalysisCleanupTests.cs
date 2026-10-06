namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies analysis failures and connection ownership survive asynchronous cleanup failures.</summary>
public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>A failed close cannot replace an analysis failure or cancellation.</summary>
    /// <param name="cancelled">Whether the primary failure represents cancellation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalysisAndCloseFailurePreserveThePrimaryException(
        bool cancelled
    )
    {
        // Arrange
        Exception primary = cancelled
            ? new OperationCanceledException("Analysis cancelled.")
            : new InvalidOperationException("Analysis failed.");

        var secondary = new InvalidOperationException("Connection close failed.");
        await using var connection = new AnalysisConnection(initiallyOpen: false)
        {
            CommandFailure = primary,
            CloseFailure = secondary,
        };

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = CreateOperations(context);

        // Act
        var actual = await Assert.ThrowsAnyAsync<Exception>(() => analyzer.AnalyzeAsync(context, operations));

        // Assert
        Assert.Same(primary, actual);
        Assert.Same(secondary, actual.Data["SafeMigrations.AnalysisConnectionCloseException"]);
        Assert.Equal(1, connection.CloseAttempts);
    }

    /// <summary>An ordinary analysis failure still closes a connection opened by the analyzer.</summary>
    [Fact]
    public async Task AnalysisFailureClosesOwnedConnectionWithoutSecondaryDiagnostics()
    {
        // Arrange
        var primary = new InvalidOperationException("Analysis failed.");
        await using var connection = new AnalysisConnection(initiallyOpen: false) { CommandFailure = primary };
        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = CreateOperations(context);

        // Act
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => analyzer.AnalyzeAsync(context, operations));

        // Assert
        Assert.Same(primary, actual);
        Assert.Empty(actual.Data);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>Failure cleanup must not close a connection that was already open on entry.</summary>
    [Fact]
    public async Task AnalysisFailureDoesNotCloseCallerOwnedConnection()
    {
        // Arrange
        var primary = new InvalidOperationException("Analysis failed.");
        await using var connection = new AnalysisConnection(initiallyOpen: true)
        {
            CommandFailure = primary,
            CloseFailure = new InvalidOperationException("Caller connection must not be closed."),
        };

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = CreateOperations(context);

        // Act
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => analyzer.AnalyzeAsync(context, operations));

        // Assert
        Assert.Same(primary, actual);
        Assert.Empty(actual.Data);
        Assert.Equal(0, connection.CloseAttempts);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>A close failure after otherwise successful analysis remains an observable failure.</summary>
    [Fact]
    public async Task SuccessfulAnalysisDoesNotSuppressCloseFailure()
    {
        // Arrange
        var closeFailure = new InvalidOperationException("Connection close failed.");
        await using var connection = new AnalysisConnection(initiallyOpen: false) { CloseFailure = closeFailure };
        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = CreateOperations(context);

        // Act
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => analyzer.AnalyzeAsync(context, operations));

        // Assert
        Assert.Same(closeFailure, actual);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Equal(1, connection.ClassificationCount);
    }

    /// <summary>Successful analysis closes only connections it opened itself.</summary>
    /// <param name="initiallyOpen">Whether the caller owns the open connection scope.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulAnalysisRespectsConnectionOwnership(
        bool initiallyOpen
    )
    {
        // Arrange
        await using var connection = new AnalysisConnection(initiallyOpen);
        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var operations = CreateOperations(context);

        // Act
        var result = await analyzer.AnalyzeAsync(context, operations);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(result).ObservedState);
        Assert.Equal(initiallyOpen ? 0 : 1, connection.CloseAttempts);
        Assert.Equal(initiallyOpen ? ConnectionState.Open : ConnectionState.Closed, connection.State);
        Assert.Equal(1, connection.ClassificationCount);
    }
}
