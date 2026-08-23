namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    private const int ExpectedPerformanceTableCount = 100;
    private const int ForeignPerformanceTableCount = 1000;

    [Fact]
    public async Task FullRunner_LiveCatalogP95RemainsBoundedWithForeignObjectsAndPooling()
    {
        var templateConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var connectionString = new MySqlConnectionStringBuilder(templateConnectionString)
        {
            Pooling = true,
            ConnectionReset = true,
            MaximumPoolSize = 4,
        }.ConnectionString;

        await ExecuteSqlAsync(
            connectionString,
            BuildMySqlPerformanceTables("expected_perf_", ExpectedPerformanceTableCount, includeIndex: false));

        await using var context = CreateContext(connectionString);
        var operations = BuildMySqlPerformanceOperations(context.Database.ProviderName!);
        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("live-performance");
        var clean = await LivePerformanceEvidence.MeasureAsync(() => runner.AnalyzeAsync(context, operations, options));

        await ExecuteSqlAsync(
            connectionString,
            BuildMySqlPerformanceTables("foreign_perf_", ForeignPerformanceTableCount, includeIndex: true));

        var noisy = await LivePerformanceEvidence.MeasureAsync(() => runner.AnalyzeAsync(context, operations, options));

        LivePerformanceEvidence.Write(
            Fixture.IsMariaDb ? "mariadb" : "mysql",
            Fixture.ServerVersion.Version.ToString(),
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

    private static List<MigrationOperation> BuildMySqlPerformanceOperations(
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
                    [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")]),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        return builder.Operations.ToList();
    }

    private static string BuildMySqlPerformanceTables(
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
                var indexDdl = includeIndex ? ", INDEX `ix_payload` (`payload`)" : string.Empty;
                var payloadColumn = includeIndex ? ", `payload` int NULL" : string.Empty;

                return $"CREATE TABLE `{table}` (`id` int NOT NULL{payloadColumn}{indexDdl});";
            }));
}
