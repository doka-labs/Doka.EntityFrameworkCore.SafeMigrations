namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlGeneratedDropIntegrationTests
{
    /// <summary>Safe DROP never grants itself the explicit-lock privilege missing from a restricted owner.</summary>
    [Fact]
    public async Task RestrictedOwnerCannotBorrowDropAuthorityForAtomicLock()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var role = "drop_lock_owner_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(connectionString, "CREATE ROLE " + role + "; "
            + "GRANT USAGE, CREATE ON SCHEMA public TO " + role + "; "
            + "CREATE TABLE drop_lock_permissions(source integer, unrelated integer); "
            + "INSERT INTO drop_lock_permissions VALUES(2,9); "
            + "ALTER TABLE drop_lock_permissions OWNER TO " + role + "; "
            + "REVOKE ALL ON drop_lock_permissions FROM " + role + ";");

        try
        {
            await using var context = CreateContext(connectionString);
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlAsync($"SELECT pg_catalog.set_config('role', {role}, false);");
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.DropColumnIfExists("source", "drop_lock_permissions");

            // Act
            var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("generated-drop-lock-permission-unproven"), CancellationToken.None);

            var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

            // Assert
            Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
            Assert.Equal("generated_column_drop_unproven", report.Assessments[0].AnalysisCode);
            Assert.Equal("P1002", Assert.IsType<PostgresException>(failure).SqlState);
            Assert.Equal("2:9", await ScalarStringAsync(connectionString,
                "SELECT source::text || ':' || unrelated::text FROM drop_lock_permissions;"));
        }
        finally
        {
            await ExecuteSqlAsync(connectionString, "ALTER TABLE drop_lock_permissions OWNER TO postgres; "
                + "REVOKE USAGE, CREATE ON SCHEMA public FROM " + role + "; DROP ROLE " + role + ";");
        }
    }

    /// <summary>Native ownership permits DROP even after the independent explicit-lock rights are revoked.</summary>
    [Fact]
    public async Task NativeOwnerDropDoesNotImplyExplicitAccessExclusiveLockPermission()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var role = "drop_lock_owner_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(connectionString, "CREATE ROLE " + role + "; "
            + "GRANT USAGE, CREATE ON SCHEMA public TO " + role + "; "
            + "CREATE TABLE drop_lock_permissions(source integer, unrelated integer); "
            + "ALTER TABLE drop_lock_permissions OWNER TO " + role + "; "
            + "REVOKE ALL ON drop_lock_permissions FROM " + role + ";");

        try
        {
            await using var context = CreateContext(connectionString);
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlAsync($"SELECT pg_catalog.set_config('role', {role}, false);");

            // Act
            var lockFailure = await Record.ExceptionAsync(() => context.Database.ExecuteSqlRawAsync(
                "DO $$ BEGIN LOCK TABLE drop_lock_permissions IN ACCESS EXCLUSIVE MODE; END $$;"));

            var dropFailure = await Record.ExceptionAsync(() => context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE drop_lock_permissions DROP COLUMN source;"));

            // Assert
            Assert.Equal("42501", Assert.IsType<PostgresException>(lockFailure).SqlState);
            Assert.Null(dropFailure);
            Assert.Equal(1, await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='drop_lock_permissions'::regclass "
                + "AND attnum>0 AND NOT attisdropped;"));
        }
        finally
        {
            await ExecuteSqlAsync(connectionString, "ALTER TABLE drop_lock_permissions OWNER TO postgres; "
                + "REVOKE USAGE, CREATE ON SCHEMA public FROM " + role + "; DROP ROLE " + role + ";");
        }
    }

    /// <summary>
    /// A lock taken after a transaction snapshot cannot make a new generated dependency visible to SQL.
    /// </summary>
    /// <param name="isolation">The native transaction-snapshot isolation level.</param>
    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task NativeRepeatableReadCatalogSnapshotRemainsOldAfterOwnerLock(System.Data.IsolationLevel isolation)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE drop_snapshot(source integer, unrelated integer);");
        await using var reader = new NpgsqlConnection(connectionString);
        await reader.OpenAsync();

        // Act
        await using var transaction = await reader.BeginTransactionAsync(isolation);
        await using var count = reader.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM pg_catalog.pg_attribute "
            + "WHERE attrelid='drop_snapshot'::regclass AND attgenerated<>'' AND NOT attisdropped;";

        var initial = Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        await ExecuteSqlAsync(connectionString,
            "ALTER TABLE drop_snapshot ADD COLUMN derived integer GENERATED ALWAYS AS(source+1) STORED;");

        await using var locked = reader.CreateCommand();
        locked.CommandText = "LOCK TABLE drop_snapshot IN ACCESS EXCLUSIVE MODE;";
        await locked.ExecuteNonQueryAsync();
        var afterLock = Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        await transaction.RollbackAsync();

        // Assert
        Assert.Equal(0, initial);
        Assert.Equal(0, afterLock);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='drop_snapshot'::regclass "
            + "AND attgenerated<>'' AND NOT attisdropped;"));
    }

    /// <summary>Transaction-snapshot callers cannot turn an old dependency snapshot into an executable DROP.</summary>
    /// <param name="isolation">The caller's unchanged transaction isolation.</param>
    /// <param name="missing">Whether the operation is a genuine native no-op.</param>
    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead, false)]
    [InlineData(System.Data.IsolationLevel.Serializable, false)]
    [InlineData(System.Data.IsolationLevel.RepeatableRead, true)]
    [InlineData(System.Data.IsolationLevel.Serializable, true)]
    public async Task SnapshotIsolationRefusesMutationButPreservesMissingNoOp(
        System.Data.IsolationLevel isolation, bool missing)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE drop_snapshot(source integer, unrelated integer); INSERT INTO drop_snapshot VALUES(2,9);");

        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(isolation);
        await context.Database.ExecuteSqlRawAsync("SELECT 1;");
        var processId = Assert.IsType<NpgsqlConnection>(context.Database.GetDbConnection()).ProcessID;
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(missing ? "missing" : "source", "drop_snapshot");

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var exclusive = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_locks WHERE pid=" + processId.ToString(CultureInfo.InvariantCulture)
            + " AND relation='drop_snapshot'::regclass AND mode='AccessExclusiveLock';");

        await transaction.RollbackAsync();

        // Assert
        if (missing)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("P1002", Assert.IsType<PostgresException>(failure).SqlState);
        }

        Assert.Equal(0, exclusive);
        Assert.Equal("2:9", await ScalarStringAsync(connectionString,
            "SELECT source::text || ':' || unrelated::text FROM drop_snapshot;"));
    }
}
