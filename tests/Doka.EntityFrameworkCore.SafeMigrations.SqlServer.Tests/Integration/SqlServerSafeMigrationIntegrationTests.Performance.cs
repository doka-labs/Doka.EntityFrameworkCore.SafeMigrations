namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies performance SQL Server contracts against isolated databases.</summary>
public sealed class SqlServerSafeMigrationPerformanceIntegrationTests : SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Creates the feature suite with the assembly-owned SQL Server fixture.</summary>
    /// <param name="fixture">The shared fixture providing a database per test.</param>
    public SqlServerSafeMigrationPerformanceIntegrationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    private const int ExpectedPerformanceTableCount = 100;

    private const int ForeignPerformanceTableCount = 1000;

    private const string MixedStressSchemaSql =
        "CREATE TABLE dbo.sqlserver_stress_target (id int NOT NULL); "
        + "INSERT INTO dbo.sqlserver_stress_target VALUES (1); "
        + "CREATE TABLE dbo.sqlserver_stress_alter (caption varchar(10) NOT NULL); "
        + "INSERT INTO dbo.sqlserver_stress_alter VALUES ('short'); "
        + "CREATE TABLE dbo.sqlserver_stress_managed (id int NOT NULL PRIMARY KEY, "
        + "managed_value nvarchar(32) NOT NULL);";

    /// <summary>Checks managed-data deferral in several mixed-stream cycles before costly stress qualification.</summary>
    [SqlServerLiveFact]
    public async Task Analyzer_BoundedMixedOperationsRetainExactDeferredContracts()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, MixedStressSchemaSql);
        await PopulateStressModelManagedRowsAsync(connectionString);
        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(PerformanceFixtureCommandTimeoutSeconds);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = LargeMigrationStressContract.Populate(
            builder, LargeMigrationStressDialect.SqlServer, operationCount: 128);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-bounded-mixed-migration"));

        // Assert
        expectation.AssertReport(report);
    }

    /// <summary>
    /// Classifies every mixed operation in the shared hundred-thousand-operation contract in original order.
    /// </summary>
    [SqlServerLiveFact]
    [Trait("Category", "LargeScale")]
    public async Task Analyzer_OneHundredThousandMixedOperationsRemainBoundedOrderedAndComplete()
    {
        // Arrange
        SqlServerLiveQualificationEvidence.WriteStage("mixed-100k", "fixture-setup");
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, MixedStressSchemaSql);
        SqlServerLiveQualificationEvidence.WriteStage("mixed-100k", "populate-source-rows");
        await PopulateStressModelManagedRowsAsync(connectionString);
        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(PerformanceFixtureCommandTimeoutSeconds);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = LargeMigrationStressContract.Populate(builder, LargeMigrationStressDialect.SqlServer);
        using var stageCapture = SqlServerLiveQualificationEvidence.CaptureAnalysisStages("mixed-100k");

        // Act
        SqlServerLiveQualificationEvidence.WriteStage("mixed-100k", "analyze");
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-large-mixed-migration"));

        // Assert
        expectation.AssertReport(report);
        stageCapture.ThrowIfWriteFailed();
        SqlServerLiveQualificationEvidence.WriteStage("mixed-100k", "completed");
    }

    /// <summary>
    /// Records informational pooled-runner timings while asserting clean and noisy catalog behavior.
    /// </summary>
    [SqlServerLiveFact]
    [Trait("Category", "LivePerformance")]
    public async Task FullRunner_LiveCatalogP95RemainsBoundedWithForeignObjectsAndPooling()
    {
        // Arrange
        SqlServerLiveQualificationEvidence.WriteStage("catalog-p95", "fixture-setup");
        var databaseConnectionString = await CreateDatabaseAsync();
        var connectionString = new SqlConnectionStringBuilder(databaseConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 4,
        }.ConnectionString;

        await ExecuteSqlAsync(connectionString,
            BuildSqlServerPerformanceTables("expected_perf_", ExpectedPerformanceTableCount, includeIndex: false));
        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(PerformanceFixtureCommandTimeoutSeconds);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var ordinal = 0; ordinal < ExpectedPerformanceTableCount; ordinal++)
        {
            builder.EnsureTable(new ExpectedTableDefinition($"expected_perf_{ordinal:D4}",
                [new ExpectedColumnDefinition("id", typeof(int), false, "int")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("sqlserver-live-performance");
        var environment = await context.GetService<ISafeMigrationProviderAnalyzer>().GetEnvironmentAsync(context);
        using var stageCapture = SqlServerLiveQualificationEvidence.CaptureAnalysisStages("catalog-p95");

        // Act
        SqlServerLiveQualificationEvidence.WriteStage("catalog-p95", "clean-samples");
        var clean = await LivePerformanceEvidence.MeasureAsync(() =>
            runner.AnalyzeAsync(context, builder.Operations, options));

        SqlServerLiveQualificationEvidence.WriteStage("catalog-p95", "create-foreign-objects");
        await ExecuteSqlAsync(connectionString,
            BuildSqlServerPerformanceTables("foreign_perf_", ForeignPerformanceTableCount, includeIndex: true));
        SqlServerLiveQualificationEvidence.WriteStage("catalog-p95", "noisy-samples");
        var noisy = await LivePerformanceEvidence.MeasureAsync(() =>
            runner.AnalyzeAsync(context, builder.Operations, options));

        LivePerformanceEvidence.Write("sqlserver", environment.ServerVersion, clean, noisy,
            ExpectedPerformanceTableCount, ForeignPerformanceTableCount);

        // Assert
        Assert.All(clean.LastReport.Assessments,
            static assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
        Assert.Empty(clean.LastReport.UnexpectedObjects);
        Assert.Equal(ForeignPerformanceTableCount, noisy.LastReport.UnexpectedObjects.Count(
            static value => value.ObjectKind == SafeMigrationDatabaseObjectKind.Table));
        Assert.DoesNotContain(noisy.LastReport.UnexpectedObjects,
            static value => value.ObjectKind != SafeMigrationDatabaseObjectKind.Table);
        Assert.Equal(clean.LastReport.Assessments.Select(static value => value.Code),
            noisy.LastReport.Assessments.Select(static value => value.Code));
        stageCapture.ThrowIfWriteFailed();
        SqlServerLiveQualificationEvidence.WriteStage("catalog-p95", "completed");
    }

    private static string BuildSqlServerPerformanceTables(
        string prefix,
        int count,
        bool includeIndex
    )
    {
        var sql = new StringBuilder();
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            var table = $"{prefix}{ordinal:D4}";
            sql.Append("CREATE TABLE dbo.[").Append(table).Append("] (id int NOT NULL); ");
            if (includeIndex)
            {
                sql.Append("CREATE INDEX [ix_").Append(table).Append("] ON dbo.[")
                    .Append(table).Append("] (id); ");
            }
        }

        return sql.ToString();
    }

    /// <summary>Creates one source row for every linear managed update in the mixed stress stream.</summary>
    /// <param name="connectionString">The isolated stress database connection.</param>
    /// <returns>A task that completes when all ordinal-specific source rows are available.</returns>
    private static async Task PopulateStressModelManagedRowsAsync(
        string connectionString
    )
    {
        using var rows = new DataTable { Locale = CultureInfo.InvariantCulture };
        rows.Columns.Add("id", typeof(int));
        rows.Columns.Add("managed_value", typeof(string));
        var ordinals = LargeMigrationStressContract.ModelManagedUpdateOrdinals(
            LargeMigrationStressDialect.SqlServer);

        foreach (var ordinal in ordinals)
        {
            rows.Rows.Add(LargeMigrationStressContract.ModelManagedUpdateKey(ordinal), "source");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var bulkCopy = new SqlBulkCopy(connection)
        {
            DestinationTableName = "dbo.sqlserver_stress_managed",
            BatchSize = 1000,
            BulkCopyTimeout = PerformanceFixtureCommandTimeoutSeconds,
        };

        bulkCopy.ColumnMappings.Add("id", "id");
        bulkCopy.ColumnMappings.Add("managed_value", "managed_value");

        await bulkCopy.WriteToServerAsync(rows);
    }
}
