namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task MatchingDerivedContext_UsesTheCanonicalSnapshotAndMigrationHistory()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = new MatchingDerivedContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();

        var emptyReport = await runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("matching-derived"));

        Assert.Equal(SafeMigrationReportStatus.NoOperations, emptyReport.Status);

        await context.Database.MigrateAsync();

        var pendingReport = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("matching-derived"));

        Assert.Equal(SafeMigrationReportStatus.NoOperations, pendingReport.Status);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM \"__CoreDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';"));
    }

    [Fact]
    public async Task Postflight_BlocksWhenTheExpectedTargetStateWasNotReached()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "postflight_missing",
            table => new { Id = table.Column<int>(type: "integer", nullable: false) });

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
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'postflight_missing';"));
    }

    [Fact]
    public async Task Runner_HonorsCancellationBeforeCatalogAccess()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                [],
                new SafeMigrationRunOptions("cancelled"),
                cancellation.Token));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() "
                + "AND table_name = '__CoreDbContextMigrationsHistory';"));
    }

    [Fact]
    public async Task Runner_CancellationDuringCatalogAccessClosesItsOwnedConnection()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var analyzer = new BlockingProviderAnalyzer("npgsql_postgresql");
        var runner = new SafeMigrationRunner(analyzer);
        using var cancellation = new CancellationTokenSource();

        var run = runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("cancelled-during-catalog"),
            cancellation.Token);

        await analyzer.Started.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task PendingPreflight_RejectsUnknownAndBackwardTargetsAndReportsCompletedHistory()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();

        await Assert.ThrowsAsync<ArgumentException>(() => runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("unknown-target", "209901010000_Unknown")));

        await context.Database.MigrateAsync();

        var completed = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("completed"));

        Assert.Equal(SafeMigrationReportStatus.NoOperations, completed.Status);
        Assert.Empty(completed.Assessments);

        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO \"__CoreDbContextMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") "
            + "VALUES ('209901010000_Future', '10.0.0');");

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions(
                "backward-target",
                CoreConvergenceMigration.MigrationIdentifier)));
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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE rename_source (source_column integer NULL, target_column integer NULL); "
            + "CREATE TABLE rename_target (id integer NOT NULL); "
            + "CREATE INDEX rename_source_index ON rename_source (source_column); "
            + "CREATE INDEX rename_target_index ON rename_source (target_column);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "rename_target");
        builder.RenameColumnIfExists("source_column", "rename_source", "target_column");
        builder.RenameIndexIfExists("rename_source_index", "rename_source", "rename_target_index");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("rename-collisions"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(report.Assessments, assessment =>
        {
            Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        });

        foreach (var operation in builder.Operations)
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context, [operation]));

            Assert.Equal("P1001", exception.SqlState);
        }

        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() "
                + "AND table_name IN ('rename_source', 'rename_target');"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'rename_source' "
                + "AND column_name IN ('source_column', 'target_column');"));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class "
                + "WHERE relkind = 'i' AND relname IN ('rename_source_index', 'rename_target_index');"));
    }

    [Fact]
    public async Task UnexpectedObjectInventory_CoversEveryConstraintFamilyWithoutMutation()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE inventory_parent (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE inventory_constraints ("
            + "id integer NOT NULL, parent_id integer NULL, code text NULL, quantity integer NOT NULL, "
            + "CONSTRAINT pk_inventory_constraints PRIMARY KEY (id), "
            + "CONSTRAINT uq_inventory_code UNIQUE (code), "
            + "CONSTRAINT ck_inventory_quantity CHECK (quantity >= 0), "
            + "CONSTRAINT fk_inventory_parent FOREIGN KEY (parent_id) REFERENCES inventory_parent (id));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "inventory_constraints",
            table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                ParentId = table.Column<int>(name: "parent_id", type: "integer", nullable: true),
                Code = table.Column<string>(type: "text", nullable: true),
                Quantity = table.Column<int>(type: "integer", nullable: false),
            },
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("unexpected-constraints"));

        Assert.Contains(report.UnexpectedObjects, value => value.ObjectKind == SafeMigrationDatabaseObjectKind.PrimaryKey
            && value.Name == "pk_inventory_constraints");
        Assert.Contains(report.UnexpectedObjects, value => value.ObjectKind == SafeMigrationDatabaseObjectKind.UniqueConstraint
            && value.Name == "uq_inventory_code");
        Assert.Contains(report.UnexpectedObjects, value => value.ObjectKind == SafeMigrationDatabaseObjectKind.CheckConstraint
            && value.Name == "ck_inventory_quantity");
        Assert.Contains(report.UnexpectedObjects, value => value.ObjectKind == SafeMigrationDatabaseObjectKind.ForeignKey
            && value.Name == "fk_inventory_parent");
        Assert.Equal(
            4,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint co "
                + "JOIN pg_catalog.pg_class c ON c.oid = co.conrelid "
                + "WHERE c.relname = 'inventory_constraints' "
                + "AND co.conname IN ('pk_inventory_constraints', 'uq_inventory_code', "
                + "'ck_inventory_quantity', 'fk_inventory_parent');"));
    }

    [Fact]
    public async Task FourConcurrentMigrators_ProduceOneHistoryRowAndOneCanonicalSchema()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        var contexts = Enumerable
            .Range(0, 4)
            .Select(_ => CreateContext(connectionString))
            .ToArray();

        try
        {
            await Task.WhenAll(contexts.Select(context => context.Database.MigrateAsync()));
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
                $"SELECT COUNT(*) FROM \"__CoreDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'pipeline_state' "
                + "AND column_name = 'payload';"));
    }

    private sealed class MatchingDerivedContext(string connectionString)
        : SafeMigrationDbContext(connectionString);

    private sealed class BlockingProviderAnalyzer(
        string providerId
    ) : ISafeMigrationProviderAnalyzer
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId { get; } = providerId;

        public Task Started => _started.Task;

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
