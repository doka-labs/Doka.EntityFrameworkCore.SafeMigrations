namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Owned scalar/reader acquisition cleanup preserves the original work or cancellation failure.</summary>
    /// <param name="stage">The actual scalar, reader-acquisition or reader-consumption failure boundary.</param>
    /// <param name="canceled">Whether actual work cancels the caller's token before raising its failure.</param>
    /// <param name="initiallyOpen">Whether the connection belongs to an existing caller-owned open scope.</param>
    [Theory]
    [InlineData("database", false, false)]
    [InlineData("database", false, true)]
    [InlineData("database", true, false)]
    [InlineData("database", true, true)]
    [InlineData("transport_command", false, false)]
    [InlineData("transport_command", false, true)]
    [InlineData("transport_command", true, false)]
    [InlineData("transport_command", true, true)]
    [InlineData("transport_reader", false, false)]
    [InlineData("transport_reader", false, true)]
    [InlineData("transport_reader", true, false)]
    [InlineData("transport_reader", true, true)]
    public async Task AcquisitionCleanup_PreservesOriginalFailure(
        string stage,
        bool canceled,
        bool initiallyOpen
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Native acquisition work failed.");

        var secondary = new InvalidOperationException("Native acquisition disposal failed.");
        var tertiary = new InvalidOperationException("Native command disposal after reader failed.");
        var state = new ProbeCleanupFailureState(stage)
        {
            WorkFailure = primary,
            DisposalFailure = secondary,
            CommandAfterReaderFailure = tertiary,
            BeforeWork = canceled
                ? _ => cancellation.Cancel()
                : null,
        };

        await using var commands = CreateInventoryCleanupCommands();
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = () => new ProbeCleanupFailureCommand(commands.CreateCommand(), state),
        };

        if (initiallyOpen)
        {
            connection.Open();
        }

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, [], cancellation.Token));

        // Assert
        Assert.Same(primary, failure);
        Assert.Same(
            secondary,
            primary.Data[stage == "transport_reader"
                ? "SafeMigrations.AnalysisReaderDisposeException"
                : "SafeMigrations.AnalysisCommandDisposeException"]);

        Assert.Equal(cancellation.Token, state.WorkToken);
        Assert.Equal(1, state.TargetDisposals);
        Assert.Equal(
            initiallyOpen
                ? 0
                : 1,
            connection.CloseAttempts);

        if (stage == "transport_reader")
        {
            Assert.Same(tertiary, primary.Data["SafeMigrations.AnalysisCommandDisposeException"]);
            Assert.Equal(["reader", "command"], state.CompletedStages);
        }
        else
        {
            Assert.Equal(["command"], state.CompletedStages);
        }

        if (canceled)
        {
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(cancellation.Token,
                Assert.IsType<OperationCanceledException>(failure)
                    .CancellationToken);
        }
    }

    /// <summary>Cleanup-only failures remain visible under owned and borrowed connection scopes.</summary>
    /// <param name="stage">The exact native cleanup boundary that fails after successful work.</param>
    /// <param name="initiallyOpen">Whether the open connection scope belongs to the caller.</param>
    [Theory]
    [InlineData("database", false)]
    [InlineData("database", true)]
    [InlineData("transport_command", false)]
    [InlineData("transport_command", true)]
    [InlineData("transport_reader", false)]
    [InlineData("transport_reader", true)]
    public async Task AcquisitionCleanup_CloseOnlyFailureRemainsObservable(
        string stage,
        bool initiallyOpen
    )
    {
        // Arrange
        var secondary = new InvalidOperationException("Native acquisition disposal failed.");
        var tertiary = new InvalidOperationException("Native command disposal after reader failed.");
        var state = new ProbeCleanupFailureState(stage)
        {
            DisposalFailure = secondary,
            CommandAfterReaderFailure = tertiary,
        };

        await using var commands = CreateInventoryCleanupCommands();
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = () => new ProbeCleanupFailureCommand(commands.CreateCommand(), state),
        };

        if (initiallyOpen)
        {
            connection.Open();
        }

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, [], CancellationToken.None));

        // Assert
        Assert.Same(secondary, failure);
        Assert.Equal(1, state.TargetDisposals);
        Assert.Equal(
            initiallyOpen
                ? 0
                : 1,
            connection.CloseAttempts);

        if (stage == "transport_reader")
        {
            Assert.Same(tertiary, secondary.Data["SafeMigrations.AnalysisCommandDisposeException"]);
            Assert.Equal(["reader", "command"], state.CompletedStages);
        }
    }

    /// <summary>Actual acquisition disposal drains before the enclosing connection lifetime can close.</summary>
    /// <param name="stage">The exact native disposal suspended behind observable readiness.</param>
    /// <param name="workFails">Whether native work already produced the original failure.</param>
    [Theory]
    [InlineData("database", false)]
    [InlineData("database", true)]
    [InlineData("transport_command", false)]
    [InlineData("transport_command", true)]
    [InlineData("transport_reader", false)]
    [InlineData("transport_reader", true)]
    public async Task AcquisitionCleanup_DrainsActualDisposalBeforeConnectionClose(
        string stage,
        bool workFails
    )
    {
        // Arrange
        var primary = new InvalidOperationException("Native acquisition work failed.");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ProbeCleanupFailureState(stage)
        {
            WorkFailure = workFails
                ? primary
                : null,
            DisposalCompletion = release.Task,
        };

        await using var commands = CreateInventoryCleanupCommands();
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = () => new ProbeCleanupFailureCommand(commands.CreateCommand(), state),
        };

        await using var context = CreateContext(connection);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        bool disposalEntered;
        bool completedBeforeRelease;
        int closeAttemptsBeforeRelease;
        Exception? failure;

        // Act
        var attempt = Record.ExceptionAsync(() =>
            analyzer.FindUnexpectedObjectsAsync(context, [], CancellationToken.None));

        try
        {
            _ = await Task.WhenAny(state.DisposalStarted.Task, attempt);
            disposalEntered = state.DisposalStarted.Task.IsCompleted;
            completedBeforeRelease = attempt.IsCompleted;
            closeAttemptsBeforeRelease = connection.CloseAttempts;
        }
        finally
        {
            release.TrySetResult();
            failure = await attempt;
        }

        // Assert
        Assert.True(disposalEntered);
        Assert.False(completedBeforeRelease);
        Assert.Equal(0, closeAttemptsBeforeRelease);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Equal(1, state.TargetDisposals);
        Assert.Same(
            workFails
                ? primary
                : null,
            failure);
    }

    /// <summary>Retains only the explicit native fault and readiness controls for one acquisition boundary.</summary>
    private sealed class ProbeCleanupFailureState
    {
        /// <summary>Selects the exact scalar, command-acquisition or reader-consumption boundary.</summary>
        /// <param name="stage">The closed set of stage names declared by these regression tests.</param>
        internal ProbeCleanupFailureState(
            string stage
        )
        {
            Stage = stage;
        }

        /// <summary>Gets the selected actual acquisition or disposal stage.</summary>
        internal string Stage { get; }

        /// <summary>Gets the unique producer marker selecting a classifier-owned batch, when applicable.</summary>
        internal string? BatchMarker { get; init; }

        /// <summary>Gets the exact native session-metadata marker selecting an acquisition scope.</summary>
        internal string? EnvironmentMarker { get; init; }

        /// <summary>Gets or sets the exact native metadata SQL observed at reader acquisition.</summary>
        internal string? EnvironmentCommandText { get; set; }

        /// <summary>Gets the primary native work error, or null for the positive work control.</summary>
        internal Exception? WorkFailure { get; init; }

        /// <summary>Gets the selected actual resource disposal error, or null for drain-only controls.</summary>
        internal Exception? DisposalFailure { get; init; }

        /// <summary>Gets the independent command error that follows a failing reader disposal.</summary>
        internal Exception? CommandAfterReaderFailure { get; init; }

        /// <summary>Gets the independently tokened native proof-release failure.</summary>
        internal Exception? CleanupQueryFailure { get; init; }

        /// <summary>Gets or sets the actual number of proof-release executions.</summary>
        internal int CleanupAttempts { get; set; }

        /// <summary>Gets or sets the actual token passed to the native proof-release command.</summary>
        internal CancellationToken CleanupToken { get; set; }

        /// <summary>Gets the actual work callback used to request cancellation before a native failure.</summary>
        internal Action<CancellationToken>? BeforeWork { get; init; }

        /// <summary>Gets actual native disposal completion rather than a cancellation-aware observer.</summary>
        internal Task DisposalCompletion { get; init; } = Task.CompletedTask;

        /// <summary>Gets readiness proving the selected native resource is already being disposed.</summary>
        internal TaskCompletionSource DisposalStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the actual token forwarded to the selected native operation.</summary>
        internal CancellationToken WorkToken { get; private set; }

        /// <summary>Gets or sets the number of completed selected native disposal attempts.</summary>
        internal int TargetDisposals { get; set; }

        /// <summary>Gets observed actual reader/command completion order, including failing native attempts.</summary>
        internal List<string> CompletedStages { get; } = [];

        /// <summary>Matches only the native environment query under this fixture's observation.</summary>
        /// <param name="sql">The actual analyzer command text.</param>
        /// <returns>Whether the command acquires the selected environment.</returns>
        internal bool TargetsEnvironmentStatement(
            string sql
        ) => EnvironmentMarker is { } marker && sql.Contains(marker, StringComparison.Ordinal);

        /// <summary>Matches only the exact authored marker of this isolated classifier fixture.</summary>
        /// <param name="sql">The real statement generated by the analyzer.</param>
        /// <returns>Whether its owning classifier batch is the selected native fault boundary.</returns>
        internal bool TargetsBatchStatement(
            string sql
        ) => BatchMarker is { } marker && sql.Contains(marker, StringComparison.Ordinal);

        /// <summary>Raises the injected work failure only at the selected actual operation boundary.</summary>
        /// <param name="stage">The native operation currently executing.</param>
        /// <param name="cancellationToken">The actual caller token forwarded to native work.</param>
        internal void ObserveWork(
            string stage,
            CancellationToken cancellationToken
        )
        {
            if (Stage != stage)
            {
                return;
            }

            WorkToken = cancellationToken;
            BeforeWork?.Invoke(cancellationToken);
            if (WorkFailure is { } failure)
            {
                throw failure;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>Completes the actual selected native disposal, including its observable fault.</summary>
        /// <param name="stage">The exact reader or command resource being disposed.</param>
        /// <returns>Completion of the actual native resource operation, without an observer timeout.</returns>
        internal async ValueTask DisposeTargetAsync(
            string stage
        )
        {
            DisposalStarted.TrySetResult();
            await DisposalCompletion;
            TargetDisposals++;
            CompletedStages.Add(stage);
            if (DisposalFailure is { } failure)
            {
                throw failure;
            }
        }
    }
}
