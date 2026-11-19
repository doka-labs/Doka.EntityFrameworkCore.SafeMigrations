namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Inventory retains the original metadata error or cancellation if actual close also fails.</summary>
    /// <param name="canceled">Whether command creation cancels the caller's operation token.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindUnexpectedObjectsAsync_WorkAndCloseFailurePreservePrimary(
        bool canceled
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Native inventory metadata failed.");

        var secondary = new InvalidOperationException("Native inventory close failed.");
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFailure = primary,
            CloseFailure = secondary,
            BeforeCommand = canceled
                ? cancellation.Cancel
                : null,
        };

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, [], cancellation.Token));

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

    /// <summary>A successful inventory still surfaces a close-only failure rather than inventing success.</summary>
    [Fact]
    public async Task FindUnexpectedObjectsAsync_CloseOnlyFailureRemainsObservable()
    {
        // Arrange
        var secondary = new InvalidOperationException("Native inventory close failed.");
        await using var commands = CreateInventoryCleanupCommands();
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = commands.CreateCommand,
            CloseFailure = secondary,
        };

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, [], CancellationToken.None));

        // Assert
        Assert.Same(secondary, failure);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Contains(commands.Submissions,
            static statement => statement.Sql.Contains(
                "SELECT 'table', t.TABLE_NAME",
                StringComparison.Ordinal));
    }

    /// <summary>Inventory owns only an open scope that it actually acquired.</summary>
    /// <param name="initiallyOpen">Whether an existing caller scope owns the open connection.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindUnexpectedObjectsAsync_PreservesCallerConnectionOwnership(
        bool initiallyOpen
    )
    {
        // Arrange
        await using var commands = CreateInventoryCleanupCommands();
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = commands.CreateCommand,
        };

        if (initiallyOpen)
        {
            connection.Open();
        }

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var findings = await analyzer.FindUnexpectedObjectsAsync(context, [], CancellationToken.None);

        // Assert
        Assert.Empty(findings);
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

    /// <summary>A borrowed open connection is not closed even when both injected work and close faults exist.</summary>
    [Fact]
    public async Task FindUnexpectedObjectsAsync_BorrowedFailureNeverClosesCallerScope()
    {
        // Arrange
        var primary = new InvalidOperationException("Native inventory metadata failed.");
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFailure = primary,
            CloseFailure = new InvalidOperationException("Must never close a borrowed connection."),
        };

        connection.Open();
        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, [], CancellationToken.None));

        // Assert
        Assert.Same(primary, failure);
        Assert.Equal(0, connection.CloseAttempts);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Empty(primary.Data);
    }

    /// <summary>A throwing diagnostic dictionary cannot replace the original inventory failure.</summary>
    [Fact]
    public async Task FindUnexpectedObjectsAsync_ThrowingDiagnosticDataPreservesOriginalFailure()
    {
        // Arrange
        var primary = new ThrowingCleanupDataException();
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFailure = primary,
            CloseFailure = new InvalidOperationException("Native inventory close failed."),
        };

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, [], CancellationToken.None));

        // Assert
        Assert.Same(primary, failure);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Equal(1, primary.DataAccesses);
    }

    /// <summary>The inventory lifetime awaits actual native close under both successful and failed work.</summary>
    /// <param name="metadataFails">Whether metadata already produced a primary failure.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindUnexpectedObjectsAsync_AwaitsActualCloseBeforeLeavingOwnership(
        bool metadataFails
    )
    {
        // Arrange
        var primary = new InvalidOperationException("Native inventory metadata failed.");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var commands = CreateInventoryCleanupCommands();
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFailure = metadataFails
                ? primary
                : null,
            CommandFactory = commands.CreateCommand,
            CloseCompletion = release.Task,
        };

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        bool closeEntered;
        bool completedBeforeRelease;
        Exception? failure;

        // Act
        var attempt = Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, [], CancellationToken.None));

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
        Assert.Same(
            metadataFails
                ? primary
                : null,
            failure);

        Assert.Equal(1, connection.CloseAttempts);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>Supplies only the two native result shapes required by an actually empty inventory.</summary>
    /// <returns>The deterministic command owner, disposed by each test after the analyzer completes.</returns>
    private static CatalogStatementTestConnection CreateInventoryCleanupCommands() => new(
        native: false,
        static (sql, _) =>
        {
            var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            if (sql == "SELECT @@max_allowed_packet, @@lower_case_table_names;")
            {
                table.Columns.Add("packet", typeof(long));
                table.Columns.Add("ownerMode", typeof(int));
                table.Rows.Add(16L * 1024 * 1024, 0);

                return table;
            }

            Assert.Contains("SELECT 'table', t.TABLE_NAME", sql, StringComparison.Ordinal);
            table.Columns.Add("kind", typeof(string));
            table.Columns.Add("table", typeof(string));
            table.Columns.Add("name", typeof(string));
            table.Columns.Add("providerGenerated", typeof(bool));

            return table;
        })
    {
        Scalar = static sql => sql == "SELECT DATABASE();"
            ? "environment_cleanup"
            : throw new InvalidOperationException("Unexpected inventory scalar query."),
    };
}
