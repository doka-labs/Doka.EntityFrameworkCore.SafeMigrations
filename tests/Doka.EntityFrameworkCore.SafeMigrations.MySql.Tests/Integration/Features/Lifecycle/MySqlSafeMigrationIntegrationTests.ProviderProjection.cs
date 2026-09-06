namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence)]
    public async Task AlterDatabasePreservesFollowingSafeTableAndIndexPrerequisites(
        SafeMigrationScaffoldingMode mode
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var alterDatabase = new AlterDatabaseOperation();
        alterDatabase.SetAnnotation("Doka:MySql:CharSet", "utf8mb4");
        builder.Operations.Add(alterDatabase);

        _ = AddScaffoldedTable(
            builder,
            mode,
            "provider_artifacts",
            table => new
            {
                id = table.Column<int>(type: "int", nullable: false),
                public_id = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
            },
            table => table.PrimaryKey("pk_provider_artifacts", value => value.id));
        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_provider_artifacts_public_id",
            "provider_artifacts",
            "public_id",
            unique: true);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions($"provider-artifact-{mode}"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions($"provider-artifact-postflight-{mode}"),
            CancellationToken.None);
        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions($"provider-artifact-replay-{mode}"),
            CancellationToken.None);

        var providerAssessment = Assert.Single(
            preflight.Assessments,
            static assessment => !assessment.IsSafeOperation);
        var tableAssessment = Assert.Single(
            preflight.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureTable);
        var indexAssessment = Assert.Single(
            preflight.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureIndex);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal("provider_owned_not_analyzed", providerAssessment.Code);
        Assert.Equal(SafeMigrationObservedState.Missing, tableAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, tableAssessment.Action);
        Assert.Equal(SafeMigrationObservedState.Missing, indexAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, indexAssessment.Action);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.All(
            postflight.Assessments.Where(static assessment => assessment.IsSafeOperation),
            static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, replay.Status);
        Assert.All(
            replay.Assessments.Where(static assessment => assessment.IsSafeOperation),
            static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'provider_artifacts' "
                + "AND INDEX_NAME = 'ix_provider_artifacts_public_id';"));
    }
}
