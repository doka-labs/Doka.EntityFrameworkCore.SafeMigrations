namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    private const int ExpectedPerformanceTableCount = 100;
    private const int ForeignPerformanceTableCount = 1000;
    private const int PerformanceFixtureCommandTimeoutSeconds = 180;

    [Fact]
    [Trait("Category", "LargeScale")]
    public async Task Analyzer_OneHundredThousandMixedOperationsRemainBoundedOrderedAndComplete()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            BuildLargeMigrationStressCatalog());

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = LargeMigrationStressContract.Populate(builder, LargeMigrationStressDialect.MySql);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("large-mixed-migration"),
                CancellationToken.None);

        expectation.AssertReport(report);
    }

    [Fact]
    [Trait("Category", "LargeScale")]
    public async Task ModelManagedData_FiftyThousandMixedRowsConvergeAndReplayIdempotently()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `large_model_managed_rows` ("
            + "`id` int NOT NULL, `managed_value` varchar(32) NOT NULL, PRIMARY KEY (`id`)) ENGINE=InnoDB;"
            + BuildMySqlModelManagedInitialRows());

        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(PerformanceFixtureCommandTimeoutSeconds);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = ModelManagedDataLargeExecutionContract.Populate(
            builder,
            "int",
            "varchar(32)");

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
            Fixture.IsMariaDb ? "mariadb" : "mysql",
            Fixture.ServerVersion.Version.ToString(),
            commands,
            initial.Measurement,
            initialExecution,
            replayExecution,
            replay.Measurement);

        expectation.AssertInitialReport(initial.Result);
        expectation.AssertReplayReport(replay.Result);
        Assert.Equal(
            expectation.FinalRowCount,
            await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `large_model_managed_rows`;"));
        Assert.Equal(
            expectation.FinalRowCount,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `large_model_managed_rows` WHERE `managed_value` = 'target';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `large_model_managed_rows` WHERE `id` >= 3000000;"));
    }

    [Fact]
    public async Task Analyzer_AppliesConfiguredCommandTimeoutToCatalogBatches()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `catalog_timeout` (`id` int NOT NULL); "
            + "INSERT INTO `catalog_timeout` (`id`) VALUES (1);");

        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(1);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_catalog_timeout",
                "catalog_timeout",
                SafeMigrationSql.Binary(
                    SafeMigrationSql.Function("SLEEP", SafeMigrationSql.Literal(5)),
                    SafeMigrationSqlBinaryOperator.Equal,
                    SafeMigrationSql.Literal(0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        _ = await Assert.ThrowsAsync<MySqlException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("catalog-timeout"),
                CancellationToken.None));
    }

    [Fact]
    public async Task FullRunner_LiveCatalogP95RemainsBoundedWithForeignObjectsAndPooling()
    {
        var templateConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var connectionString = new MySqlConnectionStringBuilder(templateConnectionString)
        {
            Pooling = true,
            ConnectionReset = true,
            MaximumPoolSize = 4,
        }.ConnectionString;

        var fixtureConnectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            DefaultCommandTimeout = PerformanceFixtureCommandTimeoutSeconds,
        }.ConnectionString;

        await ExecuteSqlAsync(
            fixtureConnectionString,
            BuildMySqlPerformanceTables("expected_perf_", ExpectedPerformanceTableCount, includeIndex: false));

        await using var context = CreateContext(connectionString);
        var operations = BuildMySqlPerformanceOperations(context.Database.ProviderName!);
        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("live-performance");
        var clean = await LivePerformanceEvidence.MeasureAsync(() => runner.AnalyzeAsync(context, operations, options));

        await ExecuteSqlAsync(
            fixtureConnectionString,
            BuildMySqlPerformanceTables("foreign_perf_", ForeignPerformanceTableCount, includeIndex: true));

        var noisy = await LivePerformanceEvidence.MeasureAsync(() => runner.AnalyzeAsync(context, operations, options));

        LivePerformanceEvidence.Write(
            Fixture.IsMariaDb ? "mariadb" : "mysql",
            Fixture.ServerVersion.Version.ToString(),
            clean,
            noisy,
            ExpectedPerformanceTableCount,
            ForeignPerformanceTableCount);

        Assert.All(
            clean.LastReport.Assessments,
            static assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
        Assert.Empty(clean.LastReport.UnexpectedObjects);
        Assert.Equal(
            ForeignPerformanceTableCount,
            noisy.LastReport.UnexpectedObjects.Count(static value =>
                value.ObjectKind == SafeMigrationDatabaseObjectKind.Table));
        Assert.DoesNotContain(
            noisy.LastReport.UnexpectedObjects,
            static value => value.ObjectKind != SafeMigrationDatabaseObjectKind.Table);
        Assert.Equal(
            clean.LastReport.Assessments.Select(static value => value.Code),
            noisy.LastReport.Assessments.Select(static value => value.Code));
        Assert.True(
            noisy.P95Milliseconds <= (clean.P95Milliseconds * 2d) + 250d,
            $"Noisy p95 {noisy.P95Milliseconds:F3} ms exceeded clean p95 {clean.P95Milliseconds:F3} ms.");
    }

    private static List<MigrationOperation> BuildMySqlPerformanceOperations(
        string providerName
    )
    {
        var builder = new MigrationBuilder(providerName);
        for (var index = 0; index < ExpectedPerformanceTableCount; index++)
        {
            var table = $"expected_perf_{index:D4}";
            _ = builder.EnsureTable(
                new ExpectedTableDefinition(
                    table,
                    [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")]),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        return builder.Operations.ToList();
    }

    private static string BuildLargeMigrationStressCatalog() =>
        "CREATE TABLE `large_migration_parent` ("
        + "`id` int NOT NULL, `tenant_id` int NOT NULL, "
        + "CONSTRAINT `pk_large_migration_parent` PRIMARY KEY (`id`, `tenant_id`)); "
        + "CREATE TABLE `large_migration_secondary_parent` ("
        + "`id` int NOT NULL, `tenant_id` int NOT NULL, "
        + "CONSTRAINT `pk_large_migration_secondary_parent` PRIMARY KEY (`id`, `tenant_id`)); "
        + "CREATE TABLE `large_migration_target` ("
        + "`id` int NOT NULL, `matching_value` int NULL, "
        + "`repair_value` varchar(40) NULL DEFAULT 'legacy', "
        + "`blocked_value` varchar(40) NULL, `indexed_value` int NOT NULL, "
        + "`unique_value` int NOT NULL, `check_value` int NOT NULL, "
        + "`parent_id` int NOT NULL, `parent_tenant_id` int NOT NULL, "
        + "`secondary_parent_id` int NOT NULL, `secondary_parent_tenant_id` int NOT NULL, "
        + "CONSTRAINT `pk_large_migration_target` PRIMARY KEY (`id`), "
        + "CONSTRAINT `uq_large_migration_target_value` UNIQUE (`unique_value`, `matching_value`), "
        + "CONSTRAINT `ck_large_migration_target_value` CHECK (`check_value` >= 0), "
        + "INDEX `ix_large_migration_target_indexed` (`indexed_value`, `matching_value`), "
        + "INDEX `ix_large_migration_target_parent` (`parent_id`, `parent_tenant_id`), "
        + "INDEX `ix_large_migration_target_secondary_parent` "
        + "(`secondary_parent_id`, `secondary_parent_tenant_id`), "
        + "CONSTRAINT `fk_large_migration_target_parent` "
        + "FOREIGN KEY (`parent_id`, `parent_tenant_id`) "
        + "REFERENCES `large_migration_parent` (`id`, `tenant_id`)); "
        + "INSERT INTO `large_migration_parent` (`id`, `tenant_id`) VALUES (1, 1); "
        + "INSERT INTO `large_migration_secondary_parent` (`id`, `tenant_id`) VALUES (1, 1); "
        + "INSERT INTO `large_migration_target` ("
        + "`id`, `matching_value`, `repair_value`, `blocked_value`, `indexed_value`, "
        + "`unique_value`, `check_value`, `parent_id`, `parent_tenant_id`, "
        + "`secondary_parent_id`, `secondary_parent_tenant_id`) "
        + "VALUES (1, 1, 'legacy', NULL, 1, 1, 1, 1, 1, 1, 1);"
        + BuildLargeMigrationModelManagedRows();

    private static string BuildLargeMigrationModelManagedRows() =>
        "INSERT INTO `large_migration_target` ("
        + "`id`, `matching_value`, `repair_value`, `blocked_value`, `indexed_value`, "
        + "`unique_value`, `check_value`, `parent_id`, `parent_tenant_id`, "
        + "`secondary_parent_id`, `secondary_parent_tenant_id`) VALUES "
        + string.Join(
            ", ",
            LargeMigrationStressContract
                .ModelManagedUpdateOrdinals(LargeMigrationStressDialect.MySql)
                .Select(ordinal =>
                {
                    var key = LargeMigrationStressContract
                        .ModelManagedUpdateKey(ordinal)
                        .ToString(CultureInfo.InvariantCulture);

                    var value = ordinal.ToString(CultureInfo.InvariantCulture);

                    return $"({key}, {value}, 'canonical', NULL, {value}, {key}, {value}, 1, 1, 1, 1)";
                }))
        + ";";

    private static string BuildMySqlPerformanceTables(
        string prefix,
        int count,
        bool includeIndex
    ) => string.Join(
        Environment.NewLine,
        Enumerable
            .Range(0, count)
            .Select(index =>
            {
                var table = $"{prefix}{index:D4}";
                var indexDdl = includeIndex ? ", INDEX `ix_payload` (`payload`)" : string.Empty;
                var payloadColumn = includeIndex ? ", `payload` int NULL" : string.Empty;

                return $"CREATE TABLE `{table}` (`id` int NOT NULL{payloadColumn}{indexDdl});";
            }));

    private static string BuildMySqlModelManagedInitialRows() => string.Join(
        Environment.NewLine,
        ModelManagedDataLargeExecutionContract
            .InitialRows()
            .Chunk(1000)
            .Select(batch => "INSERT INTO `large_model_managed_rows` (`id`, `managed_value`) VALUES "
                + string.Join(
                    ", ",
                    batch.Select(row => $"({row.Id.ToString(CultureInfo.InvariantCulture)}, '{row.Value}')"))
                + ";"));
}
