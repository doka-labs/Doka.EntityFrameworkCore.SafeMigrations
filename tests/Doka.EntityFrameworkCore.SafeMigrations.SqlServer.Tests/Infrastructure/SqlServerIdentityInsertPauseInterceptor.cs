namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Publishes attention readiness out of band without changing SqlClient's async TDS parsing path.
/// </summary>
internal sealed class SqlServerIdentityInsertPauseInterceptor
    : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
{
    private readonly string _resource;

    /// <summary>
    /// Creates a pause associated with a unique application lock in the test database.
    /// </summary>
    public SqlServerIdentityInsertPauseInterceptor(
        string resource
    ) => _resource = resource;

    /// <inheritdoc />
    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>>
        NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
    {
        command.CommandText = BuildPausedCommand(command.CommandText, _resource);

        return new ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>>(result);
    }

    /// <summary>
    /// Places the session marker after ON inside the generator's already quoted dynamic batch.
    /// </summary>
    public static string BuildPausedCommand(
        string commandText,
        string resource
    )
    {
        const string identityOn = "SET IDENTITY_INSERT [dbo].[identity_roles] ON;";
        var nestedResource = resource.Replace("'", "''''", StringComparison.Ordinal);

        // WHY: SqlClient drains an unparameterized async batch's first response under its cancellation lock.
        // NOWAIT starts that synchronous drain, but InfoMessage is deferred until the batch finishes.
        // A session-owned lock proves ON to a separate observer without emitting the early INFO token.
        return commandText.Replace(
            identityOn,
            identityOn + " DECLARE @doka_identity_ready int; "
                + "EXEC @doka_identity_ready = sys.sp_getapplock @Resource = N''" + nestedResource
                + "'', @LockMode = N''Exclusive'', @LockOwner = N''Session'', @LockTimeout = 0; "
                + "IF @doka_identity_ready < 0 THROW 51006, ''identity readiness lock failed'', 1; "
                + "WAITFOR DELAY ''00:05:00'';",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Waits for the marker while rejecting commands that finish before readiness can be observed.
    /// </summary>
    public async Task WaitUntilReadyAsync(
        SqlConnection observer,
        Task execution,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (execution.IsCompleted)
            {
                await execution;

                throw new InvalidOperationException("The identity command completed before the readiness marker.");
            }

            if (!await CanAcquireMarkerAsync(observer, cancellationToken))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    /// <summary>
    /// Observes the marker without acquiring it or using the connection running the migration.
    /// </summary>
    public async Task<bool> CanAcquireMarkerAsync(
        SqlConnection observer,
        CancellationToken cancellationToken = default
    )
    {
        await using var command = CreateMarkerObservationCommand(observer);
        var result = await command.ExecuteScalarAsync(cancellationToken);

        return ParseMarkerAvailability(result);
    }

    /// <summary>
    /// Creates a non-acquiring observation of the session marker with a parameterized resource name.
    /// </summary>
    /// <param name="observer">The separate session observing marker availability without acquiring it.</param>
    /// <returns>The caller-owned command using explicit SQL integer metadata.</returns>
    public SqlCommand CreateMarkerObservationCommand(
        SqlConnection observer
    )
    {
        var command = observer.CreateCommand();

        // WHY: Explicit SQL metadata fixes the scalar contract without coercing malformed CLR observations.
        command.CommandText = "SELECT CONVERT(int, APPLOCK_TEST(N'public', @resource, N'Exclusive', N'Session'));";
        command.CommandTimeout = 5;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = _resource });

        return command;
    }

    /// <summary>
    /// Rejects observations that are not exact lock decisions before certifying marker availability.
    /// </summary>
    /// <param name="result">The scalar returned by the explicit SQL integer observation.</param>
    /// <returns>Whether the exclusive marker can be acquired by the observing session.</returns>
    /// <exception cref="InvalidOperationException">The scalar is not an exact integer zero or one.</exception>
    public static bool ParseMarkerAvailability(
        object? result
    )
    {
        if (result is not int availability
            || availability is not (0 or 1))
        {
            throw new InvalidOperationException("The readiness marker observation did not return a lock decision.");
        }

        return availability == 1;
    }
}
