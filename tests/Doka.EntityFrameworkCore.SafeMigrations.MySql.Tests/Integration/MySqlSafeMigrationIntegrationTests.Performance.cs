namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the Performance migration contracts in an independent class collection.</summary>
public sealed partial class MySqlPerformanceIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    private const int ExpectedPerformanceTableCount = MySqlLiveBenchmarkWorkload.ManagedTableCount;
    private const int ForeignPerformanceTableCount = MySqlLiveBenchmarkWorkload.ForeignTableCount;

    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlPerformanceIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Bounds native fixture commands independently of informational performance observations.</summary>
    protected const int PerformanceFixtureCommandTimeoutSeconds = 180;
}

public sealed partial class MySqlPerformanceIntegrationTests
{
    /// <summary>
    /// Checks managed-data deferral in several mixed-stream cycles before costly stress qualification.
    /// </summary>
    [Fact]
    public async Task Analyzer_BoundedMixedOperationsRetainExactDeferredContracts()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            BuildLargeMigrationStressCatalog(),
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = LargeMigrationStressContract.Populate(
            builder,
            LargeMigrationStressDialect.MySql,
            operationCount: 128);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("bounded-mixed-migration"),
                CancellationToken.None);

        // Assert
        expectation.AssertReport(report);
    }

    /// <summary>
    /// Retains the complete ordered contract of the mixed hundred-thousand-operation catalog workload.
    /// </summary>
    [Fact]
    [Trait("Category", "LargeScale")]
    public async Task Analyzer_OneHundredThousandMixedOperationsRemainBoundedOrderedAndComplete()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            BuildLargeMigrationStressCatalog(),
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var expectation = LargeMigrationStressContract.Populate(builder, LargeMigrationStressDialect.MySql);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("large-mixed-migration"),
                CancellationToken.None);

        // Assert
        expectation.AssertReport(report);
    }
}

/// <summary>Exercises the ProcessWidePerformance migration contracts in an independent class collection.</summary>
[Collection(ProcessWideTestGroup.Name)]
public sealed partial class MySqlProcessWidePerformanceIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlProcessWidePerformanceIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Preserves typed seed rows through a large initial migration and an idempotent replay.</summary>
    [Fact]
    [Trait("Category", "LargeScale")]
    public async Task ModelManagedData_FiftyThousandMixedRowsConvergeAndReplayIdempotently()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            $"""
            CREATE TABLE `large_model_managed_rows` (`id` int NOT NULL, `managed_value`
            varchar(32) NOT NULL,
            PRIMARY KEY (`id`)) ENGINE=InnoDB;{BuildMySqlModelManagedInitialRows()}
            """,
            cancellationToken: CancellationToken.None);

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

        // Act
        var initial = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(
            operationToken => runner.AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("large-model-managed-data"),
                operationToken),
            CancellationToken.None);

        var initialExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(
            operationToken =>
                ExecuteOperationsAsync(context, builder.Operations, operationToken),
            CancellationToken.None);

        var replayExecution = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(
            operationToken =>
                ExecuteOperationsAsync(context, builder.Operations, operationToken),
            CancellationToken.None);

        var replay = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(
            operationToken => runner.AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("large-model-managed-data-replay"),
                operationToken),
            CancellationToken.None);

        ModelManagedDataLargeExecutionEvidence.Write(
            Fixture.IsMariaDb
                ? "mariadb"
                : "mysql",
            Fixture.ServerVersion.Version.ToString(),
            commands,
            initial.Measurement,
            initialExecution,
            replayExecution,
            replay.Measurement);

        // Assert
        expectation.AssertInitialReport(initial.Result);
        expectation.AssertReplayReport(replay.Result);
        Assert.Equal(
            expectation.FinalRowCount,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `large_model_managed_rows`;",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            expectation.FinalRowCount,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `large_model_managed_rows` WHERE `managed_value` = 'target';",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `large_model_managed_rows` WHERE `id` >= 3000000;",
                cancellationToken: CancellationToken.None));
    }
}

public sealed partial class MySqlPerformanceIntegrationTests
{
    /// <summary>Proves that a native missing-table rejection cannot satisfy the timeout assertion.</summary>
    /// <param name="resultPath">The first or later native result whose timeout identity must remain exact.</param>
    [Theory]
    [InlineData("first-result")]
    [InlineData("later-result")]
    public async Task Analyzer_CommandTimeoutAssertionRejectsUnrelatedNativeFailure(
        string resultPath
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM `missing_command_timeout_negative_control`;";
        var unrelated = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(CancellationToken.None));

        // Act
        var assertion = Record.Exception(() =>
        {
            if (resultPath == "first-result")
            {
                MySqlCommandTimeoutAssertions.AssertCommandTimeout(unrelated);
            }
            else
            {
                MySqlCommandTimeoutAssertions.AssertCatalogBatchTimeout(unrelated);
            }
        });

        // Assert
        Assert.Equal(MySqlErrorCode.NoSuchTable,
            Assert.IsType<MySqlException>(unrelated)
                .ErrorCode);

        Assert.IsType<XunitException>(assertion, exactMatch: false);
    }

    /// <summary>Requires the native catalog command to honor the configured provider timeout.</summary>
    [Fact]
    public async Task Analyzer_AppliesConfiguredCommandTimeoutToCatalogBatches()
    {
        // Arrange
        var cancellationToken = CancellationToken.None;
        var checkpoint = "catalog_timeout_"
            + Guid
                .NewGuid()
                .ToString("N");

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `catalog_timeout` (`id` int NOT NULL); "
            + "INSERT INTO `catalog_timeout` (`id`) VALUES (1);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(cancellationToken);
        context.Database.SetCommandTimeout(1);
        await using var checkpointCommand = context
            .Database
            .GetDbConnection()
            .CreateCommand();

        checkpointCommand.CommandText =
            "SELECT COALESCE(IS_USED_LOCK(@checkpoint) = CONNECTION_ID(), FALSE);";

        var checkpointParameter = checkpointCommand.CreateParameter();
        checkpointParameter.ParameterName = "@checkpoint";
        checkpointParameter.Value = checkpoint;
        checkpointCommand.Parameters.Add(checkpointParameter);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_catalog_timeout",
                "catalog_timeout",
                SafeMigrationSql.Binary(
                    SafeMigrationSql.Function(
                        "SLEEP",
                        SafeMigrationSql.Binary(
                            SafeMigrationSql.Function("GET_LOCK",
                                SafeMigrationSql.Literal(checkpoint),
                                SafeMigrationSql.Literal(0)),
                            SafeMigrationSqlBinaryOperator.Multiply,
                            SafeMigrationSql.Literal(5))),
                    SafeMigrationSqlBinaryOperator.Equal,
                    SafeMigrationSql.Literal(0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        Exception? exception;
        int reachedDataExpression;
        try
        {
            exception = await Record.ExceptionAsync(() => context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(
                    context,
                    builder.Operations,
                    new SafeMigrationRunOptions("catalog-timeout"),
                    cancellationToken));

            // WHY: The target expression acquires this unique session lock before SLEEP.
            // Observing its owner proves actual data-expression admission independently
            // of a JIT-sensitive private stack frame or an elapsed-time assertion.
            reachedDataExpression = Convert.ToInt32(
                await checkpointCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.ExecuteSqlAsync($"DO RELEASE_LOCK({checkpoint});", CancellationToken.None);
        }

        // Assert
        Assert.False(cancellationToken.CanBeCanceled);
        Assert.Equal(1, context.Database.GetCommandTimeout());
        Assert.Equal(1, reachedDataExpression);
        MySqlCommandTimeoutAssertions.AssertCatalogBatchTimeout(exception);
    }

    /// <summary>
    /// Requires complete identical table assessments before and after unrelated catalog noise.
    /// </summary>
    [Fact]
    public async Task FullRunner_PooledCatalogPreservesExactScopeWithForeignObjects()
    {
        // Arrange
        var templateConnectionString = await CreateDatabaseAsync(CancellationToken.None);
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

        TrackOwnedPool(connectionString);
        TrackOwnedPool(fixtureConnectionString);

        await ExecuteSqlAsync(
            fixtureConnectionString,
            MySqlLiveBenchmarkWorkload.BuildManagedTables(),
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var operations = MySqlLiveBenchmarkWorkload.CreateCatalogOperations(context.Database.ProviderName!);
        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("live-performance");

        // Act
        var clean = await runner.AnalyzeAsync(context, operations, options, CancellationToken.None);

        await ExecuteSqlAsync(
            fixtureConnectionString,
            MySqlLiveBenchmarkWorkload.BuildForeignTables(),
            cancellationToken: CancellationToken.None);

        var noisy = await runner.AnalyzeAsync(context, operations, options, CancellationToken.None);

        // Assert
        MatchingTableAssessmentAssertions.AssertComplete(
            clean.Assessments,
            ExpectedPerformanceTableCount,
            "expected_perf_");

        MatchingTableAssessmentAssertions.AssertComplete(
            noisy.Assessments,
            ExpectedPerformanceTableCount,
            "expected_perf_");

        Assert.Empty(clean.UnexpectedObjects);
        Assert.Equal(
            ForeignPerformanceTableCount,
            noisy.UnexpectedObjects.Count(static value =>
                value.ObjectKind == SafeMigrationDatabaseObjectKind.Table));

        Assert.DoesNotContain(
            noisy.UnexpectedObjects,
            static value => value.ObjectKind != SafeMigrationDatabaseObjectKind.Table);

        Assert.Equal(
            clean.Assessments.Select(static value => value.Code),
            noisy.Assessments.Select(static value => value.Code));
    }

    private static string BuildLargeMigrationStressCatalog() => $"""
        CREATE TABLE `large_migration_parent` (`id` int NOT NULL, `tenant_id` int
        NOT NULL,
        CONSTRAINT `pk_large_migration_parent` PRIMARY KEY (`id`, `tenant_id`));
        CREATE TABLE `large_migration_secondary_parent` (`id` int NOT NULL,
        `tenant_id` int NOT NULL, CONSTRAINT `pk_large_migration_secondary_parent`
        PRIMARY KEY (`id`,
        `tenant_id`)); CREATE TABLE `large_migration_target` (`id` int NOT NULL,
        `matching_value` int NULL, `repair_value` varchar(40) NULL DEFAULT 'legacy',
        `blocked_value` varchar(40) NULL, `widening_value` varchar(40) NULL, `narrowing_value`
        varchar(40) NULL,
        `blocked_narrowing_value` varchar(40) NULL, `boolean_value` bit(1) NULL,
        `indexed_value` int NOT NULL, `unique_value` int NOT NULL, `check_value`
        int NOT NULL,
        `parent_id` int NOT NULL, `parent_tenant_id` int NOT NULL, `secondary_parent_id`
        int NOT NULL,
        `secondary_parent_tenant_id` int NOT NULL, CONSTRAINT `pk_large_migration_target`
        PRIMARY KEY (`id`),
        CONSTRAINT `uq_large_migration_target_value` UNIQUE (`unique_value`, `matching_value`),
        CONSTRAINT `ck_large_migration_target_value` CHECK (`check_value` >= 0),
        INDEX `ix_large_migration_target_indexed` (`indexed_value`, `matching_value`),
        INDEX `ix_large_migration_target_parent` (`parent_id`, `parent_tenant_id`),
        INDEX `ix_large_migration_target_secondary_parent` (`secondary_parent_id`,
        `secondary_parent_tenant_id`), CONSTRAINT `fk_large_migration_target_parent`
        FOREIGN KEY (`parent_id`,
        `parent_tenant_id`) REFERENCES `large_migration_parent` (`id`, `tenant_id`));
        INSERT INTO `large_migration_parent` (`id`,
        `tenant_id`) VALUES (1, 1); INSERT INTO `large_migration_secondary_parent`
        (`id`,
        `tenant_id`) VALUES (1, 1); INSERT INTO `large_migration_target` (`id`,
        `matching_value`, `repair_value`, `blocked_value`, `widening_value`, `narrowing_value`,
        `blocked_narrowing_value`, `boolean_value`, `indexed_value`, `unique_value`,
        `check_value`, `parent_id`, `parent_tenant_id`, `secondary_parent_id`,
        `secondary_parent_tenant_id`) VALUES (1, 1, 'legacy', NULL, 'wide', 'fits',
        'too-long-value', b'1', 1, 1, 1, 1, 1, 1, 1);{BuildLargeMigrationModelManagedRows()}
        """;

    private static string BuildLargeMigrationModelManagedRows() => $"""
        INSERT INTO `large_migration_target` (`id`, `matching_value`, `repair_value`,
        `blocked_value`, `indexed_value`, `unique_value`, `check_value`, `parent_id`,
        `parent_tenant_id`, `secondary_parent_id`, `secondary_parent_tenant_id`)
        VALUES {string.Join(
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
                }))};
        """;
}

public sealed partial class MySqlProcessWidePerformanceIntegrationTests
{
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
