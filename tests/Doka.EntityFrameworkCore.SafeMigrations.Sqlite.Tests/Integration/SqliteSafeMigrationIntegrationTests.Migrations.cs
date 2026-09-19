namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    [Fact]
    public async Task Migrator_MixesProviderAndSafeOperationsAndWritesHistoryAfterSuccess()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE pipeline_state (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_pipeline_state PRIMARY KEY (Id));");
        await using var context = CreateContext(connection);

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM pragma_table_xinfo('pipeline_state') WHERE name = 'payload';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM __EFMigrationsHistory "
                + $"WHERE MigrationId = '{SqliteCoreConvergenceMigration.MigrationIdentifier}';"));
    }

    [Fact]
    public async Task Migrator_WithoutSafeRegistrationWritesNeitherDdlNorHistoryRow()
    {
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection, registerSafeMigrations: false);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Database.MigrateAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM main.sqlite_schema "
                + "WHERE type = 'table' AND name IN ('pipeline_probe', 'pipeline_state');"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM __EFMigrationsHistory "
                + $"WHERE MigrationId = '{SqliteCoreConvergenceMigration.MigrationIdentifier}';"));
    }

    [Fact]
    public async Task MigrationScriptGenerationWithSafeOperationsFailsWithoutPartialSql()
    {
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var migrator = context.GetService<IMigrator>();

        var exception = Assert.Throws<NotSupportedException>(() => migrator.GenerateScript());

        Assert.Contains("cannot express", exception.Message, StringComparison.Ordinal);
    }
}
