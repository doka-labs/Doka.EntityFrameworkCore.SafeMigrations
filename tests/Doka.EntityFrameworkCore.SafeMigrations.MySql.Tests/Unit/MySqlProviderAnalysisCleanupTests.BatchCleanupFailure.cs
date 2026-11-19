namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Every classifier-owned batch preserves its actual read or cancellation error.</summary>
    /// <param name="stage">The exact real analyzer classifier phase selected by its unique producer marker.</param>
    /// <param name="native">Whether the actual transport exposes a native ADO batch.</param>
    /// <param name="canceled">Whether actual reader work cancels the caller's token before failing.</param>
    [Theory]
    [InlineData("classification", false, false)]
    [InlineData("classification", false, true)]
    [InlineData("classification", true, false)]
    [InlineData("classification", true, true)]
    [InlineData("qualification", false, false)]
    [InlineData("qualification", false, true)]
    [InlineData("qualification", true, false)]
    [InlineData("qualification", true, true)]
    [InlineData("prerequisite", false, false)]
    [InlineData("prerequisite", false, true)]
    [InlineData("prerequisite", true, false)]
    [InlineData("prerequisite", true, true)]
    [InlineData("state_guard", false, false)]
    [InlineData("state_guard", false, true)]
    [InlineData("state_guard", true, false)]
    [InlineData("state_guard", true, true)]
    public async Task ClassifierBatchCleanup_PreservesOriginalReadFailure(
        string stage,
        bool native,
        bool canceled
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Native classifier read failed.");

        var secondary = new InvalidOperationException("Native classifier batch disposal failed.");
        var state = new ProbeCleanupFailureState(stage)
        {
            BatchMarker = "batch_cleanup_" + stage,
            WorkFailure = primary,
            DisposalFailure = secondary,
            BeforeWork = canceled
                ? _ => cancellation.Cancel()
                : null,
        };

        await using var commands = CreateBatchCleanupCommands(native);
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = () => new ProbeCleanupFailureCommand(commands.CreateCommand(), state),
            BatchFactory = native
                ? () => new BatchCleanupFailureBatch(commands.CreateBatch(), state)
                : null,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateBatchCleanupAnalyzer(context, stage);
        var operations = CreateOperations(context);

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.AnalyzeAsync(context, operations, cancellation.Token));

        // Assert
        Assert.Same(primary, failure);
        Assert.Same(secondary, primary.Data["SafeMigrations.AnalysisBatchDisposeException"]);
        Assert.Equal(cancellation.Token, state.WorkToken);
        Assert.Equal(1, state.TargetDisposals);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Equal(
            native
                ? "batch"
                : "command",
            Assert.Single(state.CompletedStages));

        Assert.Contains(commands.Submissions, statement => state.TargetsBatchStatement(statement.Sql));
        if (canceled)
        {
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(cancellation.Token,
                Assert.IsType<OperationCanceledException>(failure)
                    .CancellationToken);
        }
    }

    /// <summary>Cleanup-only native or sequential batch failures remain errors after successful reads.</summary>
    /// <param name="stage">The classifier phase whose owned resource disposal fails.</param>
    /// <param name="native">Whether the actual transport exposes a native ADO batch.</param>
    [Theory]
    [InlineData("classification", false)]
    [InlineData("classification", true)]
    [InlineData("qualification", false)]
    [InlineData("qualification", true)]
    [InlineData("prerequisite", false)]
    [InlineData("prerequisite", true)]
    [InlineData("state_guard", false)]
    [InlineData("state_guard", true)]
    public async Task ClassifierBatchCleanup_CloseOnlyFailureRemainsObservable(
        string stage,
        bool native
    )
    {
        // Arrange
        var secondary = new InvalidOperationException("Native classifier batch disposal failed.");
        var state = new ProbeCleanupFailureState(stage)
        {
            BatchMarker = "batch_cleanup_" + stage,
            DisposalFailure = secondary,
        };

        await using var commands = CreateBatchCleanupCommands(native);
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = () => new ProbeCleanupFailureCommand(commands.CreateCommand(), state),
            BatchFactory = native
                ? () => new BatchCleanupFailureBatch(commands.CreateBatch(), state)
                : null,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateBatchCleanupAnalyzer(context, stage);
        var operations = CreateOperations(context);

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.AnalyzeAsync(context, operations, CancellationToken.None));

        // Assert
        Assert.Same(secondary, failure);
        Assert.Equal(1, state.TargetDisposals);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Contains(commands.Submissions, statement => state.TargetsBatchStatement(statement.Sql));
    }

    /// <summary>Actual classifier batch disposal drains before outer connection ownership ends.</summary>
    /// <param name="stage">The exact classifier-owned resource held behind its observable readiness signal.</param>
    /// <param name="native">Whether the actual transport exposes a native ADO batch.</param>
    /// <param name="workFails">Whether native read work already produced the original failure.</param>
    [Theory]
    [InlineData("classification", false, false)]
    [InlineData("classification", false, true)]
    [InlineData("classification", true, false)]
    [InlineData("classification", true, true)]
    [InlineData("qualification", false, false)]
    [InlineData("qualification", false, true)]
    [InlineData("qualification", true, false)]
    [InlineData("qualification", true, true)]
    [InlineData("prerequisite", false, false)]
    [InlineData("prerequisite", false, true)]
    [InlineData("prerequisite", true, false)]
    [InlineData("prerequisite", true, true)]
    [InlineData("state_guard", false, false)]
    [InlineData("state_guard", false, true)]
    [InlineData("state_guard", true, false)]
    [InlineData("state_guard", true, true)]
    public async Task ClassifierBatchCleanup_DrainsActualDisposalBeforeConnectionClose(
        string stage,
        bool native,
        bool workFails
    )
    {
        // Arrange
        var primary = new InvalidOperationException("Native classifier read failed.");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ProbeCleanupFailureState(stage)
        {
            BatchMarker = "batch_cleanup_" + stage,
            WorkFailure = workFails
                ? primary
                : null,
            DisposalCompletion = release.Task,
        };

        await using var commands = CreateBatchCleanupCommands(native);
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = () => new ProbeCleanupFailureCommand(commands.CreateCommand(), state),
            BatchFactory = native
                ? () => new BatchCleanupFailureBatch(commands.CreateBatch(), state)
                : null,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateBatchCleanupAnalyzer(context, stage);
        var operations = CreateOperations(context);
        bool disposalEntered;
        bool completedBeforeRelease;
        int closeAttemptsBeforeRelease;
        Exception? failure;

        // Act
        var attempt = Record.ExceptionAsync(() => analyzer.AnalyzeAsync(context, operations, CancellationToken.None));
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

    /// <summary>Positive native/sequential controls prove every isolated classifier can publish its result.</summary>
    /// <param name="stage">The exact classifier phase whose generated SQL must actually be observed.</param>
    /// <param name="native">Whether the actual transport exposes a native ADO batch.</param>
    [Theory]
    [InlineData("classification", false)]
    [InlineData("classification", true)]
    [InlineData("qualification", false)]
    [InlineData("qualification", true)]
    [InlineData("prerequisite", false)]
    [InlineData("prerequisite", true)]
    [InlineData("state_guard", false)]
    [InlineData("state_guard", true)]
    public async Task ClassifierBatchCleanup_PositiveControlPublishesMatchingResult(
        string stage,
        bool native
    )
    {
        // Arrange
        var state = new ProbeCleanupFailureState(stage) { BatchMarker = "batch_cleanup_" + stage };
        await using var commands = CreateBatchCleanupCommands(native);
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = () => new ProbeCleanupFailureCommand(commands.CreateCommand(), state),
            BatchFactory = native
                ? () => new BatchCleanupFailureBatch(commands.CreateBatch(), state)
                : null,
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateBatchCleanupAnalyzer(context, stage);
        var operations = CreateOperations(context);

        // Act
        var results = await analyzer.AnalyzeAsync(context, operations, CancellationToken.None);

        // Assert
        var result = Assert.Single(results);
        Assert.Equal(SafeMigrationObservedState.Matching, result.ObservedState);
        Assert.True(result.PostconditionSatisfied);
        Assert.Equal(1, state.TargetDisposals);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Contains(commands.Submissions, statement => state.TargetsBatchStatement(statement.Sql));
    }
}
