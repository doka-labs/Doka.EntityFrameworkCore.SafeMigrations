namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task MissingParentTable_IsReportedAsPrerequisiteFailureAcrossChildFamilies()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "missing_parent",
            new ExpectedColumnDefinition("value", typeof(int), true, "int"),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_missing_parent_value",
                "missing_parent",
                [new ExpectedIndexKeyDefinition(column: "value")]),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_missing_parent_value",
                "missing_parent",
                SqlColumnAndInt("value", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("missing-parent"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, assessment.Action);
                Assert.Equal("prerequisite_missing", assessment.Code);
            });

        var exception = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, [builder.Operations[0]]));

        Assert.Equal(1062, exception.Number);
        Assert.Contains("doka_sm_prerequisite_missing", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'missing_parent';"));
    }

    [Fact]
    public async Task Analyzer_ProcessesMoreThanOneBoundedClassificationChunkInGlobalOrder()
    {
        const int operationCount = 513;
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `chunked_analysis` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var ordinal = 0; ordinal < operationCount; ordinal++)
        {
            builder.EnsureColumn(
                "chunked_analysis",
                new ExpectedColumnDefinition($"value_{ordinal}", typeof(int), true, "int"),
                SafeMigrationPolicy.ThrowIfDifferent);
        }

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("chunked-analysis"));

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(operationCount, report.Assessments.Count);
        Assert.Equal(Enumerable.Range(0, operationCount), report.Assessments.Select(static value => value.Ordinal));
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
            });
    }

    [Fact]
    public async Task Analyzer_RejectsSingleOperationAboveTheUtf8PayloadLimitBeforeClassificationExecution()
    {
        const int maximumUtf8PayloadBytes = 4 * 1024 * 1024;
        var oversizedComment = new string('\u00e4', (maximumUtf8PayloadBytes / 2) + 1);
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `oversized_analysis` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "oversized_analysis",
            new ExpectedColumnDefinition("payload", typeof(string), true, "longtext", comment: oversizedComment),
            SafeMigrationPolicy.ThrowIfDifferent);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("oversized-analysis")));

        Assert.Contains("operation 0 exceeds a bounded query limit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("utf8_payload_bytes=", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'oversized_analysis' "
                + "AND COLUMN_NAME = 'payload';"));
    }

    [Fact]
    public async Task OpaqueSqlExpression_IsUnsupportedWithStableReasonBeforeTargetDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `opaque_expression` (`value` int NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_opaque_expression", "opaque_expression", "value >= 0");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("opaque-expression"));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("opaque_sql_expression", assessment.Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'ck_opaque_expression';"));
    }

    [Fact]
    public async Task MatchingDerivedContext_UsesTheCanonicalSnapshotAndMigrationHistory()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = new MatchingDerivedContext(connectionString, Fixture.ServerVersion);
        var runner = context.GetService<ISafeMigrationRunner>();

        var emptyReport = await runner.AnalyzeAsync(context, [], new SafeMigrationRunOptions("matching-derived"));

        Assert.Equal(SafeMigrationReportStatus.NoOperations, emptyReport.Status);

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        var pendingReport = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("matching-derived"));

        Assert.Equal(SafeMigrationReportStatus.NoOperations, pendingReport.Status);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `__CoreDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';"));
    }

    [Fact]
    public async Task Postflight_BlocksWhenTheExpectedTargetStateWasNotReached()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "postflight_missing",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .VerifyAsync(context, builder.Operations, new SafeMigrationRunOptions("postflight-negative"));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(assessment.PostconditionSatisfied);
        Assert.Equal("postcondition_failed", assessment.Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'postflight_missing';"));
    }

    [Fact]
    public async Task Runner_HonorsCancellationBeforeCatalogAccess()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("cancelled"), cancellation.Token));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = '__CoreDbContextMigrationsHistory';"));
    }

    [Fact]
    public async Task Runner_CancellationDuringCatalogAccessClosesItsOwnedConnection()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var analyzer = new BlockingProviderAnalyzer("doka_mysql");
        var runner = new SafeMigrationRunner(analyzer);
        using var cancellation = new CancellationTokenSource();

        var run = runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("cancelled-during-catalog"),
            cancellation.Token);

        await analyzer.Started.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
        Assert.Equal(
            System.Data.ConnectionState.Closed,
            context.Database.GetDbConnection()
                .State);
    }

    [Fact]
    public async Task PendingPreflight_RejectsUnknownAndBackwardTargetsAndReportsCompletedHistory()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();

        await Assert.ThrowsAsync<ArgumentException>(() => runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("unknown-target", "209901010000_Unknown")));

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        var completed = await runner.AnalyzePendingMigrationsAsync(context, new SafeMigrationRunOptions("completed"));

        Assert.Equal(SafeMigrationReportStatus.NoOperations, completed.Status);
        Assert.Empty(completed.Assessments);

        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `__CoreDbContextMigrationsHistory` (`MigrationId`, `ProductVersion`) "
            + "VALUES ('209901010000_Future', '10.0.0');");

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("backward-target", CoreConvergenceMigration.MigrationIdentifier)));
    }

    [Fact]
    public void ConvergenceBaseline_DownFailsBeforeProducingDestructiveOperations()
    {
        var migration = new CoreConvergenceMigration();

        var exception = Assert.Throws<NotSupportedException>(() => migration.DownOperations);

        Assert.Contains("forward-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RenameTargetCollisions_AreRejectedWithoutMutatingEitherObject()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `rename_source` (`source_column` int NULL, `target_column` int NULL); "
            + "CREATE TABLE `rename_target` (`id` int NOT NULL); "
            + "CREATE INDEX `rename_source_index` ON `rename_source` (`source_column`); "
            + "CREATE INDEX `rename_target_index` ON `rename_source` (`target_column`);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "rename_target");
        builder.RenameColumnIfExists("source_column", "rename_source", "target_column");
        builder.RenameIndexIfExists("rename_source_index", "rename_source", "rename_target_index");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("rename-collisions"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        foreach (var operation in builder.Operations)
        {
            var exception =
                await Assert.ThrowsAsync<MySqlException>(() => ExecuteOperationsAsync(context, [operation]));

            Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('rename_source', 'rename_target');"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'rename_source' "
                + "AND COLUMN_NAME IN ('source_column', 'target_column');"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'rename_source' "
                + "AND INDEX_NAME IN ('rename_source_index', 'rename_target_index');"));
    }

    [Fact]
    public async Task UnexpectedObjectInventory_CoversEveryConstraintFamilyWithoutMutation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `inventory_parent` (`id` int NOT NULL, PRIMARY KEY (`id`)); "
            + "CREATE TABLE `inventory_constraints` ("
            + "`id` int NOT NULL, `parent_id` int NULL, `code` varchar(30) NULL, `quantity` int NOT NULL, "
            + "PRIMARY KEY (`id`), "
            + "CONSTRAINT `uq_inventory_code` UNIQUE (`code`), "
            + "CONSTRAINT `ck_inventory_quantity` CHECK (`quantity` >= 0), "
            + "CONSTRAINT `fk_inventory_parent` FOREIGN KEY (`parent_id`) REFERENCES `inventory_parent` (`id`));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "inventory_constraints",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                ParentId = table.Column<int>(name: "parent_id", type: "int", nullable: true),
                Code = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                Quantity = table.Column<int>(type: "int", nullable: false),
            },
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unexpected-constraints"));

        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.PrimaryKey);
        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.UniqueConstraint
                && value.Name == "uq_inventory_code");
        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.CheckConstraint
                && value.Name == "ck_inventory_quantity");
        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.ForeignKey
                && value.Name == "fk_inventory_parent");
        Assert.Equal(
            4,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'inventory_constraints';"));
    }

    [Fact]
    public async Task FourConcurrentMigrators_ProduceOneHistoryRowAndOneCanonicalSchema()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var contexts = Enumerable
            .Range(0, 4)
            .Select(_ => CreateContext(connectionString))
            .ToArray();

        try
        {
            await Task.WhenAll(contexts.Select(context => context.Database.MigrateAsync(cancellationToken: CancellationToken.None)));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `__CoreDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state' "
                + "AND COLUMN_NAME = 'payload';"));
    }

    private sealed class MatchingDerivedContext(
        string connectionString,
        MySqlServerVersion serverVersion
    ) : SafeMigrationDbContext(connectionString, serverVersion);

    private sealed class BlockingProviderAnalyzer(string providerId) : ISafeMigrationProviderAnalyzer
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId { get; } = providerId;

        public Task Started => _started.Task;

        public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
            DbContext context,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Analysis scope acquisition must not be reached.");

        public async Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
            DbContext context,
            CancellationToken cancellationToken = default
        )
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            throw new InvalidOperationException("The blocking analyzer completed without cancellation.");
        }

        public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
            DbContext context,
            IReadOnlyList<SafeMigrationOperation> operations,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Operation analysis must not be reached.");

        public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
            DbContext context,
            IReadOnlyList<MigrationOperation> operations,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Unexpected-object analysis must not be reached.");
    }
}
