namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, DokaMySqlGuidFormat.Char36)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, DokaMySqlGuidFormat.Binary16)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, DokaMySqlGuidFormat.Char36)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, DokaMySqlGuidFormat.Binary16)]
    public async Task ModelManagedTypedValuesConvergeUpdateDeleteAndReplay(
        SafeMigrationScaffoldingMode mode,
        DokaMySqlGuidFormat guidFormat
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var storeType = GuidStoreType(guidFormat);
        var maximumLength = guidFormat == DokaMySqlGuidFormat.Binary16 ? 16 : 36;
        var identifier = Guid.Parse("1458f03f-9acb-4902-bca8-280b613a7c92");

        var ensure = new MigrationBuilder(context.Database.ProviderName!);
        _ = AddScaffoldedTable(
            ensure,
            mode,
            "typed_model_rows",
            table => new
            {
                id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maximumLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                symbol = table.Column<char>(type: "char(1)", maxLength: 1, fixedLength: true, nullable: false),
                state = table.Column<int>(type: "int", nullable: false),
            },
            table => table.PrimaryKey("pk_typed_model_rows", value => value.id));
        _ = ensure.EnsureModelManagedDataFromModel(
            "typed_model_rows",
            ["id"],
            [storeType],
            ["id", "symbol", "state"],
            [storeType, "char(1)", "int"],
            new object?[,] { { identifier, 'A', SafeMigrationScaffoldingWorkItemKind.Task, }, });

        var runner = context.GetService<ISafeMigrationRunner>();
        var ensurePreflight = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions($"typed-model-ensure-{mode}-{guidFormat}"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, ensure.Operations, CancellationToken.None);

        var ensureReplay = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions($"typed-model-ensure-replay-{mode}-{guidFormat}"),
            CancellationToken.None);

        var update = new MigrationBuilder(context.Database.ProviderName!);
        _ = update.UpdateModelManagedDataFromModel(
            "typed_model_rows",
            ["id"],
            [storeType],
            new object?[,] { { identifier, }, },
            ["symbol", "state"],
            ["char(1)", "int"],
            new object?[,] { { 'A', SafeMigrationScaffoldingWorkItemKind.Task, }, },
            new object?[,] { { 'B', SafeMigrationScaffoldingWorkItemKind.External, }, });

        var updatePreflight = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions($"typed-model-update-{mode}-{guidFormat}"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, update.Operations, CancellationToken.None);

        var updateReplay = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions($"typed-model-update-replay-{mode}-{guidFormat}"),
            CancellationToken.None);

        var delete = new MigrationBuilder(context.Database.ProviderName!);
        _ = delete.DeleteModelManagedDataFromModel(
            "typed_model_rows",
            ["id"],
            [storeType],
            new object?[,] { { identifier, }, },
            ["id", "symbol", "state"],
            [storeType, "char(1)", "int"],
            new object?[,] { { identifier, 'B', SafeMigrationScaffoldingWorkItemKind.External, }, });

        var deletePreflight = await runner.AnalyzeAsync(
            context,
            delete.Operations,
            new SafeMigrationRunOptions($"typed-model-delete-{mode}-{guidFormat}"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, delete.Operations, CancellationToken.None);

        var deleteReplay = await runner.AnalyzeAsync(
            context,
            delete.Operations,
            new SafeMigrationRunOptions($"typed-model-delete-replay-{mode}-{guidFormat}"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, ensurePreflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, ensureReplay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, ensureReplay.Assessments[^1].Action);
        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(updatePreflight.Assessments).ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(updateReplay.Assessments).ObservedState);
        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(deletePreflight.Assessments).ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(deleteReplay.Assessments).ObservedState);
        Assert.Equal(0, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `typed_model_rows`;"));
    }
}
