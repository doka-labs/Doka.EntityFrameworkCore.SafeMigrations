namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises execution with independently owned SQLite state.</summary>
public sealed class SqliteSafeMigrationExecutionIntegrationTests : SqliteSafeMigrationCatalogIntegrationTests
{
    [Fact]
    public async Task PreCancelledAnalysis_LeavesCatalogUnchanged()
    {
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE cancellation_probe (Id INTEGER NOT NULL);",
            cancellationToken: CancellationToken.None);

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
                "SELECT COUNT(*) FROM pragma_table_xinfo('cancellation_probe') WHERE name = 'Value';",
                cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentMigrators_ConvergeThroughTheEfMigrationLock()
    {
        // Arrange
        var databasePath = CreateOwnedDatabasePath();
        var connectionString = CreateOwnedFileConnectionString(databasePath);
        Exception? primaryFailure = null;

        try
        {
            await using (var setup = new SqliteConnection(connectionString))
            {
                await setup.OpenAsync(CancellationToken.None);
            }

            using var start = new Barrier(2);

            // Act
            // WHY: SQLite's provider performs synchronous native work even through
            // async APIs. Separate workers create real overlap, not an async wrapper.
            var results = await Task.WhenAll(
                Task.Run(() => ExecuteConcurrentMigrationAsync(
                    connectionString,
                    start,
                    cancellationToken: CancellationToken.None)),
                Task.Run(() => ExecuteConcurrentMigrationAsync(
                    connectionString,
                    start,
                    cancellationToken: CancellationToken.None)));

            await using var verification = new SqliteConnection(connectionString);
            await verification.OpenAsync(CancellationToken.None);
            var payloadColumns = await ScalarIntAsync(
                verification,
                "SELECT COUNT(*) FROM pragma_table_xinfo('pipeline_state') WHERE name = 'payload';",
                cancellationToken: CancellationToken.None);

            var probeTables = await ScalarIntAsync(
                verification,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'table' AND name = 'pipeline_probe';",
                cancellationToken: CancellationToken.None);

            var historyRows = await ScalarIntAsync(
                verification,
                "SELECT COUNT(*) FROM __EFMigrationsHistory "
                + $"WHERE MigrationId = '{SqliteCoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None);

            // Assert
            Assert.Contains(results, static result => result == ConcurrentExecutionResult.Completed);
            Assert.All(
                results,
                static result => Assert.True(
                    result is ConcurrentExecutionResult.Completed or ConcurrentExecutionResult.Busy));

            Assert.Equal(1, payloadColumns);
            Assert.Equal(1, probeTables);
            Assert.Equal(1, historyRows);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;

            throw;
        }
        finally
        {
            await OwnedTestOperationCleanup.CompleteAsync(primaryFailure,
                null,
                null,
                null,
                afterDrain: () =>
                {
                    File.Delete(databasePath);

                    return Task.CompletedTask;
                });
        }
    }

    /// <summary>Allocates one file identity per invocation, not per class or assembly.</summary>
    /// <returns>The GUID-qualified native database path owned by its caller.</returns>
    internal static string CreateOwnedDatabasePath() => Path.Combine(
        Path.GetTempPath(),
        "sqlite-safe-migrations-"
        + Guid
            .NewGuid()
            .ToString("N", CultureInfo.InvariantCulture)
        + ".db");

    /// <summary>Prevents file ownership from requiring a process-wide pool reset during cleanup.</summary>
    /// <param name="databasePath">The independently allocated path owned by this invocation.</param>
    /// <returns>The same migration-lock connection options with pooling explicitly disabled.</returns>
    internal static string CreateOwnedFileConnectionString(
        string databasePath
    ) => $"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Private;"
        + "Default Timeout=10;Foreign Keys=True;Pooling=False";

    /// <summary>Admits one independent migrator or withdraws it before its peer can be stranded.</summary>
    /// <param name="connectionString">The independently owned native database.</param>
    /// <param name="start">The shared single-phase admission barrier.</param>
    /// <param name="cancellationToken">Cancellation for initialization, admission and migration.</param>
    /// <returns>The completed or native-busy result without a scheduling-speed requirement.</returns>
    private static async Task<ConcurrentExecutionResult> ExecuteConcurrentMigrationAsync(
        string connectionString,
        Barrier start,
        CancellationToken cancellationToken
    )
    {
        var admitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var context = CreateContext(connection);

            // WHY: Admission is a state transition, not a CPU/load budget. A
            // worker that fails initialization withdraws in finally, so its
            // peer does not need a timer to escape an impossible rendezvous.
            start.SignalAndWait(cancellationToken);
            admitted = true;
            try
            {
                await context.Database.MigrateAsync(cancellationToken);

                return ConcurrentExecutionResult.Completed;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                return ConcurrentExecutionResult.Busy;
            }
        }
        finally
        {
            if (!admitted)
            {
                start.RemoveParticipant();
            }
        }
    }

    private enum ConcurrentExecutionResult
    {
        Completed,
        Busy,
    }
}
