namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    [Fact]
    [Trait("Category", "LargeScale")]
    public async Task ModelManagedData_FiftyThousandMixedRowsConvergeAndReplayIdempotently()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE large_model_managed_rows ("
            + "id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL);");
        await InsertInitialModelManagedRowsAsync(connection);
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = ModelManagedDataLargeExecutionContract.Populate(
            builder,
            "INTEGER",
            "TEXT");

        var runner = context.GetService<ISafeMigrationRunner>();
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        var initial = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() => runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-large-model-managed-data"),
            CancellationToken.None));

        var initialExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var replayExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        var replay = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(() => runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-large-model-managed-data-replay"),
            CancellationToken.None));

        ModelManagedDataLargeExecutionEvidence.Write(
            "sqlite",
            connection.ServerVersion,
            commands,
            initial.Measurement,
            initialExecution,
            replayExecution,
            replay.Measurement);
        expectation.AssertInitialReport(initial.Result);
        expectation.AssertReplayReport(replay.Result);
        Assert.Equal(
            expectation.FinalRowCount,
            await ScalarIntAsync(connection, "SELECT COUNT(*) FROM large_model_managed_rows;"));
        Assert.Equal(
            expectation.FinalRowCount,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM large_model_managed_rows WHERE managed_value = 'target';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM large_model_managed_rows WHERE id >= 3000000;"));
    }

    private static async Task InsertInitialModelManagedRowsAsync(
        SqliteConnection connection
    )
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO large_model_managed_rows (id, managed_value) VALUES ($id, $value);";
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var value = command.Parameters.Add("$value", SqliteType.Text);

        foreach (var row in ModelManagedDataLargeExecutionContract.InitialRows())
        {
            id.Value = row.Id;
            value.Value = row.Value;

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await transaction.CommitAsync(CancellationToken.None);
    }
}
