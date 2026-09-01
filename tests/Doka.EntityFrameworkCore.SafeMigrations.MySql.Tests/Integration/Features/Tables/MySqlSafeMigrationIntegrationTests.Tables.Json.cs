namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task JsonTable_ConvergesAndTreatsOnlyTheProviderValidationCheckAsOwned()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "json_documents",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Payload = table.Column<string>(type: "json", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_json_documents", value => value.Id));
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("json-table-preflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("json-table-postflight"),
            CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `json_documents` (`id`, `Payload`) VALUES (1, '{\"valid\":true}');");
        var invalidJson = await Assert.ThrowsAsync<MySqlException>(() => ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `json_documents` (`id`, `Payload`) VALUES (2, 'not-json');"));

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Empty(postflight.UnexpectedObjects);
        Assert.NotEmpty(invalidJson.Message);
    }

    [Fact]
    public async Task JsonTable_DoesNotHideAnIndependentUserCheckConstraint()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "json_check_drift",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Payload = table.Column<string>(type: "json", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_json_check_drift", value => value.Id));

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "ALTER TABLE `json_check_drift` ADD CONSTRAINT `ck_json_check_drift_id` CHECK (`Id` > 0);");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("json-user-check-drift"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.CheckConstraint
                && value.Name == "ck_json_check_drift_id");
    }
}
