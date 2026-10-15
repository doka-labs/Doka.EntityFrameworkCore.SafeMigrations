namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises execution with independently owned SQLite state.</summary>
public sealed class SqliteSafeMigrationExecutionIntegrationTests : SqliteSafeMigrationCatalogIntegrationTests
{
    [Fact]
    public async Task PreCancelledAnalysis_LeavesCatalogUnchanged()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE cancellation_probe (Id INTEGER NOT NULL);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>(
            "Value",
            "cancellation_probe",
            type: "TEXT",
            nullable: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-cancelled-analysis"),
                cancellation.Token));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('cancellation_probe') WHERE name = 'Value';"));
    }

    [Fact]
    public async Task ConcurrentMigrators_ConvergeThroughTheEfMigrationLock()
    {
        var databasePath = CreateOwnedDatabasePath();
        var connectionString = CreateOwnedFileConnectionString(databasePath);

        try
        {
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(CancellationToken.None);
            }

            using var start = new Barrier(2);

            var results = await Task.WhenAll(
                Task.Run(() => ExecuteConcurrentMigrationAsync(connectionString, start)),
                Task.Run(() => ExecuteConcurrentMigrationAsync(connectionString, start)));

            await using var verification = new SqliteConnection(connectionString);
            await verification.OpenAsync(CancellationToken.None);

            Assert.Contains(results, static result => result == ConcurrentExecutionResult.Completed);
            Assert.All(
                results,
                static result => Assert.True(
                    result is ConcurrentExecutionResult.Completed or ConcurrentExecutionResult.Busy));
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    verification,
                    "SELECT COUNT(*) FROM pragma_table_xinfo('pipeline_state') WHERE name = 'payload';"));
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    verification,
                    "SELECT COUNT(*) FROM main.sqlite_schema "
                    + "WHERE type = 'table' AND name = 'pipeline_probe';"));
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    verification,
                    "SELECT COUNT(*) FROM __EFMigrationsHistory "
                    + $"WHERE MigrationId = '{SqliteCoreConvergenceMigration.MigrationIdentifier}';"));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>Allocates one file identity per invocation, not per class or assembly.</summary>
    /// <returns>The GUID-qualified native database path owned by its caller.</returns>
    internal static string CreateOwnedDatabasePath() => Path.Combine(
        Path.GetTempPath(),
        "sqlite-safe-migrations-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".db");

    /// <summary>Prevents file ownership from requiring a process-wide pool reset during cleanup.</summary>
    /// <param name="databasePath">The independently allocated path owned by this invocation.</param>
    /// <returns>The same migration-lock connection options with pooling explicitly disabled.</returns>
    internal static string CreateOwnedFileConnectionString(string databasePath) =>
        $"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Private;"
        + "Default Timeout=10;Foreign Keys=True;Pooling=False";

    private static async Task<ConcurrentExecutionResult> ExecuteConcurrentMigrationAsync(
        string connectionString,
        Barrier start
    )
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var context = CreateContext(connection);
        if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Concurrent SQLite migrators did not reach the start barrier.");
        }

        try
        {
            await context.Database.MigrateAsync(CancellationToken.None);

            return ConcurrentExecutionResult.Completed;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return ConcurrentExecutionResult.Busy;
        }
    }

    private enum ConcurrentExecutionResult
    {
        Completed,
        Busy,
    }
}
