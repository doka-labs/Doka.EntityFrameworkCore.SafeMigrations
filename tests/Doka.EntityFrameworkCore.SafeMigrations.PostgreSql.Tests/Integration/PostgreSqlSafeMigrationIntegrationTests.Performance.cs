namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the Performance PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlPerformanceIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    private const int ExpectedPerformanceTableCount = 100;

    private const int ForeignPerformanceTableCount = 1000;

    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlPerformanceIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

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
            LargeMigrationStressDialect.PostgreSql,
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

    /// <summary>Analyzer One Hundred Thousand Mixed Operations Remain Bounded Ordered And Complete.</summary>
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
        var expectation = LargeMigrationStressContract.Populate(builder, LargeMigrationStressDialect.PostgreSql);

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

    /// <summary>Analyzer Applies Configured Command Timeout To Catalog Batches.</summary>
    [Fact]
    public async Task Analyzer_AppliesConfiguredCommandTimeoutToCatalogBatches()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE catalog_timeout (id integer NOT NULL); "
            + "INSERT INTO catalog_timeout (id) VALUES (1);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        context.Database.SetCommandTimeout(1);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_catalog_timeout",
                "catalog_timeout",
                SafeMigrationSql.IsNull(
                    SafeMigrationSql.Function("pg_sleep", SafeMigrationSql.Literal(5)))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var failure = await Record.ExceptionAsync(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("catalog-timeout"),
                CancellationToken.None));

        // Assert
        PostgreSqlCommandTimeoutAssertions.AssertTimeout(failure);
        Assert.Equal(ConnectionState.Closed,
            context.Database.GetDbConnection()
                .State);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint WHERE conname = 'ck_catalog_timeout';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Retains exact managed and foreign catalog contracts with real pooled connections.</summary>
    [Fact]
    public async Task FullRunner_PooledCatalogPreservesExactScopeWithForeignObjects()
    {
        // Arrange
        var templateConnectionString = await CreateDatabaseAsync(CancellationToken.None);
        var connectionString = new NpgsqlConnectionStringBuilder(templateConnectionString)
        {
            Pooling = true,
            NoResetOnClose = false,
            MaxPoolSize = 4,
        }.ConnectionString;

        var fixtureConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            CommandTimeout = PerformanceFixtureCommandTimeoutSeconds,
        }.ConnectionString;

        await ExecuteSqlAsync(
            fixtureConnectionString,
            BuildPostgreSqlPerformanceTables("expected_perf_", ExpectedPerformanceTableCount, includeIndex: false),
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var operations = BuildPostgreSqlPerformanceOperations(context.Database.ProviderName!);
        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("live-performance");

        // Act
        var clean = await runner.AnalyzeAsync(context, operations, options, CancellationToken.None);

        await ExecuteSqlAsync(
            fixtureConnectionString,
            BuildPostgreSqlPerformanceTables("foreign_perf_", ForeignPerformanceTableCount, includeIndex: true),
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

    private static List<MigrationOperation> BuildPostgreSqlPerformanceOperations(
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
                    [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer")]),
                SafeMigrationTableMode.StrictDefinition,
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        // WHY: Snapshot framework operation ordering in an independently owned list.
        // ReSharper disable once UseCollectionExpression
        return builder.Operations.ToList();
    }

    private static string BuildLargeMigrationStressCatalog() => $"""
        CREATE TABLE large_migration_parent (id integer NOT NULL, tenant_id integer NOT NULL,
        CONSTRAINT pk_large_migration_parent PRIMARY KEY (id, tenant_id)); CREATE TABLE
        large_migration_secondary_parent (id integer NOT NULL,
        tenant_id integer NOT NULL, CONSTRAINT pk_large_migration_secondary_parent PRIMARY KEY (id,
        tenant_id)); CREATE TABLE large_migration_target (id integer NOT NULL,
        matching_value integer NULL, repair_value character varying(40) NULL DEFAULT 'legacy',
        blocked_value character varying(40) NULL, widening_value character varying(40) NULL,
        narrowing_value character varying(40) NULL, blocked_narrowing_value character varying(40) NULL,
        boolean_value boolean NULL, indexed_value integer NOT NULL, unique_value integer NOT NULL,
        check_value integer NOT NULL, parent_id integer NOT NULL, parent_tenant_id integer NOT NULL,
        secondary_parent_id integer NOT NULL, secondary_parent_tenant_id integer NOT NULL,
        CONSTRAINT pk_large_migration_target PRIMARY KEY (id), CONSTRAINT uq_large_migration_target_value UNIQUE
        (unique_value,
        matching_value), CONSTRAINT ck_large_migration_target_value CHECK (check_value >= 0),
        CONSTRAINT fk_large_migration_target_parent FOREIGN KEY (parent_id, parent_tenant_id) REFERENCES
        large_migration_parent (id,
        tenant_id)); CREATE INDEX ix_large_migration_target_indexed ON large_migration_target (indexed_value,
        matching_value); CREATE INDEX ix_large_migration_target_parent ON large_migration_target (parent_id,
        parent_tenant_id); CREATE INDEX ix_large_migration_target_secondary_parent ON large_migration_target
        (secondary_parent_id,
        secondary_parent_tenant_id); INSERT INTO large_migration_parent (id, tenant_id) VALUES (1,
        1); INSERT INTO large_migration_secondary_parent (id, tenant_id) VALUES (1,
        1); INSERT INTO large_migration_target (id, matching_value, repair_value,
        blocked_value, widening_value, narrowing_value, blocked_narrowing_value,
        boolean_value, indexed_value, unique_value, check_value, parent_id, parent_tenant_id,
        secondary_parent_id, secondary_parent_tenant_id) VALUES (1, 1, 'legacy',
        NULL, 'wide', 'fits', 'too-long-value', true, 1, 1, 1, 1, 1, 1, 1);{BuildLargeMigrationModelManagedRows()}
        """;

    private static string BuildLargeMigrationModelManagedRows() => $"""
        INSERT INTO large_migration_target (id, matching_value, repair_value,
        blocked_value, indexed_value, unique_value, check_value, parent_id, parent_tenant_id,
        secondary_parent_id, secondary_parent_tenant_id) VALUES {string.Join(
            ", ",
            LargeMigrationStressContract
                .ModelManagedUpdateOrdinals(LargeMigrationStressDialect.PostgreSql)
                .Select(ordinal =>
                {
                    var key = LargeMigrationStressContract
                        .ModelManagedUpdateKey(ordinal)
                        .ToString(CultureInfo.InvariantCulture);

                    var value = ordinal.ToString(CultureInfo.InvariantCulture);

                    return $"({key}, {value}, 'canonical', NULL, {value}, {key}, {value}, 1, 1, 1, 1)";
                }))};
        """;

    private static string BuildPostgreSqlPerformanceTables(
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
                var payloadColumn = includeIndex
                    ? ", \"payload\" integer NULL"
                    : string.Empty;

                var indexDdl = includeIndex
                    ? $" CREATE INDEX \"ix_{table}_payload\" ON \"{table}\" (\"payload\");"
                    : string.Empty;

                return $"CREATE TABLE \"{table}\" (\"id\" integer NOT NULL{payloadColumn});{indexDdl}";
            }));
}
