namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task CompositeModelManagedKeysConvergeAndReplayInOperationOrder()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `model_composite_roles` ("
            + "`tenant_id` int NOT NULL, `id` int NOT NULL, `name` varchar(64) NOT NULL, "
            + "PRIMARY KEY (`tenant_id`, `id`)) ENGINE=InnoDB;"
            + "INSERT INTO `model_composite_roles` (`tenant_id`, `id`, `name`) VALUES "
            + "(1, 1, 'source'), (2, 1, 'removed');");

        await using var context = CreateContext(connectionString);
        var builder = CompositeModelManagedOperations(context.Database.ProviderName!, "int", "varchar(64)");
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("model-data-composite-keys"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("model-data-composite-keys-replay"),
            CancellationToken.None);

        Assert.Equal(
            [
                SafeMigrationObservedState.Missing,
                SafeMigrationObservedState.TransitionReady,
                SafeMigrationObservedState.TransitionReady,
            ],
            preflight.Assessments.Select(static assessment => assessment.ObservedState));
        Assert.All(replay.Assessments, static assessment =>
            Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `model_composite_roles` WHERE `name` = 'target';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `model_composite_roles` WHERE `tenant_id` = 2 AND `id` = 1;"));
    }

    private static MigrationBuilder CompositeModelManagedOperations(
        string providerName,
        string integerStoreType,
        string textStoreType
    )
    {
        var builder = new MigrationBuilder(providerName);
        _ = builder.EnsureModelManagedDataFromModel(
            "model_composite_roles",
            ["tenant_id", "id"],
            [integerStoreType, integerStoreType],
            ["tenant_id", "id", "name"],
            [integerStoreType, integerStoreType, textStoreType],
            new object?[,] { { 1, 2, "target" } });
        _ = builder.UpdateModelManagedDataFromModel(
            "model_composite_roles",
            ["tenant_id", "id"],
            [integerStoreType, integerStoreType],
            new object?[,] { { 1, 1 } },
            ["name"],
            [textStoreType],
            new object?[,] { { "source" } },
            new object?[,] { { "target" } });
        _ = builder.DeleteModelManagedDataFromModel(
            "model_composite_roles",
            ["tenant_id", "id"],
            [integerStoreType, integerStoreType],
            new object?[,] { { 2, 1 } },
            ["tenant_id", "id", "name"],
            [integerStoreType, integerStoreType, textStoreType],
            new object?[,] { { 2, 1, "removed" } });

        return builder;
    }
}
