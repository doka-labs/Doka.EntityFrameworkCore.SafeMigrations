namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    private const int ExpectedPerformanceTableCount = 100;
    private const int ForeignPerformanceTableCount = 1000;
    private const int PerformanceFixtureCommandTimeoutSeconds = 180;

    /// <summary>
    /// Classifies every mixed operation in the shared hundred-thousand-operation contract in original order.
    /// </summary>
    [SqlServerLiveFact]
    [Trait("Category", "LargeScale")]
    public async Task Analyzer_OneHundredThousandMixedOperationsRemainBoundedOrderedAndComplete()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.sqlserver_stress_target (id int NOT NULL); "
            + "INSERT INTO dbo.sqlserver_stress_target VALUES (1); "
            + "CREATE TABLE dbo.sqlserver_stress_alter (caption varchar(10) NOT NULL); "
            + "INSERT INTO dbo.sqlserver_stress_alter VALUES ('short'); "
            + "CREATE TABLE dbo.sqlserver_stress_managed (id int NOT NULL PRIMARY KEY, "
            + "managed_value nvarchar(32) NOT NULL); "
            + "INSERT INTO dbo.sqlserver_stress_managed VALUES (1, N'source');");
        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(PerformanceFixtureCommandTimeoutSeconds);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = LargeMigrationStressContract.Populate(builder, LargeMigrationStressDialect.SqlServer);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-large-mixed-migration"));

        // Assert
        expectation.AssertReport(report);
    }

    /// <summary>
    /// Executes and replays the shared fifty-thousand-row ensure, update, and delete contract.
    /// </summary>
    [SqlServerLiveFact]
    [Trait("Category", "LargeScale")]
    public async Task ModelManagedData_FiftyThousandMixedRowsConvergeAndReplayIdempotently()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.large_model_managed_rows (id int NOT NULL PRIMARY KEY, "
            + "managed_value nvarchar(32) NOT NULL);");
        await PopulateLargeModelManagedRowsAsync(connectionString);
        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(PerformanceFixtureCommandTimeoutSeconds);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = ModelManagedDataLargeExecutionContract.Populate(builder, "int", "nvarchar(32)");
        var runner = context.GetService<ISafeMigrationRunner>();
        var environment = await context.GetService<ISafeMigrationProviderAnalyzer>().GetEnvironmentAsync(context);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Act
        var initial = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() => runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-large-model-managed-data")));

        var initialExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        var replayExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        var replay = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() => runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-large-model-managed-data-replay")));

        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.large_model_managed_rows;");
        var targetRowCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.large_model_managed_rows WHERE managed_value = N'target';");

        var deletedRangeCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.large_model_managed_rows WHERE id >= 3000000;");

        ModelManagedDataLargeExecutionEvidence.Write("sqlserver", environment.ServerVersion, commands,
            initial.Measurement, initialExecution, replayExecution, replay.Measurement);

        // Assert
        expectation.AssertInitialReport(initial.Result);
        expectation.AssertReplayReport(replay.Result);
        Assert.Equal(expectation.FinalRowCount, rowCount);
        Assert.Equal(expectation.FinalRowCount, targetRowCount);
        Assert.Equal(0, deletedRangeCount);
    }

    /// <summary>
    /// Measures the pooled full runner against clean and noisy catalogs with the shared relative p95 gate.
    /// </summary>
    [SqlServerLiveFact]
    [Trait("Category", "LivePerformance")]
    public async Task FullRunner_LiveCatalogP95RemainsBoundedWithForeignObjectsAndPooling()
    {
        // Arrange
        var databaseConnectionString = await Fixture.CreateDatabaseAsync();
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

        // Act
        var clean = await LivePerformanceEvidence.MeasureAsync(() =>
            runner.AnalyzeAsync(context, builder.Operations, options));

        await ExecuteSqlAsync(connectionString,
            BuildSqlServerPerformanceTables("foreign_perf_", ForeignPerformanceTableCount, includeIndex: true));
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
        Assert.True(noisy.P95Milliseconds <= (clean.P95Milliseconds * 2d) + 250d,
            $"Noisy p95 {noisy.P95Milliseconds:F3} ms exceeded clean p95 {clean.P95Milliseconds:F3} ms.");
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

    private static async Task PopulateLargeModelManagedRowsAsync(string connectionString)
    {
        using var rows = new DataTable { Locale = CultureInfo.InvariantCulture };
        rows.Columns.Add("id", typeof(int));
        rows.Columns.Add("managed_value", typeof(string));
        foreach (var row in ModelManagedDataLargeExecutionContract.InitialRows())
        {
            rows.Rows.Add(row.Id, row.Value);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var bulkCopy = new SqlBulkCopy(connection)
        {
            DestinationTableName = "dbo.large_model_managed_rows",
            BatchSize = 1000,
            BulkCopyTimeout = PerformanceFixtureCommandTimeoutSeconds,
        };

        bulkCopy.ColumnMappings.Add("id", "id");
        bulkCopy.ColumnMappings.Add("managed_value", "managed_value");

        await bulkCopy.WriteToServerAsync(rows);
    }
}
