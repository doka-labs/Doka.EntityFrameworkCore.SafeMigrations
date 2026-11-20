namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises migrations with independently owned SQLite state.</summary>
public sealed class SqliteSafeMigrationMigrationsIntegrationTests : SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>
    /// Migrator mixes provider and safe operations and writes history after success.
    /// </summary>
    [Fact]
    public async Task Migrator_MixesProviderAndSafeOperationsAndWritesHistoryAfterSuccess()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE pipeline_state (Id INTEGER NOT NULL, "
            + "CONSTRAINT pk_pipeline_state PRIMARY KEY (Id));",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connection);

        // Act
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM pragma_table_xinfo('pipeline_state') WHERE name = 'payload';",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM __EFMigrationsHistory "
            + $"WHERE MigrationId = '{SqliteCoreConvergenceMigration.MigrationIdentifier}';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            1,
            observedScalar1);

        Assert.Equal(
            1,
            observedScalar2);
    }

    /// <summary>
    /// Migrator without safe registration writes neither DDL nor history row.
    /// </summary>
    [Fact]
    public async Task Migrator_WithoutSafeRegistrationWritesNeitherDdlNorHistoryRow()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connection, registerSafeMigrations: false);

        // Act
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Database.MigrateAsync(cancellationToken: CancellationToken.None));

        var observedScalar1 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM main.sqlite_schema "
            + "WHERE type = 'table' AND name IN ('pipeline_probe', 'pipeline_state');",
            cancellationToken: CancellationToken.None);

        var observedScalar2 = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM __EFMigrationsHistory "
            + $"WHERE MigrationId = '{SqliteCoreConvergenceMigration.MigrationIdentifier}';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(
            0,
            observedScalar1);

        Assert.Equal(
            0,
            observedScalar2);
    }

    /// <summary>
    /// Migration script generation with safe operations fails without partial SQL.
    /// </summary>
    [Fact]
    public async Task MigrationScriptGenerationWithSafeOperationsFailsWithoutPartialSql()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await using var context = CreateContext(connection);
        var migrator = context.GetService<IMigrator>();

        // Act
        var exception = Assert.Throws<NotSupportedException>(() => migrator.GenerateScript());

        // Assert
        Assert.Contains("cannot express", exception.Message, StringComparison.Ordinal);
    }
}
