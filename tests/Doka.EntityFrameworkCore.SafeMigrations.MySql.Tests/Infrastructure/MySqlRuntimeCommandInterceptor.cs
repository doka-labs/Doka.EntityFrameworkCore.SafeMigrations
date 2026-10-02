namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Measures executed migration commands without retaining SQL or database connection information.</summary>
internal sealed class MySqlRuntimeCommandInterceptor : DbCommandInterceptor
{
    private const string PreparedSetupStatement = "PREPARE doka_sm_statement FROM @doka_sm_sql;";
    private const string PreparedSetupGroup = PreparedSetupStatement
        + "EXECUTE doka_sm_statement;DEALLOCATE PREPARE doka_sm_statement;";

    private readonly Dictionary<string, CommandCategory> _categories = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string? _setupInjection;
    private Action? _setupStarted;
    private bool _injectCompactedPrepareGroup;
    private bool _injectDataProbeGroup;
    private bool _injectNullabilityProbeGroup;

    /// <summary>Gets the number of started commands across all observed categories.</summary>
    public long CommandCount { get; private set; }

    /// <summary>Gets the number of guarded bodies that started executing.</summary>
    public long GuardedBodyCount { get; private set; }

    /// <summary>Gets whether the failure probe reached real acquired setup resources.</summary>
    public bool SetupWasInjected { get; private set; }

    /// <summary>Gets the real provider timeout on the command selected for the setup probe.</summary>
    public int? InjectedCommandTimeout { get; private set; }

    /// <summary>Gets whether injection targeted three prepared setup statements in one dispatched command.</summary>
    public bool CompactedPrepareGroupWasInjected { get; private set; }

    /// <summary>Gets whether injection reached the fused data probe instead of another prepared group.</summary>
    public bool DataProbePrepareGroupWasInjected { get; private set; }

    /// <summary>Gets whether injection reached the operation-local NULL-proof prepared group.</summary>
    public bool NullabilityProbePrepareGroupWasInjected { get; private set; }

    /// <summary>Gets the original grouped command size before adding the test-owned failure probe.</summary>
    public int? OriginalCompactedPrepareGroupPayloadBytes { get; private set; }

    /// <summary>Injects SQL once after assertion setup to probe failure cleanup on the actual migration path.</summary>
    /// <param name="sql">The test-owned failure or delay statement.</param>
    /// <param name="setupStarted">An optional action scheduled only after the targeted setup command starts.</param>
    public void InjectFirstSetup(
        string sql,
        Action? setupStarted = null
    )
    {
        _setupInjection = sql;
        _setupStarted = setupStarted;
        _injectCompactedPrepareGroup = false;
        _injectDataProbeGroup = false;
        _injectNullabilityProbeGroup = false;
    }

    /// <summary>Injects SQL after acquired PREPARE within a real compacted setup command.</summary>
    /// <param name="sql">The test-owned failure or blocking statements followed by their sentinel.</param>
    /// <param name="setupStarted">An optional cancellation action scheduled at the targeted command dispatch.</param>
    public void InjectFirstCompactedPrepareGroup(
        string sql,
        Action? setupStarted = null
    )
    {
        _setupInjection = sql;
        _setupStarted = setupStarted;
        _injectCompactedPrepareGroup = true;
        _injectDataProbeGroup = false;
        _injectNullabilityProbeGroup = false;
    }

    /// <summary>Interrupts the fused data probe after PREPARE acquires its session resources.</summary>
    /// <param name="sql">The test-owned failure or blocking statements followed by their sentinel.</param>
    /// <param name="setupStarted">An optional cancellation action scheduled at the targeted command dispatch.</param>
    public void InjectFirstCompactedDataProbeGroup(
        string sql,
        Action? setupStarted = null
    )
    {
        _setupInjection = sql;
        _setupStarted = setupStarted;
        _injectCompactedPrepareGroup = true;
        _injectDataProbeGroup = true;
        _injectNullabilityProbeGroup = false;
    }

    /// <summary>Interrupts the NULL-proof group after its prepared statement is acquired.</summary>
    /// <param name="sql">The test-owned failure or blocking statements followed by their sentinel.</param>
    /// <param name="setupStarted">An optional cancellation action scheduled at the targeted dispatch.</param>
    public void InjectFirstCompactedNullabilityProbeGroup(
        string sql,
        Action? setupStarted = null
    )
    {
        _setupInjection = sql;
        _setupStarted = setupStarted;
        _injectCompactedPrepareGroup = true;
        _injectDataProbeGroup = false;
        _injectNullabilityProbeGroup = true;
    }

    /// <summary>Resets counters between initial application and history-only replay.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _categories.Clear();
            CommandCount = 0;
            GuardedBodyCount = 0;
        }
    }

    /// <summary>Returns a bounded aggregate snapshot for durable, non-sensitive performance evidence.</summary>
    /// <returns>The counters and durations grouped by command purpose.</returns>
    public object Snapshot()
    {
        lock (_gate)
        {

            return new
            {
                CommandCount,
                GuardedBodyCount,
                categories = _categories.ToDictionary(
                    static item => item.Key,
                    static item => new
                    {
                        item.Value.Started,
                        item.Value.Completed,
                        item.Value.Failed,
                        item.Value.PayloadBytes,
                        item.Value.MaximumPayloadBytes,
                        item.Value.ElapsedMilliseconds,
                    }),
            };
        }
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        Started(command);

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result
    )
    {
        Started(command);

        return result;
    }

    /// <inheritdoc />
    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default
    )
    {
        Completed(command, eventData.Duration, failed: false);

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result
    )
    {
        Completed(command, eventData.Duration, failed: false);

        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    )
    {
        Started(command);

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result
    )
    {
        Started(command);

        return result;
    }

    /// <inheritdoc />
    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default
    )
    {
        Completed(command, eventData.Duration, failed: false);

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result
    )
    {
        Completed(command, eventData.Duration, failed: false);

        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default
    )
    {
        Started(command);

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result
    )
    {
        Started(command);

        return result;
    }

    /// <inheritdoc />
    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default
    )
    {
        Completed(command, eventData.Duration, failed: false);

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result
    )
    {
        Completed(command, eventData.Duration, failed: false);

        return result;
    }

    /// <inheritdoc />
    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        Completed(command, eventData.Duration, failed: true);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void CommandFailed(
        DbCommand command,
        CommandErrorEventData eventData
    ) => Completed(command, eventData.Duration, failed: true);

    /// <inheritdoc />
    public override Task CommandCanceledAsync(
        DbCommand command,
        CommandEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        Completed(command, eventData.Duration, failed: true);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void CommandCanceled(
        DbCommand command,
        CommandEndEventData eventData
    ) => Completed(command, eventData.Duration, failed: true);

    /// <summary>Counts a real command and applies the one-shot acquired-resource probe when requested.</summary>
    private void Started(
        DbCommand command
    )
    {
        if (_setupInjection is not null)
        {
            if (_injectCompactedPrepareGroup)
            {
                var offset = command.CommandText.IndexOf(PreparedSetupGroup, StringComparison.Ordinal);
                var assignmentPrefix = _injectNullabilityProbeGroup
                    ? "SET @doka_sm_column_repair_eligible ="
                    : _injectDataProbeGroup
                    ? "SET @doka_sm_sql = CASE WHEN @doka_sm_data_probe_required"
                    : "SET @doka_sm_sql =";

                if (offset >= 0 && command.CommandText.StartsWith(assignmentPrefix, StringComparison.Ordinal))
                {
                    // WHY: The assignment and contiguous triple must share the actual dispatch;
                    // an isolated short PREPARE group cannot prove the fused large-command boundary.
                    OriginalCompactedPrepareGroupPayloadBytes = Encoding.UTF8.GetByteCount(command.CommandText);
                    command.CommandText = command.CommandText.Insert(
                        offset + PreparedSetupStatement.Length,
                        "\n" + _setupInjection + "\n");

                    CompactedPrepareGroupWasInjected = true;
                    DataProbePrepareGroupWasInjected = _injectDataProbeGroup;
                    NullabilityProbePrepareGroupWasInjected = _injectNullabilityProbeGroup;
                    CompleteSetupInjection(command);
                }
            }
            else if (command.CommandText.StartsWith(
                         "DROP TEMPORARY TABLE IF EXISTS `__doka_sm_assert`",
                         StringComparison.Ordinal))
            {
                // WHY: Inject after real setup rather than suppress execution so acquired resources must be unwound.
                command.CommandText += "\n" + _setupInjection;
                CompleteSetupInjection(command);
            }
        }

        var category = Classify(command.CommandText);
        var bytes = Encoding.UTF8.GetByteCount(command.CommandText);

        lock (_gate)
        {
            var entry = GetCategory(category);
            entry.Started++;
            entry.PayloadBytes += bytes;
            entry.MaximumPayloadBytes = Math.Max(entry.MaximumPayloadBytes, bytes);
            CommandCount++;
            if (category == "guarded_body")
            {
                GuardedBodyCount++;
            }
        }
    }

    /// <summary>Records one-shot probe consumption only when the selected real command is dispatched.</summary>
    private void CompleteSetupInjection(
        DbCommand command
    )
    {
        _setupInjection = null;
        SetupWasInjected = true;
        InjectedCommandTimeout = command.CommandTimeout;
        _setupStarted?.Invoke();
        _setupStarted = null;
    }

    /// <summary>Records provider execution time for successful or aborted command executions.</summary>
    private void Completed(
        DbCommand command,
        TimeSpan duration,
        bool failed
    )
    {
        lock (_gate)
        {
            var entry = GetCategory(Classify(command.CommandText));
            entry.ElapsedMilliseconds += duration.TotalMilliseconds;
            if (failed)
            {
                entry.Failed++;
            }
            else
            {
                entry.Completed++;
            }
        }
    }

    /// <summary>Gets one of the small, fixed command categories without retaining command text.</summary>
    private CommandCategory GetCategory(
        string name
    )
    {
        if (!_categories.TryGetValue(name, out var category))
        {
            category = new CommandCategory();
            _categories.Add(name, category);
        }

        return category;
    }

    /// <summary>Separates owned guard setup, body, independent cleanup, and ordinary EF infrastructure.</summary>
    private static string Classify(
        string sql
    )
    {
        if (sql.Contains("SET @doka_sm_post_ok", StringComparison.Ordinal))
        {
            return "guarded_body";
        }

        if (sql.Contains("SET @doka_sm_state = NULL, @doka_sm_action = NULL", StringComparison.Ordinal))
        {
            return "guard_cleanup";
        }

        if (sql.StartsWith("PREPARE doka_sm_statement FROM 'DO 0'", StringComparison.Ordinal))
        {
            return "prepared_cleanup";
        }

        if (sql.Contains("doka_sm", StringComparison.Ordinal))
        {
            return "guard_setup";
        }

        return "migration_infrastructure";
    }

    /// <summary>Stores bounded aggregate counters for one command purpose.</summary>
    private sealed class CommandCategory
    {
        /// <summary>Gets or sets actual command dispatches.</summary>
        public long Started { get; set; }

        /// <summary>Gets or sets successfully completed commands.</summary>
        public long Completed { get; set; }

        /// <summary>Gets or sets failed or cancelled command executions.</summary>
        public long Failed { get; set; }

        /// <summary>Gets or sets the cumulative UTF-8 command payload.</summary>
        public long PayloadBytes { get; set; }

        /// <summary>Gets or sets the largest individual UTF-8 command payload.</summary>
        public int MaximumPayloadBytes { get; set; }

        /// <summary>Gets or sets cumulative EF-observed provider command duration.</summary>
        public double ElapsedMilliseconds { get; set; }
    }
}
