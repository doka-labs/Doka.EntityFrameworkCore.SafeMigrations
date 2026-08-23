namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    private const int ExpectedPerformanceTableCount = 100;
    private const int ForeignPerformanceTableCount = 1000;

    [Fact]
    public async Task FullRunner_LiveCatalogP95RemainsBoundedWithForeignObjectsAndPooling()
    {
        var templateConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var connectionString = new NpgsqlConnectionStringBuilder(templateConnectionString)
        {
            Pooling = true,
            NoResetOnClose = false,
            MaxPoolSize = 4,
        }.ConnectionString;

        await ExecuteSqlAsync(
            connectionString,
            BuildPostgreSqlPerformanceTables("expected_perf_", ExpectedPerformanceTableCount, includeIndex: false));

        await using var context = CreateContext(connectionString);
        var operations = BuildPostgreSqlPerformanceOperations(context.Database.ProviderName!);
        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("live-performance");
        var clean = await LivePerformanceEvidence.MeasureAsync(() => runner.AnalyzeAsync(context, operations, options));

        await ExecuteSqlAsync(
            connectionString,
            BuildPostgreSqlPerformanceTables("foreign_perf_", ForeignPerformanceTableCount, includeIndex: true));

        var noisy = await LivePerformanceEvidence.MeasureAsync(() => runner.AnalyzeAsync(context, operations, options));

        LivePerformanceEvidence.Write(
            "postgresql",
            Fixture.ServerVersion.ToString(),
            clean,
            noisy,
            ExpectedPerformanceTableCount,
            ForeignPerformanceTableCount);

        Assert.All(
            clean.LastReport.Assessments,
            static assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
        Assert.Empty(clean.LastReport.UnexpectedObjects);
        Assert.Equal(
            ForeignPerformanceTableCount,
            noisy.LastReport.UnexpectedObjects.Count(static value =>
                value.ObjectKind == SafeMigrationDatabaseObjectKind.Table));
        Assert.DoesNotContain(
            noisy.LastReport.UnexpectedObjects,
            static value => value.ObjectKind != SafeMigrationDatabaseObjectKind.Table);
        Assert.Equal(
            clean.LastReport.Assessments.Select(static value => value.Code),
            noisy.LastReport.Assessments.Select(static value => value.Code));
        Assert.True(
            noisy.P95Milliseconds <= (clean.P95Milliseconds * 2d) + 250d,
            $"Noisy p95 {noisy.P95Milliseconds:F3} ms exceeded clean p95 {clean.P95Milliseconds:F3} ms.");
    }

    private static List<MigrationOperation> BuildPostgreSqlPerformanceOperations(
        string providerName
    )
    {
        var builder = new MigrationBuilder(providerName);
        for (var index = 0; index < ExpectedPerformanceTableCount; index++)
        {
            var table = $"expected_perf_{index:D4}";
            _ = builder.EnsureTable(
                new ExpectedTableDefinition(
                    table,
                    [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer")]),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        return builder.Operations.ToList();
    }

    private static string BuildPostgreSqlPerformanceTables(
        string prefix,
        int count,
        bool includeIndex
    ) => string.Join(
        Environment.NewLine,
        Enumerable
            .Range(0, count)
            .Select(index =>
            {
                var table = $"{prefix}{index:D4}";
                var payloadColumn = includeIndex ? ", \"payload\" integer NULL" : string.Empty;
                var indexDdl = includeIndex
                    ? $" CREATE INDEX \"ix_{table}_payload\" ON \"{table}\" (\"payload\");"
                    : string.Empty;

                return $"CREATE TABLE \"{table}\" (\"id\" integer NOT NULL{payloadColumn});{indexDdl}";
            }));
}
