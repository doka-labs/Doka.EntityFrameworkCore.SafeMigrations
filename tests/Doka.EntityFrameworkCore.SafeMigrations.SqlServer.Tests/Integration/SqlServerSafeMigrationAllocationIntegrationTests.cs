namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies process-total allocation SQL Server contracts against isolated databases.</summary>
[Collection(ProcessWideTestGroup.Name)]
public sealed class SqlServerSafeMigrationAllocationIntegrationTests : SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Creates the feature suite with the assembly-owned SQL Server fixture.</summary>
    /// <param name="fixture">The shared fixture providing a database per test.</param>
    public SqlServerSafeMigrationAllocationIntegrationTests(
        SqlServerContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>
    /// Executes and replays the shared fifty-thousand-row ensure, update, and delete contract.
    /// </summary>
    [SqlServerLiveFact]
    [Trait("Category", "LargeScale")]
    public async Task ModelManagedData_FiftyThousandMixedRowsConvergeAndReplayIdempotently()
    {
        // Arrange
        SqlServerLiveQualificationEvidence.WriteStage("managed-50k", "fixture-setup");
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.large_model_managed_rows (id int NOT NULL PRIMARY KEY, "
            + "managed_value nvarchar(32) NOT NULL);");
        SqlServerLiveQualificationEvidence.WriteStage("managed-50k", "populate-source-rows");
        await PopulateLargeModelManagedRowsAsync(connectionString);
        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(PerformanceFixtureCommandTimeoutSeconds);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = ModelManagedDataLargeExecutionContract.Populate(builder, "int", "nvarchar(32)");
        var runner = context.GetService<ISafeMigrationRunner>();
        var environment = await context.GetService<ISafeMigrationProviderAnalyzer>().GetEnvironmentAsync(context);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        using var stageCapture = SqlServerLiveQualificationEvidence.CaptureAnalysisStages("managed-50k");

        // Act
        SqlServerLiveQualificationEvidence.WriteStage("managed-50k", "initial-analysis");
        var initial = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() => runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-large-model-managed-data")));

        SqlServerLiveQualificationEvidence.WriteStage("managed-50k", "initial-execution");
        var initialExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        SqlServerLiveQualificationEvidence.WriteStage("managed-50k", "replay-execution");
        var replayExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        SqlServerLiveQualificationEvidence.WriteStage("managed-50k", "replay-analysis");
        var replay = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() => runner.AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-large-model-managed-data-replay")));

        SqlServerLiveQualificationEvidence.WriteStage("managed-50k", "verify-rows");
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
        stageCapture.ThrowIfWriteFailed();
        SqlServerLiveQualificationEvidence.WriteStage("managed-50k", "completed");
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
