namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Exercises performance with independently owned SQLite state.</summary>
[Collection(ProcessWideTestGroup.Name)]
public sealed class SqliteSafeMigrationPerformanceIntegrationTests : SqliteSafeMigrationCatalogIntegrationTests
{
    [Fact]
    [Trait("Category", "LargeScale")]
    public async Task ModelManagedData_FiftyThousandMixedRowsConvergeAndReplayIdempotently()
    {
        await using var connection = await OpenConnectionAsync(cancellationToken: CancellationToken.None);
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE large_model_managed_rows ("
            + "id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL);",
            cancellationToken: CancellationToken.None);

        await InsertInitialModelManagedRowsAsync(connection, cancellationToken: CancellationToken.None);
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

        var initial = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(
            token => runner.AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-large-model-managed-data"),
                token),
            CancellationToken.None);

        var initialExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(
            token =>
                ExecuteOperationsAsync(context, builder.Operations, token),
            CancellationToken.None);

        var replayExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(
            token =>
                ExecuteOperationsAsync(context, builder.Operations, token),
            CancellationToken.None);

        var replay = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(
            token => runner.AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-large-model-managed-data-replay"),
                token),
            CancellationToken.None);

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
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM large_model_managed_rows;",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            expectation.FinalRowCount,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM large_model_managed_rows WHERE managed_value = 'target';",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM large_model_managed_rows WHERE id >= 3000000;",
                cancellationToken: CancellationToken.None));
    }

    private static async Task InsertInitialModelManagedRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

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

            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
