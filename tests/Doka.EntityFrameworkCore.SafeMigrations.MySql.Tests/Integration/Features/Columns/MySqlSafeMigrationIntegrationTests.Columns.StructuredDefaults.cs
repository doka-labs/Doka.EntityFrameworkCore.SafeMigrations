namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task CurrentTimestampDefault_FromEfOperationConvergesAndIsIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "timestamp_defaults",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                CreatedAt = table.Column<DateTime>(
                    name: "created_at",
                    type: "datetime(6)",
                    nullable: false,
                    defaultValueSql: "CURRENT_TIMESTAMP(6)"),
            },
            constraints: table => table.PrimaryKey("pk_timestamp_defaults", value => value.Id));
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("timestamp-default-preflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("timestamp-default-postflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Empty(postflight.UnexpectedObjects);
    }

    [Fact]
    public async Task UnboundedSqlDefault_RemainsUnsupportedWithoutCreatingTheTable()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "opaque_timestamp_defaults",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                ExpiresAt = table.Column<DateTime>(
                    name: "expires_at",
                    type: "datetime(6)",
                    nullable: false,
                    defaultValueSql: "CURRENT_TIMESTAMP(6) + INTERVAL 1 DAY"),
            });

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("opaque-timestamp-default"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal("opaque_sql_expression", assessment.Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'opaque_timestamp_defaults';"));
    }
}
