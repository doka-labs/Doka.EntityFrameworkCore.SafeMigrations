namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Native metadata work retains its exact failure across owned resource disposal.</summary>
    /// <param name="stage">The native metadata command or reader boundary under test.</param>
    /// <param name="canceled">Whether native work requests cancellation before failing.</param>
    /// <param name="initiallyOpen">Whether connection ownership remains with an existing caller.</param>
    [Theory]
    [InlineData("index_command", false, false)]
    [InlineData("index_command", true, true)]
    [InlineData("index_reader", false, true)]
    [InlineData("index_reader", true, false)]
    [InlineData("column_command", false, false)]
    [InlineData("column_command", true, true)]
    [InlineData("column_reader", false, true)]
    [InlineData("column_reader", true, false)]
    public async Task MetadataCleanup_PreservesOriginalWorkFailure(
        string stage,
        bool canceled,
        bool initiallyOpen
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled
            ? new OperationCanceledException(cancellation.Token)
            : new InvalidOperationException("Native metadata work failed.");

        var secondary = new InvalidOperationException("Native metadata disposal failed.");
        var tertiary = new InvalidOperationException("Native metadata command disposal failed after reader.");
        var state = CreateMetadataCleanupState(stage,
            primary,
            secondary,
            tertiary,
            canceled
                ? _ => cancellation.Cancel()
                : null);

        await using var commands = CreateMetadataCleanupCommands(stage);
        await using var connection = CreateMetadataCleanupConnection(commands, state, initiallyOpen);
        await using var context = CreateContext(connection);
        var analyzer = CreateBatchCleanupAnalyzer(context, "classification");
        var operations = CreateMetadataCleanupOperations(stage);

        // Act
        var failure = await Record.ExceptionAsync(() => analyzer.AnalyzeAsync(context, operations, cancellation.Token));

        // Assert
        Assert.Same(primary, failure);
        Assert.Equal(cancellation.Token, state.WorkToken);
        Assert.Equal(1, state.TargetDisposals);
        Assert.Equal(initiallyOpen
                ? 0
                : 1,
            connection.CloseAttempts);

        Assert.Same(secondary,
            primary.Data[stage.EndsWith("_reader", StringComparison.Ordinal)
                ? "SafeMigrations.AnalysisReaderDisposeException"
                : "SafeMigrations.AnalysisCommandDisposeException"]);

        if (stage.EndsWith("_reader", StringComparison.Ordinal))
        {
            Assert.Same(tertiary, primary.Data["SafeMigrations.AnalysisCommandDisposeException"]);
            Assert.Equal(["reader", "command"], state.CompletedStages);
        }

        Assert.True(state.TargetsEnvironmentStatement(Assert.IsType<string>(state.EnvironmentCommandText)));
        if (stage.EndsWith("_reader", StringComparison.Ordinal))
        {
            Assert.Contains(commands.Submissions, statement => state.TargetsEnvironmentStatement(statement.Sql));
        }
    }

    /// <summary>Successful metadata reads do not turn an actual cleanup-only failure into success.</summary>
    /// <param name="stage">The native metadata resource that fails during disposal.</param>
    [Theory]
    [InlineData("index_command")]
    [InlineData("index_reader")]
    [InlineData("column_command")]
    [InlineData("column_reader")]
    public async Task MetadataCleanup_CleanupOnlyFailureRemainsObservable(
        string stage
    )
    {
        // Arrange
        var secondary = new InvalidOperationException("Native metadata disposal failed.");
        var state = CreateMetadataCleanupState(stage, null, secondary, null, null);
        await using var commands = CreateMetadataCleanupCommands(stage);
        await using var connection = CreateMetadataCleanupConnection(commands, state, initiallyOpen: false);
        await using var context = CreateContext(connection);
        var analyzer = CreateBatchCleanupAnalyzer(context, "classification");
        var operations = CreateMetadataCleanupOperations(stage);

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.AnalyzeAsync(context, operations, CancellationToken.None));

        // Assert
        Assert.Same(secondary, failure);
        Assert.Equal(1, state.TargetDisposals);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Contains(commands.Submissions, statement => state.TargetsEnvironmentStatement(statement.Sql));
    }

    /// <summary>Actual pending metadata disposal must finish before the original work failure escapes.</summary>
    /// <param name="stage">The selected reader or command whose actual disposal is held pending.</param>
    [Theory]
    [InlineData("index_command")]
    [InlineData("index_reader")]
    [InlineData("column_command")]
    [InlineData("column_reader")]
    public async Task MetadataCleanup_DrainsPendingNativeDisposal(
        string stage
    )
    {
        // Arrange
        var primary = new InvalidOperationException("Native metadata work failed.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ProbeCleanupFailureState(stage)
        {
            EnvironmentMarker = MetadataMarker(stage),
            WorkFailure = primary,
            DisposalCompletion = completion.Task,
        };

        await using var commands = CreateMetadataCleanupCommands(stage);
        await using var connection = CreateMetadataCleanupConnection(commands, state, initiallyOpen: false);
        await using var context = CreateContext(connection);
        var analyzer = CreateBatchCleanupAnalyzer(context, "classification");
        var operations = CreateMetadataCleanupOperations(stage);
        Exception? failure;
        bool pendingAtDisposal;

        // Act
        var execution = Record.ExceptionAsync(() => analyzer.AnalyzeAsync(context, operations, CancellationToken.None));
        try
        {
            await Task.WhenAny(state.DisposalStarted.Task, execution);
            pendingAtDisposal = state.DisposalStarted.Task.IsCompletedSuccessfully && !execution.IsCompleted;
        }
        finally
        {
            completion.TrySetResult();
            failure = await execution;
        }

        // Assert
        Assert.True(pendingAtDisposal);
        Assert.Same(primary, failure);
        Assert.Equal(1, state.TargetDisposals);
        Assert.Equal(1, connection.CloseAttempts);
    }

    /// <summary>Successful metadata resource disposal leaves a later, unrelated failure untouched.</summary>
    /// <param name="stage">The exact resource scope that must complete before the later catalog query.</param>
    [Theory]
    [InlineData("index_command")]
    [InlineData("index_reader")]
    [InlineData("column_command")]
    [InlineData("column_reader")]
    public async Task MetadataCleanup_PositiveScopePreservesLaterFailure(
        string stage
    )
    {
        // Arrange
        var later = new InvalidOperationException("The independently selected later catalog query failed.");
        var state = CreateMetadataCleanupState(stage, null, null, null, null);
        await using var commands = CreateMetadataCleanupCommands(stage, later);
        await using var connection = CreateMetadataCleanupConnection(commands, state, initiallyOpen: true);
        await using var context = CreateContext(connection);
        var analyzer = CreateBatchCleanupAnalyzer(context, "classification");
        var operations = CreateMetadataCleanupOperations(stage);

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.AnalyzeAsync(context, operations, CancellationToken.None));

        // Assert
        Assert.Same(later, failure);
        Assert.Empty(later.Data);
        Assert.Equal(1, state.TargetDisposals);
        Assert.Equal(0, connection.CloseAttempts);
    }
}
