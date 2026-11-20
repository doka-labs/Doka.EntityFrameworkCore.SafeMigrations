namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Actual proof recovery cannot replace a native failure when diagnostic Data is unavailable.</summary>
    /// <param name="stage">The native proof family whose failure recovery executes.</param>
    /// <param name="throwing">Whether Data throws instead of returning a read-only dictionary.</param>
    [Theory]
    [InlineData("binary_proof", false)]
    [InlineData("binary_proof", true)]
    [InlineData("identifier_proof", false)]
    [InlineData("identifier_proof", true)]
    public async Task ProofCleanup_HostileDataCannotReplaceNativeFailure(
        string stage,
        bool throwing
    )
    {
        // Arrange
        var primary = new MySqlCleanupEvidenceException(throwing);
        var secondary = new InvalidOperationException("Native proof recovery failed.");
        var state = new ProbeCleanupFailureState(stage)
        {
            BatchMarker = stage == "binary_proof"
                ? "@doka_sm_binary_default_sql"
                : "@doka_sm_ir_result_",
            WorkFailure = primary,
            CleanupQueryFailure = secondary,
        };

        await using var commands = CreateBatchCleanupCommands(native: false);
        await using var connection = new EnvironmentCleanupFailureConnection
        {
            CommandFactory = () => new ProbeCleanupFailureCommand(commands.CreateCommand(), state),
        };

        await using var context = CreateContext(connection);
        var analyzer = CreateBatchCleanupAnalyzer(context, stage);
        var operations = CreateOperations(context);

        // Act
        var failure = await Record.ExceptionAsync(() =>
            analyzer.AnalyzeAsync(context, operations, CancellationToken.None));

        // Assert
        Assert.Same(primary, failure);
        Assert.Equal(1, primary.DataAccesses);
        Assert.Equal(1, state.CleanupAttempts);
        Assert.Equal(CancellationToken.None, state.CleanupToken);
        Assert.Equal(1, connection.CloseAttempts);
        Assert.Contains(commands.Submissions, statement => state.TargetsBatchStatement(statement.Sql));
    }
}
