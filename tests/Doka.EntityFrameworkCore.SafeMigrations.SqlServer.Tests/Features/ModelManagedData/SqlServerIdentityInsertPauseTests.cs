namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies that attention readiness does not depend on informational TDS messages.
/// </summary>
public sealed class SqlServerIdentityInsertPauseTests
{
    /// <summary>
    /// Publishes an observable marker after ON and preserves nested SQL literal quoting.
    /// </summary>
    [Theory]
    [InlineData("ready_marker", "ready_marker")]
    [InlineData("ready'_marker", "ready''''_marker")]
    public void PauseSql_MarkerFollowsIdentityOnWithoutFlushingInfoMessages(
        string resource,
        string nestedResource
    )
    {
        // Arrange
        const string sql = "EXEC sys.sp_executesql N'SET IDENTITY_INSERT [dbo].[identity_roles] ON; "
            + "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (7, N''Administrator''); "
            + "SET IDENTITY_INSERT [dbo].[identity_roles] OFF;';";

        // Act
        var paused = SqlServerIdentityInsertPauseInterceptor.BuildPausedCommand(sql, resource);
        var identityOn = paused.IndexOf("SET IDENTITY_INSERT [dbo].[identity_roles] ON;", StringComparison.Ordinal);
        var marker = paused.IndexOf("EXEC @doka_identity_ready = sys.sp_getapplock", StringComparison.Ordinal);
        var pause = paused.IndexOf("WAITFOR DELAY ''00:05:00'';", StringComparison.Ordinal);
        var insert = paused.IndexOf("INSERT INTO dbo.identity_roles", StringComparison.Ordinal);

        // Assert
        Assert.True(identityOn >= 0 && identityOn < marker && marker < pause && pause < insert);
        Assert.Contains("@Resource = N''" + nestedResource + "''", paused, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = N''Session''", paused, StringComparison.Ordinal);
        Assert.Contains("@LockTimeout = 0", paused, StringComparison.Ordinal);
        Assert.DoesNotContain("RAISERROR", paused, StringComparison.Ordinal);
        Assert.DoesNotContain("NOWAIT", paused, StringComparison.Ordinal);
    }

    /// <summary>
    /// Does not publish a false readiness marker for unrelated SQL or OFF-only cleanup.
    /// </summary>
    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("SET IDENTITY_INSERT [dbo].[identity_probe] ON;")]
    [InlineData("SET IDENTITY_INSERT [dbo].[identity_roles] OFF;")]
    public void PauseSql_WithoutTargetIdentityOnDoesNotPublishMarker(
        string sql
    )
    {
        // Arrange
        const string resource = "ready_marker";

        // Act
        var paused = SqlServerIdentityInsertPauseInterceptor.BuildPausedCommand(sql, resource);

        // Assert
        Assert.Equal(sql, paused);
        Assert.DoesNotContain("sp_getapplock", paused, StringComparison.Ordinal);
        Assert.DoesNotContain("WAITFOR", paused, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects an already completed command rather than certifying a timer-only cancellation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readiness_CommandCompletesBeforeMarkerIsNotAccepted(
        bool commandFails
    )
    {
        // Arrange
        await using var observer = new SqlConnection();
        var interceptor = new SqlServerIdentityInsertPauseInterceptor("ready_marker");
        var originalFailure = new InvalidOperationException("command failed before IDENTITY_INSERT ON");
        var execution = commandFails ? Task.FromException(originalFailure) : Task.CompletedTask;

        // Act
        var readinessFailure = await Record.ExceptionAsync(() => interceptor.WaitUntilReadyAsync(
            observer, execution, CancellationToken.None));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(readinessFailure);

        Assert.Equal(ConnectionState.Closed, observer.State);
        if (commandFails)
        {
            Assert.Same(originalFailure, failure);
        }
        else
        {
            Assert.Contains("completed before the readiness marker", failure.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A canceled observation cannot falsely certify that IDENTITY_INSERT has been enabled.
    /// </summary>
    [Fact]
    public async Task Readiness_CanceledObservationDoesNotCertifyIdentityOn()
    {
        // Arrange
        await using var observer = new SqlConnection();
        var interceptor = new SqlServerIdentityInsertPauseInterceptor("ready_marker");
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var readinessFailure = await Record.ExceptionAsync(() => interceptor.WaitUntilReadyAsync(
            observer, execution.Task, cancellation.Token));

        // Assert
        var failure = Assert.IsType<OperationCanceledException>(readinessFailure);

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.False(execution.Task.IsCompleted);
        Assert.Equal(ConnectionState.Closed, observer.State);
    }
}
