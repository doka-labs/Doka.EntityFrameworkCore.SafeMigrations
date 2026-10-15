namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Measures process-total managed-data allocations without competing collections.</summary>
[Collection(ProcessWideTestGroup.Name)]
public sealed class PostgreSqlModelManagedDataAllocationIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this allocation case.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlModelManagedDataAllocationIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    [Fact]
    [Trait("Category", "LargeScale")]
    public async Task ModelManagedData_FiftyThousandMixedRowsConvergeAndReplayIdempotently()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE large_model_managed_rows ("
            + "id integer NOT NULL PRIMARY KEY, managed_value character varying(32) NOT NULL);"
            + BuildPostgreSqlModelManagedInitialRows());

        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(PerformanceFixtureCommandTimeoutSeconds);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = ModelManagedDataLargeExecutionContract.Populate(
            builder,
            "integer",
            "character varying(32)");

        var runner = context.GetService<ISafeMigrationRunner>();
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        var initial = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() => runner.AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("large-model-managed-data"),
                CancellationToken.None));

        var initialExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var replayExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var replay = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() => runner.AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("large-model-managed-data-replay"),
                CancellationToken.None));

        ModelManagedDataLargeExecutionEvidence.Write(
            "postgresql",
            Fixture.ServerVersion.ToString(),
            commands,
            initial.Measurement,
            initialExecution,
            replayExecution,
            replay.Measurement);

        expectation.AssertInitialReport(initial.Result);
        expectation.AssertReplayReport(replay.Result);
        Assert.Equal(
            expectation.FinalRowCount,
            await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM large_model_managed_rows;"));
        Assert.Equal(
            expectation.FinalRowCount,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM large_model_managed_rows WHERE managed_value = 'target';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM large_model_managed_rows WHERE id >= 3000000;"));
    }

    private static string BuildPostgreSqlModelManagedInitialRows() => string.Join(
        Environment.NewLine,
        ModelManagedDataLargeExecutionContract
            .InitialRows()
            .Chunk(1000)
            .Select(batch => "INSERT INTO large_model_managed_rows (id, managed_value) VALUES "
                + string.Join(
                    ", ",
                    batch.Select(row => $"({row.Id.ToString(CultureInfo.InvariantCulture)}, '{row.Value}')"))
                + ";"));

}
