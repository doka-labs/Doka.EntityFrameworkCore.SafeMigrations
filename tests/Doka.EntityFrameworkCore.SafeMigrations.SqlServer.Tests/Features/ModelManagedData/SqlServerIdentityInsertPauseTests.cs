namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies that attention readiness does not depend on informational TDS messages.
/// </summary>
public sealed class SqlServerIdentityInsertPauseTests
{
    /// <summary>
    /// Binds the observation to explicit SQL integer metadata without interpolating the resource.
    /// </summary>
    [Theory]
    [InlineData("ready_marker")]
    [InlineData("ready'_marker")]
    public void ReadinessCommand_UsesExplicitIntScalarAndParameterizedSessionMarker(
        string resource
    )
    {
        // Arrange
        using var observer = new SqlConnection();
        var interceptor = new SqlServerIdentityInsertPauseInterceptor(resource);

        // Act
        using var command = interceptor.CreateMarkerObservationCommand(observer);

        // Assert
        Assert.Same(observer, command.Connection);
        Assert.Equal("SELECT CONVERT(int, APPLOCK_TEST(N'public', @resource, N'Exclusive', N'Session'));",
            command.CommandText);
        Assert.Equal(5, command.CommandTimeout);
        var parameter = Assert.Single(command.Parameters.Cast<SqlParameter>());

        Assert.Equal("@resource", parameter.ParameterName);
        Assert.Equal(SqlDbType.NVarChar, parameter.SqlDbType);
        Assert.Equal(255, parameter.Size);
        Assert.Equal(resource, parameter.Value);
    }

    /// <summary>
    /// Accepts only the exact SQL integer decisions for an available or held marker.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ReadinessDecision_ExactIntZeroOrOneMapsToAvailability(
        int result,
        bool expectedAvailable
    )
    {
        // Arrange
        object scalar = result;

        // Act
        var available = SqlServerIdentityInsertPauseInterceptor.ParseMarkerAvailability(scalar);

        // Assert
        Assert.Equal(expectedAvailable, available);
    }

    /// <summary>
    /// Rejects null, malformed, coercible, and out-of-domain observations rather than certifying readiness.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData((short)0)]
    [InlineData((short)1)]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData("0")]
    [InlineData("1")]
    public void ReadinessDecision_InvalidScalarFailsClosed(
        object? scalar
    )
    {
        // Arrange
        Func<bool> parse = () => SqlServerIdentityInsertPauseInterceptor.ParseMarkerAvailability(scalar);

        // Act
        var exception = Record.Exception(() => parse());

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);

        Assert.Equal("The readiness marker observation did not return a lock decision.", failure.Message);
    }

    /// <summary>
    /// Rejects a database NULL instead of treating it as an available or held marker.
    /// </summary>
    [Fact]
    public void ReadinessDecision_DatabaseNullFailsClosed()
    {
        // Arrange
        var scalar = DBNull.Value;

        // Act
        var exception = Record.Exception(() =>
            SqlServerIdentityInsertPauseInterceptor.ParseMarkerAvailability(scalar));

        // Assert
        var failure = Assert.IsType<InvalidOperationException>(exception);

        Assert.Equal("The readiness marker observation did not return a lock decision.", failure.Message);
    }

    /// <summary>
    /// Publishes an observable marker after ON and preserves nested SQL literal quoting.
    /// </summary>
    [Theory]
    [InlineData(false, "ready_marker", "ready_marker")]
    [InlineData(false, "ready'_marker", "ready''''_marker")]
    [InlineData(true, "ready_marker", "ready_marker")]
    [InlineData(true, "ready'_marker", "ready''''_marker")]
    public void PauseSql_MarkerFollowsIdentityOnWithoutFlushingInfoMessages(
        bool isolatedGuard,
        string resource,
        string nestedResource
    )
    {
        // Arrange
        const string sql = "EXEC sys.sp_executesql N'SET IDENTITY_INSERT [dbo].[identity_roles] ON; "
            + "INSERT INTO dbo.identity_roles (Id, Caption) VALUES (7, N''Administrator''); "
            + "SET IDENTITY_INSERT [dbo].[identity_roles] OFF;';";

        var command = isolatedGuard
            ? SqlServerGuardedSqlTestContract.EncodeScope("DECLARE @doka_state nvarchar(32);\n" + sql)
            : sql;

        // Act
        var paused = SqlServerIdentityInsertPauseInterceptor.BuildPausedCommand(command, resource);
        var body = isolatedGuard ? SqlServerGuardedSqlTestContract.DecodeScope(paused) : paused;
        var baseline = SqlServerGuardedSqlTestContract.DecodeScope(
            body.Substring(body.IndexOf("EXEC sys.sp_executesql N'", StringComparison.Ordinal)));

        var identityOn = body.IndexOf("SET IDENTITY_INSERT [dbo].[identity_roles] ON;", StringComparison.Ordinal);
        var marker = body.IndexOf("EXEC @doka_identity_ready = sys.sp_getapplock", StringComparison.Ordinal);
        var pause = body.IndexOf("WAITFOR DELAY ''00:05:00'';", StringComparison.Ordinal);
        var insert = body.IndexOf("INSERT INTO dbo.identity_roles", StringComparison.Ordinal);

        // Assert
        Assert.True(identityOn >= 0 && identityOn < marker && marker < pause && pause < insert);
        Assert.Contains("@Resource = N''" + nestedResource + "''", body, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = N''Session''", body, StringComparison.Ordinal);
        Assert.Contains("@LockTimeout = 0", body, StringComparison.Ordinal);
        Assert.Contains("@Resource = N'" + resource.Replace("'", "''", StringComparison.Ordinal) + "'",
            baseline, StringComparison.Ordinal);
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
