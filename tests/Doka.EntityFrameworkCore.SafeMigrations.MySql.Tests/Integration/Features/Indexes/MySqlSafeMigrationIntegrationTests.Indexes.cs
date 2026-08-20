namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task UnsupportedFilteredIndex_FailsBeforeTargetDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `events` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_events_filtered", "events", ["id"], filter: "id > 0");

        var exception =
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'events' "
                + "AND INDEX_NAME = 'ix_events_filtered';"));
    }

    [Fact]
    public async Task AdvancedIndexFacets_ConvergeAndDetectDirectionDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `advanced_indexes` (`id` int NOT NULL, `value` varchar(80) NULL);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedIndexDefinition(
            "ix_advanced_value",
            "advanced_indexes",
            [new ExpectedIndexKeyDefinition(column: "value", descending: true, prefixLength: 12)],
            method: "BTREE");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.EnsureIndex(
            new ExpectedIndexDefinition(
                definition.Name,
                definition.Table,
                [new ExpectedIndexKeyDefinition(column: "value", prefixLength: 12)],
                method: "BTREE"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, drift.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);
    }

    [Fact]
    public async Task FunctionalIndex_FollowsTheActiveEngineCapability()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `functional_indexes` (`value` varchar(80) NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_functional_lower_value",
                "functional_indexes",
                [new ExpectedIndexKeyDefinition(expression: "lower(value)")]),
            SafeMigrationPolicy.ThrowIfDifferent);

        var preflight = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

        if (Fixture.IsMariaDb)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
            Assert.Equal(
                SafeMigrationObservedState.Unsupported,
                Assert.Single(preflight.Assessments)
                    .ObservedState);
            return;
        }

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
    }
}
