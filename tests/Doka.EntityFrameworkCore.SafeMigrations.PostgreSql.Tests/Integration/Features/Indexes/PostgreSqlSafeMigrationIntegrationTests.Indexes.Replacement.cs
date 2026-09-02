namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ProviderDropThenUniqueCreate_PreservesDataBlockedClassificationAndExistingIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE blocked_index_replacement (code integer NOT NULL); "
            + "CREATE INDEX ix_blocked_index_replacement "
            + "ON blocked_index_replacement (code); "
            + "INSERT INTO blocked_index_replacement (code) VALUES (1), (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndex("ix_blocked_index_replacement", "blocked_index_replacement");
        builder.CreateIndexIfNotExists(
            "ix_blocked_index_replacement",
            "blocked_index_replacement",
            ["code"],
            unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("blocked-index-replacement"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[1].Action);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class i "
                + "INNER JOIN pg_catalog.pg_namespace n ON n.oid = i.relnamespace "
                + "WHERE n.nspname = current_schema() "
                + "AND i.relname = 'ix_blocked_index_replacement' "
                + "AND i.relkind = 'i';"));
    }
}
