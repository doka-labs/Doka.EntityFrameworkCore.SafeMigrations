namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the ColumnsAtomicRequiredAddConcurrency migration contracts in an independent class collection.</summary>
public sealed partial class MySqlColumnsAtomicRequiredAddConcurrencyIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlColumnsAtomicRequiredAddConcurrencyIntegrationTests(MySqlEngineContainerFixture fixture) : base(fixture) { }

    private const string AtomicAddExplicitGuid = "00000000-0000-0000-0000-000000000017";

    /// <summary>
    /// A writer arriving after setup cannot obtain a fabricated required value from the first DDL stage.
    /// </summary>
    [Fact]
    public async Task AtomicRequiredAddConcurrencyRejectsRowsArrivingBeforeFirstStage()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, AtomicAddConcurrencyFixtureSql);
        var operation = AtomicRequiredAddOperation();
        var guardName = AtomicRequiredAddGuardName(operation);
        var checkpoint = new AtomicAddCheckpointInterceptor(guardName, betweenStages: false);
        await using var context = CreateRuntimeContext(connectionString, checkpoint);
        context.Database.SetCommandTimeout(60);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await using var writer = new MySqlConnection(connectionString);
        await writer.OpenAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var gate = await AcquireAtomicAddWriterGateAsync(writer, deadline.Token);
        var gateReleased = false;

        // Act
        var execution = Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], executionCancellation.Token));
        Exception? failure;
        try
        {
            await WaitForAtomicAddCheckpointAsync(writer, gate, checkpoint, deadline.Token);
            await ExecuteAtomicAddWriterSqlAsync(writer, gate,
                "INSERT INTO `atomic_required_items` (`key`) VALUES (1);", deadline.Token);
            await gate.CommitAsync(deadline.Token);
            gateReleased = true;
            failure = await execution.WaitAsync(deadline.Token);
        }
        finally
        {
            await FinishAtomicAddConcurrentAttemptAsync(execution, executionCancellation, gate, gateReleased);
        }

        var interrupted = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var retry = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [operation],
            new SafeMigrationRunOptions("atomic-add-raced-existing-row"), CancellationToken.None);

        var retryFailure = await Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], CancellationToken.None));

        // Assert
        Assert.True(checkpoint.CheckpointWasReached);
        Assert.True(checkpoint.WasInjected);
        Assert.Contains(guardName, AssertOwnedAtomicCheckFailure(failure).Message, StringComparison.Ordinal);
        Assert.Equal(0, interrupted.Columns);
        Assert.Equal(0, interrupted.Guards);
        Assert.Equal(1, interrupted.Rows);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, retry.Assessments[0].Action);
        Assert.Contains("doka_sm_data_blocked", AssertOwnedGuardFailure(retryFailure).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The staged physical barrier rejects omitted values while preserving an explicitly supplied value.
    /// </summary>
    /// <param name="useGuidStorage">Whether the required target uses GUID rather than integer storage.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AtomicRequiredAddConcurrencyProtectsOmittedAndExplicitWriterValues(bool useGuidStorage)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, AtomicAddConcurrencyFixtureSql);
        var operation = AtomicRequiredAddOperation(useGuidStorage);
        var guardName = AtomicRequiredAddGuardName(operation);
        var checkpoint = new AtomicAddCheckpointInterceptor(guardName, betweenStages: true);
        await using var context = CreateRuntimeContext(connectionString, checkpoint);
        context.Database.SetCommandTimeout(60);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await using var writer = new MySqlConnection(connectionString);
        await writer.OpenAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var gate = await AcquireAtomicAddWriterGateAsync(writer, deadline.Token);
        var gateReleased = false;

        // Act
        var execution = Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], executionCancellation.Token));
        Exception? omissionFailure;
        Exception? migrationFailure;
        try
        {
            await WaitForAtomicAddCheckpointAsync(writer, gate, checkpoint, deadline.Token);
            omissionFailure = await Record.ExceptionAsync(() => ExecuteAtomicAddWriterSqlAsync(writer, gate,
                "INSERT INTO `atomic_required_items` (`key`) VALUES (1);", deadline.Token));

            await ExecuteAtomicAddWriterSqlAsync(writer, gate,
                "INSERT INTO `atomic_required_items` (`key`, `required_value`) VALUES (2, "
                + (useGuidStorage ? $"'{AtomicAddExplicitGuid}');" : "17);"), deadline.Token);
            await gate.CommitAsync(deadline.Token);
            gateReleased = true;
            migrationFailure = await execution.WaitAsync(deadline.Token);
        }
        finally
        {
            await FinishAtomicAddConcurrentAttemptAsync(execution, executionCancellation, gate, gateReleased);
        }

        var final = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var suppliedValue = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `atomic_required_items` WHERE `key` = 2;");

        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);

        // Assert
        Assert.True(checkpoint.CheckpointWasReached);
        Assert.True(checkpoint.WasInjected);
        Assert.Contains(guardName, Assert.IsType<MySqlException>(omissionFailure).Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(migrationFailure);
        Assert.Equal(1, final.Columns);
        Assert.Equal(0, final.NullableColumns);
        Assert.Equal(0, final.Guards);
        Assert.Equal(useGuidStorage ? AtomicAddExplicitGuid : "17", suppliedValue);
        Assert.Equal(1, final.Rows);
    }
}

public sealed partial class MySqlColumnsAtomicRequiredAddConcurrencyIntegrationTests : MySqlSafeMigrationIntegrationTests
{

    /// <summary>A writer-only MariaDB check bypass cannot make final strict DDL invent a value.</summary>
    [AtomicAddMariaDbFact]
    public async Task AtomicRequiredAddConcurrencyPreservesNullAndBarrierAfterWriterCheckBypass()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, AtomicAddConcurrencyFixtureSql);
        var operation = AtomicRequiredAddOperation();
        var guardName = AtomicRequiredAddGuardName(operation);
        var checkpoint = new AtomicAddCheckpointInterceptor(guardName, betweenStages: true);
        await using var context = CreateRuntimeContext(connectionString, checkpoint);
        context.Database.SetCommandTimeout(60);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await using var writer = new MySqlConnection(connectionString);
        await writer.OpenAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var gate = await AcquireAtomicAddWriterGateAsync(writer, deadline.Token);
        var gateReleased = false;

        // Act
        var execution = Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], executionCancellation.Token));
        Exception? failure;
        try
        {
            await WaitForAtomicAddCheckpointAsync(writer, gate, checkpoint, deadline.Token);
            // WHY: Only this hostile writer changes its own session. The
            // migration session must remain strict and keep CHECK enforcement.
            await ExecuteAtomicAddWriterSqlAsync(writer, gate,
                "SET SESSION check_constraint_checks = OFF; "
                + "INSERT INTO `atomic_required_items` (`key`, `required_value`) VALUES (1, NULL); "
                + "SET SESSION check_constraint_checks = ON;", deadline.Token);
            await gate.CommitAsync(deadline.Token);
            gateReleased = true;
            failure = await execution.WaitAsync(deadline.Token);
        }
        finally
        {
            await FinishAtomicAddConcurrentAttemptAsync(execution, executionCancellation, gate, gateReleased);
        }

        var interrupted = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var nullRowsBeforeRetry = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `atomic_required_items` WHERE `key` = 1 AND `required_value` IS NULL;");

        await ExecuteAtomicAddWriterSqlAsync(writer, transaction: null,
            "UPDATE `atomic_required_items` SET `required_value` = '" + AtomicAddExplicitGuid + "' WHERE `key` = 1;",
            CancellationToken.None);
        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var recovered = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var preserved = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `atomic_required_items` WHERE `key` = 1;");

        // Assert
        Assert.True(checkpoint.CheckpointWasReached);
        Assert.True(checkpoint.WasInjected);
        Assert.IsType<MySqlException>(failure);
        Assert.Equal(1, interrupted.Columns);
        Assert.Equal(1, interrupted.NullableColumns);
        Assert.Equal(1, interrupted.Guards);
        Assert.Equal(1, nullRowsBeforeRetry);
        Assert.Equal(1, recovered.Columns);
        Assert.Equal(0, recovered.NullableColumns);
        Assert.Equal(0, recovered.Guards);
        Assert.Equal(AtomicAddExplicitGuid, preserved);
    }
}

public sealed partial class MySqlColumnsAtomicRequiredAddConcurrencyIntegrationTests : MySqlSafeMigrationIntegrationTests
{

    /// <summary>
    /// Cancellation at a proven staged barrier preserves a writer's supplied value and permits retry.
    /// </summary>
    [Fact]
    public async Task AtomicRequiredAddConcurrencyCancellationRetainsBarrierAndWriterValue()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, AtomicAddConcurrencyFixtureSql);
        var operation = AtomicRequiredAddOperation();
        var guardName = AtomicRequiredAddGuardName(operation);
        var checkpoint = new AtomicAddCheckpointInterceptor(guardName, betweenStages: true);
        await using var context = CreateRuntimeContext(connectionString, checkpoint);
        context.Database.SetCommandTimeout(60);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        await using var writer = new MySqlConnection(connectionString);
        await writer.OpenAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var gate = await AcquireAtomicAddWriterGateAsync(writer, deadline.Token);
        var gateReleased = false;
        var sessionBefore = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");

        // Act
        var execution = Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, [operation], executionCancellation.Token));
        Exception? failure;
        try
        {
            await WaitForAtomicAddCheckpointAsync(writer, gate, checkpoint, deadline.Token);
            await ExecuteAtomicAddWriterSqlAsync(writer, gate,
                "INSERT INTO `atomic_required_items` (`key`, `required_value`) VALUES (1, '"
                + AtomicAddExplicitGuid + "');", deadline.Token);
            // WHY: Keep the gate locked until cancellation is observed. A
            // commit-before-cancel would race successful finalization instead.
            await executionCancellation.CancelAsync();
            failure = await execution.WaitAsync(deadline.Token);
            await gate.CommitAsync(deadline.Token);
            gateReleased = true;
        }
        finally
        {
            await FinishAtomicAddConcurrentAttemptAsync(execution, executionCancellation, gate, gateReleased);
        }

        var interrupted = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var valueBeforeRetry = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `atomic_required_items` WHERE `key` = 1;");

        await ExecuteOperationsAsync(context, [operation], CancellationToken.None);
        var recovered = await ReadAtomicRequiredAddStateAsync(connectionString, operation);
        var sessionAfter = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var preserved = await ScalarStringAsync(connectionString,
            "SELECT `required_value` FROM `atomic_required_items` WHERE `key` = 1;");

        // Assert
        Assert.True(checkpoint.CheckpointWasReached);
        Assert.True(checkpoint.WasInjected);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(1, interrupted.Columns);
        Assert.Equal(1, interrupted.NullableColumns);
        Assert.Equal(1, interrupted.Guards);
        Assert.Equal(AtomicAddExplicitGuid, valueBeforeRetry);
        Assert.Equal(1, recovered.Columns);
        Assert.Equal(0, recovered.NullableColumns);
        Assert.Equal(0, recovered.Guards);
        Assert.Equal(sessionBefore, sessionAfter);
        Assert.Equal(AtomicAddExplicitGuid, preserved);
    }

    // WHY: The marker and the gate are separate tables. The migration commits
    // its reached marker before waiting on the writer's independent row lock.
    private const string AtomicAddConcurrencyFixtureSql =
        "CREATE TABLE `atomic_required_items` (`key` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
        + "CREATE TABLE `atomic_add_checkpoint` "
        + "(`key` int NOT NULL PRIMARY KEY, `reached` int NOT NULL) ENGINE=InnoDB; "
        + "INSERT INTO `atomic_add_checkpoint` VALUES (1, 0); "
        + "CREATE TABLE `atomic_add_gate` (`key` int NOT NULL PRIMARY KEY) ENGINE=InnoDB; "
        + "INSERT INTO `atomic_add_gate` VALUES (1);";
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{

    /// <summary>Acquires a real writer-owned row lock before dispatching the migration.</summary>
    /// <param name="writer">The independently opened writer session.</param>
    /// <param name="cancellationToken">The bounded test deadline.</param>
    /// <returns>The transaction retaining the gate until the test deliberately releases it.</returns>
    protected static async Task<MySqlTransaction> AcquireAtomicAddWriterGateAsync(
        MySqlConnection writer,
        CancellationToken cancellationToken
    )
    {
        // WHY: Repeated marker reads must see later commits rather than keep
        // the first repeatable-read snapshot while this transaction holds a gate.
        var transaction = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using var command = writer.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT `key` FROM `atomic_add_gate` WHERE `key` = 1 FOR UPDATE;";
            var key = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (key != 1)
            {
                throw new InvalidOperationException("The atomic-add writer gate was not acquired.");
            }

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();

            throw;
        }
    }
}

public sealed partial class MySqlColumnsAtomicRequiredAddConcurrencyIntegrationTests : MySqlSafeMigrationIntegrationTests
{

    /// <summary>Waits for the database-visible checkpoint rather than estimating stage timing.</summary>
    /// <param name="writer">The independent writer session.</param>
    /// <param name="transaction">The transaction retaining the test gate.</param>
    /// <param name="checkpoint">The observer whose non-vacuity flag is set only after an actual marker read.</param>
    /// <param name="cancellationToken">The bounded test deadline.</param>
    private static async Task WaitForAtomicAddCheckpointAsync(
        MySqlConnection writer,
        MySqlTransaction transaction,
        AtomicAddCheckpointInterceptor checkpoint,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            await using var command = writer.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT `reached` FROM `atomic_add_checkpoint` WHERE `key` = 1;";
            var reached = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            if (reached == 1)
            {
                checkpoint.RecordObservedCheckpoint();

                return;
            }

            // WHY: This is only polling backoff. A committed marker, never an
            // elapsed delay, authorizes the writer's deterministic interleaving.
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{

    /// <summary>Executes only test-owned writer SQL on its existing independent session.</summary>
    /// <param name="writer">The writer connection that never uses the migration's advisory lock.</param>
    /// <param name="transaction">The writer-owned gate transaction, or null after its commit.</param>
    /// <param name="sql">The bounded anonymous fixture statement.</param>
    /// <param name="cancellationToken">The operation's test deadline.</param>
    protected static async Task ExecuteAtomicAddWriterSqlAsync(
        MySqlConnection writer,
        MySqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = writer.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Releases a test gate on failure and observes any outstanding migration before disposal.</summary>
    /// <param name="execution">The captured migration completion, including its first exception.</param>
    /// <param name="executionCancellation">The test-owned migration cancellation source.</param>
    /// <param name="gate">The writer transaction retaining the independent row lock.</param>
    /// <param name="gateReleased">Whether the test successfully committed that transaction.</param>
    protected static async Task FinishAtomicAddConcurrentAttemptAsync(
        Task<Exception?> execution,
        CancellationTokenSource executionCancellation,
        MySqlTransaction gate,
        bool gateReleased
    )
    {
        if (!gateReleased)
        {
            await gate.RollbackAsync(CancellationToken.None);
        }

        if (!execution.IsCompleted)
        {
            await executionCancellation.CancelAsync();
            _ = await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Injects one test-owned gate into the real two-stage body, never into setup or product code.</summary>
    protected sealed class AtomicAddCheckpointInterceptor : DbCommandInterceptor
    {
        private const string BodyPrefix = "SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' "
            + "AND NOT @doka_sm_required_add_resume THEN";
        private const string StageEnd = "DEALLOCATE PREPARE doka_sm_statement;";
        private const string CheckpointSql = "UPDATE `atomic_add_checkpoint` SET `reached` = 1 WHERE `key` = 1; "
            + "SELECT `key` FROM `atomic_add_gate` WHERE `key` = 1 FOR UPDATE; ";

        private readonly bool _betweenStages;
        private readonly string _guardName;
        private readonly string _guardHex;
        private int _injected;
        private int _checkpointReached;

        /// <summary>Scopes the deterministic interception to one immutable operation-owned atomic body.</summary>
        /// <param name="guardName">The exact guard derived from the operation's immutable contract.</param>
        /// <param name="betweenStages">Whether the gate belongs after the first DDL rather than before it.</param>
        public AtomicAddCheckpointInterceptor(string guardName, bool betweenStages)
        {
            _guardName = guardName;
            _guardHex = Convert.ToHexString(Encoding.UTF8.GetBytes(guardName));
            _betweenStages = betweenStages;
        }

        /// <summary>Gets whether this interceptor modified the exact expected body shape once.</summary>
        public bool WasInjected => Volatile.Read(ref _injected) != 0;

        /// <summary>Gets whether the independent writer observed the database's committed reached marker.</summary>
        public bool CheckpointWasReached => Volatile.Read(ref _checkpointReached) != 0;

        /// <summary>Records non-vacuity only after a live read observed the committed checkpoint marker.</summary>
        public void RecordObservedCheckpoint() => Volatile.Write(ref _checkpointReached, 1);

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            var sql = command.CommandText;
            if (!sql.StartsWith(BodyPrefix, StringComparison.Ordinal)
                || (!sql.Contains(_guardHex, StringComparison.OrdinalIgnoreCase)
                    && !sql.Contains(_guardName, StringComparison.Ordinal))
                || WasInjected)
            {
                return ValueTask.FromResult(result);
            }

            var firstEnd = sql.IndexOf(StageEnd, StringComparison.Ordinal);
            var secondEnd = firstEnd < 0 ? -1 : sql.IndexOf(StageEnd, firstEnd + StageEnd.Length,
                StringComparison.Ordinal);

            if (firstEnd < 0 || secondEnd < 0
                || sql.IndexOf(StageEnd, secondEnd + StageEnd.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("The atomic-add checkpoint requires exactly two owned DDL stages.");
            }

            if (Interlocked.CompareExchange(ref _injected, 1, 0) == 0)
            {
                command.CommandText = sql.Insert(_betweenStages ? firstEnd + StageEnd.Length : 0, CheckpointSql);
            }

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Reports an honest discovery-time skip where the writer-only check toggle does not exist.</summary>
    private protected sealed class AtomicAddMariaDbFactAttribute : FactAttribute
    {
        /// <summary>Uses the same explicit engine selector and MariaDB default as the container fixture.</summary>
        public AtomicAddMariaDbFactAttribute()
        {
            var engine = Environment.GetEnvironmentVariable("SAFE_MIGRATIONS_MYSQL_ENGINE")?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(engine) && engine != "mariadb")
            {
                Skip = "MariaDB check_constraint_checks is not a MySQL session variable.";
            }
        }
    }
}
