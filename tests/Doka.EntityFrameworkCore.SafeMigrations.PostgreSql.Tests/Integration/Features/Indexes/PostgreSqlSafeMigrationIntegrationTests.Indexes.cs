namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task AdvancedIndexFacets_ConvergeAndDetectDirectionDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE advanced_indexes (value text NULL, payload text NULL);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedIndexDefinition(
            "ix_advanced_value",
            "advanced_indexes",
            [new ExpectedIndexKeyDefinition(expression: "lower(value)", descending: true)],
            unique: true,
            filter: "value IS NOT NULL",
            includedColumns: ["payload"],
            method: "btree",
            nullsDistinct: false);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(definition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_advanced_collation",
                "advanced_indexes",
                [
                    new ExpectedIndexKeyDefinition(column: "value", collation: "C", operatorClass: "text_pattern_ops"),
                ],
                method: "btree"),
            SafeMigrationPolicy.ThrowIfDifferent);

        if (Fixture.ServerVersion.Major < 15)
        {
            var unsupported = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

            Assert.Equal(SafeMigrationReportStatus.Blocked, unsupported.Status);
            Assert.Contains(
                unsupported.Assessments,
                assessment => assessment.ObservedState == SafeMigrationObservedState.Unsupported);
            var exception =
                await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context, builder.Operations));

            Assert.Equal("P1002", exception.SqlState);
            Assert.Equal("doka_sm_unsupported", exception.MessageText);
            return;
        }

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.EnsureIndex(
            new ExpectedIndexDefinition(
                definition.Name,
                definition.Table,
                [new ExpectedIndexKeyDefinition(expression: "lower(value)")],
                unique: true,
                filter: definition.Filter,
                includedColumns: definition.IncludedColumns,
                method: definition.Method,
                nullsDistinct: definition.NullsDistinct),
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
}
