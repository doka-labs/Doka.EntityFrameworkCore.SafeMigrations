namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Fences integer repairs against concurrent DDL without acquiring rights for no-op replay.</summary>
public sealed class PostgreSqlIntegerWideningAtomicIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the assembly-owned engine with independent test-local database ownership.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public PostgreSqlIntegerWideningAtomicIntegrationTests(PostgreSqlContainerFixture fixture) : base(fixture) { }

    /// <summary>Rechecks source/dependency facts after waiting for the exact owner lock held by native DDL.</summary>
    /// <param name="mutation">The committed metadata change observed only after lock acquisition.</param>
    [Theory]
    [InlineData("none")]
    [InlineData("default")]
    [InlineData("check")]
    public async Task IntegerWideningReclassifiesAfterConcurrentDdl(string mutation)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE atomic_integer_rows (value integer NOT NULL); INSERT INTO atomic_integer_rows VALUES (7);");
        var applicationName = "integer_fence_" + Guid.NewGuid().ToString("N");
        var migrationConnection = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName,
        }.ConnectionString;

        await using var context = CreateContext(migrationConnection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("atomic_integer_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);
        await using var holder = new NpgsqlConnection(connectionString);
        await holder.OpenAsync(CancellationToken.None);
        await using var transaction = await holder.BeginTransactionAsync(CancellationToken.None);
        await using var holderCommand = holder.CreateCommand();
        holderCommand.Transaction = transaction;
        holderCommand.CommandText = "LOCK TABLE ONLY atomic_integer_rows IN ACCESS EXCLUSIVE MODE;";
        await holderCommand.ExecuteNonQueryAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));

        // Act
        var execution = ExecuteOperationsAsync(context, builder.Operations, cancellation.Token);
        await WaitForIntegerOwnerLockAsync(connectionString, applicationName, execution, cancellation.Token);
        if (mutation != "none")
        {
            holderCommand.CommandText = mutation == "default"
                ? "ALTER TABLE atomic_integer_rows ALTER COLUMN value SET DEFAULT 7;"
                : "ALTER TABLE atomic_integer_rows ADD CONSTRAINT ck_atomic_integer CHECK(value > 0);";
            await holderCommand.ExecuteNonQueryAsync(cancellation.Token);
        }

        await transaction.CommitAsync(cancellation.Token);
        var failure = await Record.ExceptionAsync(() => execution);

        // Assert
        if (mutation == "none")
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("P1001", Assert.IsType<PostgresException>(failure).SqlState);
        }

        Assert.Equal(mutation == "none" ? "bigint" : "integer", await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema='public' "
            + "AND table_name='atomic_integer_rows' AND column_name='value';"));
        Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT value FROM atomic_integer_rows;"));
    }

    /// <summary>Rejects repairs without explicit lock authority but allows exact replay without a lock.</summary>
    /// <param name="targetAlreadyWidened">Whether the complete target contract already matches.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntegerWideningNeverBorrowsImplicitOwnerLockPrivileges(bool targetAlreadyWidened)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var role = "integer_lock_owner_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(connectionString, "CREATE ROLE " + role + "; "
            + "GRANT USAGE, CREATE ON SCHEMA public TO " + role + "; "
            + "CREATE TABLE integer_lock_rows(value " + (targetAlreadyWidened ? "bigint" : "integer")
            + " NOT NULL); INSERT INTO integer_lock_rows VALUES(7); "
            + "ALTER TABLE integer_lock_rows OWNER TO " + role + "; "
            + "REVOKE ALL ON integer_lock_rows FROM " + role + ";");

        try
        {
            await using var context = CreateContext(connectionString);
            await context.Database.OpenConnectionAsync(CancellationToken.None);
            await context.Database.ExecuteSqlAsync($"SELECT pg_catalog.set_config('role', {role}, false);");
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.AlterColumnIfDifferent("integer_lock_rows",
                new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
                SafeMigrationPolicy.RepairIfSafe);

            // Act
            var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
                context, builder.Operations, new SafeMigrationRunOptions("integer-lock-permission"));
            var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

            // Assert
            Assert.Equal(targetAlreadyWidened ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectUnsupported,
                Assert.Single(report.Assessments).Action);
            if (targetAlreadyWidened)
            {
                Assert.Null(failure);
            }
            else
            {
                Assert.Equal("integer_widening_unproven", report.Assessments[0].AnalysisCode);
                Assert.Equal("P1002", Assert.IsType<PostgresException>(failure).SqlState);
            }

            Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT value FROM integer_lock_rows;"));
        }
        finally
        {
            await ExecuteSqlAsync(connectionString, "ALTER TABLE integer_lock_rows OWNER TO postgres; "
                + "REVOKE USAGE, CREATE ON SCHEMA public FROM " + role + "; DROP ROLE " + role + ";");
        }
    }

    /// <summary>Refuses snapshot-frozen repairs without changing the caller's transaction isolation.</summary>
    /// <param name="isolation">The caller-owned isolation level.</param>
    /// <param name="targetAlreadyWidened">Whether only exact target replay remains.</param>
    [Theory]
    [InlineData(IsolationLevel.RepeatableRead, false)]
    [InlineData(IsolationLevel.Serializable, false)]
    [InlineData(IsolationLevel.RepeatableRead, true)]
    [InlineData(IsolationLevel.Serializable, true)]
    public async Task IntegerWideningRequiresFreshRuntimeSnapshots(IsolationLevel isolation, bool targetAlreadyWidened)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE integer_isolation_rows(value "
            + (targetAlreadyWidened ? "bigint" : "integer") + " NOT NULL); "
            + "INSERT INTO integer_isolation_rows VALUES(7);");
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(isolation, CancellationToken.None);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("integer_isolation_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        await transaction.RollbackAsync(CancellationToken.None);

        // Assert
        if (targetAlreadyWidened)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("P1002", Assert.IsType<PostgresException>(failure).SqlState);
        }

        Assert.Equal(targetAlreadyWidened ? "bigint" : "integer", await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema='public' "
            + "AND table_name='integer_isolation_rows' AND column_name='value';"));
        Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT value FROM integer_isolation_rows;"));
    }

    /// <summary>Waits on the server-observed exact owner request rather than an elapsed-time guess.</summary>
    private static async Task WaitForIntegerOwnerLockAsync(
        string connectionString, string applicationName, Task execution, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks held "
            + "JOIN pg_catalog.pg_stat_activity session ON session.pid = held.pid "
            + "JOIN pg_catalog.pg_class owner ON owner.oid = held.relation "
            + "WHERE session.application_name = @application AND owner.relname = 'atomic_integer_rows' "
            + "AND held.mode = 'AccessExclusiveLock' AND NOT held.granted);";
        command.Parameters.AddWithValue("application", applicationName);

        while (!Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture))
        {
            if (execution.IsCompleted)
            {
                await execution;

                throw new InvalidOperationException("The integer repair completed without reaching its owner fence.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

}
