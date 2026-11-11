using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Publishes attention readiness out of band without changing SqlClient's async TDS parsing path.
/// </summary>
internal sealed class SqlServerIdentityInsertPauseInterceptor
    : DbCommandInterceptor
{
    private readonly bool _beforeConfirmation;
    private readonly string _resource;

    /// <summary>
    /// Creates a pause associated with a unique application lock in the test database.
    /// </summary>
    public SqlServerIdentityInsertPauseInterceptor(
        string resource,
        bool beforeConfirmation = false
    )
    {
        _resource = resource;
        _beforeConfirmation = beforeConfirmation;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>>
        NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
    {
        command.CommandText = BuildPausedCommand(command.CommandText, _resource, _beforeConfirmation);

        return new ValueTask<InterceptionResult<int>>(result);
    }

    /// <summary>
    /// Places readiness at the native acquisition phase without interpreting quoted caller data as SQL.
    /// </summary>
    /// <param name="commandText">The unchanged generated command, including its execution scopes.</param>
    /// <param name="resource">The application-lock resource whose value must survive every quoting level.</param>
    /// <param name="beforeConfirmation">Whether to interrupt before ownership confirmation.</param>
    /// <returns>The instrumented command, or the same string when no target acquisition exists.</returns>
    public static string BuildPausedCommand(
        string commandText,
        string resource,
        bool beforeConfirmation = false
    )
    {
        if (!commandText.Contains("IDENTITY_INSERT", StringComparison.OrdinalIgnoreCase))
        {
            return commandText;
        }

        // WHY: SqlClient drains an unparameterized async batch's first response under its cancellation lock.
        // NOWAIT starts that synchronous drain, but InfoMessage is deferred until the batch finishes.
        // A session-owned lock proves ON to a separate observer without emitting the early INFO token.
        var pause = $"""

            DECLARE @doka_identity_ready int;
            EXEC @doka_identity_ready = sys.sp_getapplock @Resource = N'{resource.Replace("'", "''",
                StringComparison.Ordinal)}', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0;
            IF @doka_identity_ready < 0 THROW 51006, 'identity readiness lock failed', 1;
            WAITFOR DELAY '00:05:00';

            """;

        // WHY: A worklist follows only executable literal scopes and the generator's closed
        // lossless carrier. Reverse composition preserves arbitrary nesting without recursion.
        var scopes = new List<PauseScope> { new(commandText, -1, 0, 0, false) };

        // WHY: Discovery appends scopes; enumeration would invalidate the growing worklist.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < scopes.Count; index++)
        {
            DiscoverPauses(scopes, index, pause, beforeConfirmation);
        }

        // WHY: A rewritten child must reach its parent before that parent is composed.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = scopes.Count - 1; index > 0; index--)
        {
            var scope = scopes[index];

            if (scope.Edits.Count == 0)
            {
                continue;
            }

            var rewritten = ApplyEdits(scope);
            var replacement = scope.ProtectedCarrier
                ? SqlServerGuardedSqlTestContract.DecodeScope(
                    SqlServerSafeMigrationsSqlGenerator.ProtectCommandSql(
                        rewritten, MigrationsSqlGenerationOptions.Idempotent))
                : "'" + rewritten.Replace("'", "''", StringComparison.Ordinal) + "'";

            scopes[scope.Parent].Edits.Add((scope.Start, scope.Length, replacement));
        }

        return ApplyEdits(scopes[0]);
    }

    /// <summary>Finds native acquisition and executable payload boundaries at exactly one quoting level.</summary>
    /// <param name="scopes">The owned worklist, extended only for executable literal payloads.</param>
    /// <param name="index">The current scope index.</param>
    /// <param name="pause">The native pause text, before any transport escaping.</param>
    /// <param name="beforeConfirmation">Whether to interrupt the uncertain ownership interval.</param>
    private static void DiscoverPauses(
        List<PauseScope> scopes,
        int index,
        string pause,
        bool beforeConfirmation
    )
    {
        var scope = scopes[index];

        if (SqlServerGuardedSqlTestContract.DecodeProtectedPayload(scope.Sql) is { } payload)
        {
            scopes.Add(new PauseScope(payload, index, 0, scope.Sql.Length, true));

            return;
        }

        var reader = new SqlServerTestSqlReader(scope.Sql);
        var pendingAcquisition = -1;

        while (reader.Read())
        {
            if (reader.IsComment)
            {
                if (pendingAcquisition >= 0 && reader.Token.SequenceEqual(
                        SqlServerSafeMigrationIdentityInsertScope.AcquiredMarkerComment))
                {
                    scope.Edits.Add((reader.Position, 0, pause));
                    pendingAcquisition = -1;
                }

                continue;
            }

            if (reader.Token.Equals("SET", StringComparison.OrdinalIgnoreCase))
            {
                var acquisition = reader;

                if (acquisition.TryReadSequence("IDENTITY_INSERT [dbo].[identity_roles] ON;"))
                {
                    if (pendingAcquisition >= 0)
                    {
                        scope.Edits.Add((pendingAcquisition, 0, pause));
                    }

                    if (beforeConfirmation)
                    {
                        scope.Edits.Add((acquisition.Position, 0, pause));
                    }
                    else
                    {
                        pendingAcquisition = acquisition.Position;
                    }

                    reader = acquisition;
                }
            }
            else if (reader.Token.Equals("EXEC", StringComparison.OrdinalIgnoreCase)
                || reader.Token.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase))
            {
                var execution = reader;

                if (execution.TryReadSequence("sys.sp_executesql N")
                    && execution.ReadSignificant() && execution.Token[0] == '\'')
                {
                    var inner = execution.Token[1..^1].ToString().Replace("''", "'", StringComparison.Ordinal);

                    if (inner.Contains("IDENTITY_INSERT", StringComparison.OrdinalIgnoreCase))
                    {
                        scopes.Add(new PauseScope(inner, index, execution.TokenStart,
                            execution.Position - execution.TokenStart, false));
                    }

                    reader = execution;
                }
            }
        }

        // WHY: Older focused fixtures have no ownership protocol marker. Only an actual
        // native ON statement can select this fallback; caller string values cannot.
        if (pendingAcquisition >= 0)
        {
            scope.Edits.Add((pendingAcquisition, 0, pause));
        }
    }

    /// <summary>Copies unchanged ranges exactly and applies only proven scope-local edits.</summary>
    /// <param name="scope">The original scope and its non-overlapping edits.</param>
    /// <returns>The same string for an unchanged scope, otherwise the precisely rewritten text.</returns>
    private static string ApplyEdits(
        PauseScope scope
    )
    {
        if (scope.Edits.Count == 0)
        {
            return scope.Sql;
        }

        scope.Edits.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var result = new StringBuilder(scope.Sql.Length);
        var copied = 0;

        foreach (var edit in scope.Edits)
        {
            result.Append(scope.Sql.AsSpan(copied, edit.Start - copied));
            result.Append(edit.Replacement);
            copied = edit.Start + edit.Length;
        }

        result.Append(scope.Sql.AsSpan(copied));

        return result.ToString();
    }

    /// <summary>Owns one immutable execution scope and its exact parent replacement range.</summary>
    /// <param name="sql">The native scope at one quoting level.</param>
    /// <param name="parent">The parent worklist index, or minus one for the root.</param>
    /// <param name="start">The replaced range start in the parent.</param>
    /// <param name="length">The replaced range length in the parent.</param>
    /// <param name="protectedCarrier">Whether the parent uses the closed MAX/NCHAR transport.</param>
    private sealed class PauseScope(string sql, int parent, int start, int length, bool protectedCarrier)
    {
        /// <summary>Gets the unchanged SQL at this execution level.</summary>
        public string Sql { get; } = sql;

        /// <summary>Gets the owning worklist entry, or minus one for the root.</summary>
        public int Parent { get; } = parent;

        /// <summary>Gets the exact parent replacement start.</summary>
        public int Start { get; } = start;

        /// <summary>Gets the exact parent replacement length.</summary>
        public int Length { get; } = length;

        /// <summary>Gets whether the parent must reconstruct a MAX/NCHAR payload.</summary>
        public bool ProtectedCarrier { get; } = protectedCarrier;

        /// <summary>Gets only the proven non-overlapping replacements at this execution level.</summary>
        public List<(int Start, int Length, string Replacement)> Edits { get; } = [];
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
    /// <param name="observer">The independent connection observing marker availability.</param>
    /// <param name="cancellationToken">The token cancelling the marker query.</param>
    /// <returns>Whether the observing session could acquire the marker.</returns>
    public async Task<bool> CanAcquireMarkerAsync(
        SqlConnection observer,
        CancellationToken cancellationToken
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
        if (result is not (int availability and (0 or 1)))
        {
            throw new InvalidOperationException("The readiness marker observation did not return a lock decision.");
        }

        return availability == 1;
    }
}
