namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlGeneratedDropIntegrationTests
{
    /// <summary>
    /// A generated dependency committed while DROP waits must be reclassified under the acquired owner lock.
    /// </summary>
    [Fact]
    public async Task ConcurrentGeneratedAdditionCannotBeSilentlyRemovedBySourceDrop()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_drop(source integer, unrelated integer); "
            + "INSERT INTO generated_drop VALUES(2,9);");

        await using var writer = new NpgsqlConnection(connectionString);
        await writer.OpenAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await using var add = writer.CreateCommand();
        add.CommandText = "ALTER TABLE generated_drop ADD COLUMN derived integer "
            + "GENERATED ALWAYS AS(source+1) STORED;";

        await add.ExecuteNonQueryAsync();
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        var dropPid = Assert.IsType<NpgsqlConnection>(context.Database.GetDbConnection()).ProcessID;
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("source", "generated_drop");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var committed = false;

        // Act
        var pending = Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        Exception? failure;
        try
        {
            await WaitForDropOwnerLockAsync(connectionString, dropPid, pending, deadline.Token);
            await transaction.CommitAsync(deadline.Token);
            committed = true;
            failure = await pending.WaitAsync(deadline.Token);
        }
        finally
        {
            if (!committed)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }

        // Assert
        Assert.Equal("P1001", Assert.IsType<PostgresException>(failure).SqlState);
        Assert.Equal("2:9:3", await ScalarStringAsync(connectionString,
            "SELECT source::text || ':' || unrelated::text || ':' || derived::text FROM generated_drop;"));
    }

    /// <summary>Coordinates on native lock state instead of a CPU-dependent delay or runtime threshold.</summary>
    private static async Task WaitForDropOwnerLockAsync(
        string connectionString, int processId, Task pending, CancellationToken cancellationToken)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync(cancellationToken);
        await using var command = observer.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_locks "
            + "WHERE pid=$1 AND relation='generated_drop'::regclass "
            + "AND mode='AccessExclusiveLock' AND NOT granted);";

        command.Parameters.AddWithValue(processId);
        while (!pending.IsCompleted)
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new InvalidOperationException("The native DROP completed before acquiring its conflicting owner lock.");
    }
}
