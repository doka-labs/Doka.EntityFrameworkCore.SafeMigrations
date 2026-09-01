namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task ProviderGeneratedTemporalRowVersionAndJsonConstraint_AreOwnedAndIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.CreateTableIfNotExists(
            name: "provider_artifacts",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.AutoIncrement),
                PublicId = table.Column<Guid>(type: "char(36)", fixedLength: true, maxLength: 36, nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.None),
                DefaultCorrelationId = table.Column<Guid>(
                        type: "char(36)",
                        fixedLength: true,
                        maxLength: 36,
                        nullable: false,
                        defaultValue: new Guid("27caab1e-a588-4dcc-bace-fef7cf47e1fd"))
                    .Annotation("Doka:MySql:GuidFormat", DokaMySqlGuidFormat.Char36)
                    .Annotation("Doka:MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.ClientGuid),
                State = table.Column<string>(
                    type: "varchar(24)",
                    maxLength: 24,
                    nullable: false,
                    defaultValue: "Active"),
                IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: true),
                Threshold = table.Column<decimal>(
                    type: "decimal(10,4)",
                    precision: 10,
                    scale: 4,
                    nullable: false,
                    defaultValue: 12.3750m),
                LiteralText = table.Column<string>(
                    type: "varchar(120)",
                    maxLength: 120,
                    nullable: false,
                    defaultValue: "Doka's default\\path"),
                BusinessDate = table.Column<DateOnly>(
                    type: "date",
                    nullable: false,
                    defaultValue: new DateOnly(2026, 8, 31)),
                CutoffTime = table.Column<TimeOnly>(
                    type: "time(6)",
                    precision: 6,
                    nullable: false,
                    defaultValue: new TimeOnly(18, 45, 30, 123).Add(TimeSpan.FromTicks(4560))),
                CreatedAt = table.Column<DateTime>(
                    type: "datetime(6)",
                    precision: 6,
                    nullable: false,
                    defaultValueSql: "CURRENT_TIMESTAMP(6)"),
                Version = table.Column<byte[]>(type: "timestamp(6)", rowVersion: true, nullable: false),
                Payload = table.Column<string>(type: "json", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_provider_artifacts", value => value.Id));

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("provider-row-version-json");

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `provider_artifacts` (`PublicId`, `Payload`) "
            + "VALUES ('94b13fc0-e6fb-45ba-8b19-f241359d7258', '{\"valid\":true}');");

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);
        var invalidJson = await Assert.ThrowsAsync<MySqlException>(() => ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `provider_artifacts` (`PublicId`, `Payload`) "
            + "VALUES ('ad0a2937-5455-4bb6-a793-32e866112fde', 'not-json');"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Empty(postflight.UnexpectedObjects);
        Assert.NotEmpty(invalidJson.Message);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'provider_artifacts' "
                + "AND COLUMN_NAME = 'Version' "
                + "AND LOWER(COLUMN_DEFAULT) IN ('current_timestamp(6)', 'now(6)') "
                + "AND LOWER(EXTRA) LIKE '%on update current_timestamp(6)%';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `provider_artifacts` WHERE `Version` IS NOT NULL;"));
    }

    [Fact]
    public async Task TemporalRowVersionWithoutOnUpdate_IsRejectedAsDifferent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `row_version_drift` ("
            + "`Id` int NOT NULL, "
            + "`Version` timestamp(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6), "
            + "PRIMARY KEY (`Id`));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.CreateTableIfNotExists(
            name: "row_version_drift",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Version = table.Column<byte[]>(type: "timestamp(6)", rowVersion: true, nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_row_version_drift", value => value.Id));

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("row-version-without-update"),
                CancellationToken.None);
        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'row_version_drift' "
                + "AND COLUMN_NAME = 'Version' AND LOWER(EXTRA) LIKE '%on update%';"));
    }
}
