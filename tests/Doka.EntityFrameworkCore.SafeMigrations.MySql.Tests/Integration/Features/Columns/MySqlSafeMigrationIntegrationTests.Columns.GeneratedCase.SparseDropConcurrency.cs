namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>A prior preflight result cannot authorize a DROP after native generated dependencies change.</summary>
    /// <param name="mutation">The separately committed generated-owner change.</param>
    [GeneratedMariaDbTheory]
    [InlineData("add")]
    [InlineData("change")]
    [InlineData("recreate")]
    public async Task StandaloneGeneratedDropRefreshesDependencyAfterPreflight(string mutation)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await SeedSparseDropConcurrencyAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", "generated_sparse_drop");
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-before-concurrent-change"), CancellationToken.None);

        // Act
        await ExecuteSqlAsync(connectionString, SparseDropConcurrentMutation(mutation));
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var after = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-after-concurrent-change"), CancellationToken.None);

        var unrelated = await SparseDropColumnCountAsync(connectionString, "unrelated");
        var retained = await SparseDropRetainedOriginalRowsAsync(connectionString);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, Assert.Single(preflight.Assessments).Action);
        Assert.Equal("doka_sm_unsupported", Assert.IsType<SafeMigrationExecutionException>(failure).GuardCode);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(after.Assessments).Action);
        Assert.Equal(1, unrelated);
        Assert.Equal(2, retained);
    }

    /// <summary>Native dependency validation remains the final guard after completed state evaluation.</summary>
    /// <param name="mutation">The dependency change injected by the independent native session.</param>
    [GeneratedMariaDbTheory]
    [InlineData("add")]
    [InlineData("change")]
    [InlineData("recreate")]
    [InlineData("replace-target")]
    public async Task StandaloneGeneratedDropNativeGuardRejectsPostClassificationDependency(string mutation)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await SeedSparseDropConcurrencyAsync(connectionString);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_sparse_control LIKE generated_sparse_drop; "
            + "ALTER TABLE generated_sparse_control "
            + "ADD COLUMN dependent int GENERATED ALWAYS AS (unrelated + 1) VIRTUAL;");
        var gateName = "sm-sparse-drop-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var checkpoint = new SparseDropCheckpointInterceptor(gateName);
        await using var context = CreateRuntimeContext(connectionString, checkpoint);
        context.Database.SetCommandTimeout(60);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", "generated_sparse_drop");
        await using var writer = new MySqlConnection(connectionString);
        await writer.OpenAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var acquire = writer.CreateCommand();
        acquire.CommandText = $"SELECT GET_LOCK('{gateName}', 30);";
        if (Convert.ToInt32(await acquire.ExecuteScalarAsync(deadline.Token), CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException("The independent generated-DROP gate was not acquired.");
        }

        // Act
        var controlFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "ALTER TABLE generated_sparse_control DROP COLUMN unrelated;"));

        var execution = Record.ExceptionAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, executionCancellation.Token));

        Exception? failure;
        var observed = false;
        var mutated = false;
        try
        {
            await WaitForSparseDropCheckpointAsync(writer, deadline.Token);
            observed = true;
            await using var mutate = writer.CreateCommand();
            mutate.CommandText = SparseDropConcurrentMutation(mutation);
            await mutate.ExecuteNonQueryAsync(deadline.Token);
            mutated = true;
            await ReleaseSparseDropGateAsync(writer, gateName);
            failure = await execution.WaitAsync(deadline.Token);
        }
        finally
        {
            await ReleaseSparseDropGateAsync(writer, gateName);
            if (!execution.IsCompleted)
            {
                await executionCancellation.CancelAsync();
                _ = await execution.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        var unrelated = await SparseDropColumnCountAsync(connectionString, "unrelated");
        var retained = await SparseDropRetainedOriginalRowsAsync(connectionString);
        var after = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("standalone-generated-race-replay"), CancellationToken.None);

        // Assert
        Assert.True(checkpoint.WasInjected);
        Assert.True(observed);
        Assert.True(mutated);
        // WHY: This interleaving is after the owned guard. A native dependency
        // error, not another owned preflight refusal, proves the final boundary.
        var nativeControl = Assert.IsType<MySqlException>(controlFailure);
        var native = Assert.IsType<MySqlException>(failure);
        Assert.Equal(nativeControl.Number, native.Number);
        Assert.Equal(nativeControl.SqlState, native.SqlState);
        Assert.NotEqual(1062, native.Number);
        Assert.Contains("unrelated", native.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("doka_sm_", native.Message, StringComparison.Ordinal);
        Assert.Equal(1, unrelated);
        Assert.Equal(2, retained);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(after.Assessments).Action);
    }

    /// <summary>Creates anonymous rows and a separately committed checkpoint, never production fixtures.</summary>
    private static Task SeedSparseDropConcurrencyAsync(string connectionString)
        => ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, source int NULL, "
            + "unrelated int NULL, result int GENERATED ALWAYS AS (source + 1) VIRTUAL); "
            + "INSERT INTO generated_sparse_drop (id, source, unrelated) VALUES (1, 3, 11), (2, 7, 13); "
            + "CREATE TABLE generated_drop_checkpoint (id int NOT NULL PRIMARY KEY, reached int NOT NULL); "
            + "INSERT INTO generated_drop_checkpoint VALUES (1, 0);");

    /// <summary>Describes a committed dependency change without accepting arbitrary fixture SQL.</summary>
    private static string SparseDropConcurrentMutation(string mutation) => mutation switch
    {
        "add" => "ALTER TABLE generated_sparse_drop "
            + "ADD COLUMN dependent int GENERATED ALWAYS AS (unrelated + 1) VIRTUAL;",
        "change" => "ALTER TABLE generated_sparse_drop "
            + "MODIFY COLUMN result int GENERATED ALWAYS AS (unrelated + 1) VIRTUAL;",
        "recreate" => "DROP TABLE generated_sparse_drop; "
            + "CREATE TABLE generated_sparse_drop (id int NOT NULL PRIMARY KEY, source int NULL, "
            + "unrelated int NULL, result int GENERATED ALWAYS AS (unrelated + 1) VIRTUAL); "
            + "INSERT INTO generated_sparse_drop (id, source, unrelated) VALUES (1, 3, 11), (2, 7, 13);",
        "replace-target" => "ALTER TABLE generated_sparse_drop DROP COLUMN unrelated; "
            + "ALTER TABLE generated_sparse_drop ADD COLUMN unrelated int NULL; "
            + "UPDATE generated_sparse_drop SET unrelated = CASE id WHEN 1 THEN 11 ELSE 13 END; "
            + "ALTER TABLE generated_sparse_drop ADD COLUMN dependent int "
            + "GENERATED ALWAYS AS (unrelated + 1) VIRTUAL;",
        _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
    };

    /// <summary>Checks every original ordinary value after a refused DROP.</summary>
    private static Task<int> SparseDropRetainedOriginalRowsAsync(string connectionString)
        => ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM generated_sparse_drop "
            + "WHERE (id = 1 AND source = 3 AND unrelated = 11) OR (id = 2 AND source = 7 AND unrelated = 13);");

    /// <summary>Waits for a database-visible marker rather than guessing command duration.</summary>
    private static async Task WaitForSparseDropCheckpointAsync(
        MySqlConnection writer, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var command = writer.CreateCommand();
            command.CommandText = "SELECT reached FROM generated_drop_checkpoint WHERE id = 1;";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1)
            {
                return;
            }

            await Task.Yield();
        }
    }

    /// <summary>Releases only the unique test-owned named gate, including failed interleavings.</summary>
    private static async Task ReleaseSparseDropGateAsync(MySqlConnection writer, string gateName)
    {
        await using var command = writer.CreateCommand();
        command.CommandText = $"DO RELEASE_LOCK('{gateName}');";
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Places one independent writer barrier between final classification and target DDL selection.</summary>
    private sealed class SparseDropCheckpointInterceptor(string gateName) : DbCommandInterceptor
    {
        private const string ApplyPrefix = "SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' THEN CONVERT(0x";
        private int _injected;

        /// <summary>Gets whether the exact owned body received the deterministic barrier.</summary>
        public bool WasInjected => Volatile.Read(ref _injected) != 0;

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var position = command.CommandText.IndexOf(ApplyPrefix, StringComparison.Ordinal);
            if (position >= 0 && Interlocked.CompareExchange(ref _injected, 1, 0) == 0)
            {
                // WHY: A named gate survives the writer's implicit DDL commits;
                // a row-lock gate would release before the adversarial ALTER.
                var barrier = "UPDATE generated_drop_checkpoint SET reached = 1 WHERE id = 1; "
                    + $"DO GET_LOCK('{gateName}', 30); DO RELEASE_LOCK('{gateName}'); ";

                command.CommandText = command.CommandText.Insert(position, barrier);
            }

            return ValueTask.FromResult(result);
        }
    }
}
