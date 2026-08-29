namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task MissingParentTable_IsReportedAsPrerequisiteFailureAcrossChildFamilies()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "missing_parent",
            new ExpectedColumnDefinition("value", typeof(int), true, "integer"),
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

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, [builder.Operations[0]]));

        Assert.Equal("P1004", exception.SqlState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'missing_parent';"));
    }

    [Fact]
    public async Task MissingReferencedColumns_AreClassifiedBeforeAnyDataProbe()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE column_prerequisite_child (id integer NOT NULL); "
            + "CREATE TABLE column_prerequisite_parent (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ux_column_prerequisite_code",
                "column_prerequisite_child",
                [new ExpectedIndexKeyDefinition(column: "missing_code")],
                unique: true),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddPrimaryKeyIfNotExists(
            "pk_column_prerequisite_child",
            "column_prerequisite_child",
            ["missing_primary"]);
        builder.AddUniqueConstraintIfNotExists(
            "uq_column_prerequisite_child",
            "column_prerequisite_child",
            ["missing_unique"]);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_column_prerequisite_child",
                "column_prerequisite_child",
                SqlColumnAndInt("missing_check", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddForeignKeyIfNotExists(
            "fk_column_prerequisite_child",
            "column_prerequisite_child",
            ["missing_foreign"],
            "column_prerequisite_parent",
            ["missing_principal"]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("missing-column-prerequisites"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(5, report.Assessments.Count);
        Assert.All(
            report.Assessments,
            static assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, assessment.Action);
            });
    }

    [Fact]
    public async Task LegacyConvergence_AddsNullableColumnBeforeUniqueIndexAndRemainsIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE legacy_unique_users (id integer NOT NULL); "
            + "INSERT INTO legacy_unique_users (id) VALUES (1), (2);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "legacy_unique_users",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("email", typeof(string), true, "text"),
            ]);

        var index = new ExpectedIndexDefinition(
            "ux_legacy_unique_users_email",
            "legacy_unique_users",
            [new ExpectedIndexKeyDefinition(column: "email")],
            unique: true);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition, [index]);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("legacy-unique-users-preflight"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[^1].ObservedState);
        Assert.Equal("projected_missing", preflight.Assessments[^1].Code);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("legacy-unique-users-postflight"));

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_indexes "
                + "WHERE schemaname = current_schema() AND tablename = 'legacy_unique_users' "
                + "AND indexname = 'ux_legacy_unique_users_email';"));
    }

    [Fact]
    public async Task LegacyConvergence_DoesNotProjectUniqueSafetyThroughNonNullDefault()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE legacy_default_users (id integer NOT NULL); "
            + "INSERT INTO legacy_default_users (id) VALUES (1), (2);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "legacy_default_users",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition(
                    "tenant_id",
                    typeof(int),
                    true,
                    "integer",
                    defaultValue: SafeMigrationDefaultValue.Literal(0)),
            ]);

        var index = new ExpectedIndexDefinition(
            "ux_legacy_default_users_tenant",
            "legacy_default_users",
            [new ExpectedIndexKeyDefinition(column: "tenant_id")],
            unique: true);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition, [index]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("legacy-default-users"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[^1].ObservedState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'legacy_default_users' "
                + "AND column_name = 'tenant_id';"));
    }

    [Fact]
    public async Task Analyzer_ProcessesMoreThanOneBoundedClassificationChunkInGlobalOrder()
    {
        const int operationCount = 513;
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE chunked_analysis (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var ordinal = 0; ordinal < operationCount; ordinal++)
        {
            builder.EnsureColumn(
                "chunked_analysis",
                new ExpectedColumnDefinition($"value_{ordinal}", typeof(int), true, "integer"),
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
        await ExecuteSqlAsync(connectionString, "CREATE TABLE oversized_analysis (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "oversized_analysis",
            new ExpectedColumnDefinition("payload", typeof(string), true, "text", comment: oversizedComment),
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
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'oversized_analysis' "
                + "AND column_name = 'payload';"));
    }

    [Fact]
    public async Task Analyzer_RejectsSingleOperationAboveTheParameterLimitBeforeClassificationExecution()
    {
        const int columnCount = 5_400;
        var columns = Enumerable
            .Range(0, columnCount)
            .Select(ordinal => new ExpectedColumnDefinition(
                $"value_{ordinal}",
                typeof(string),
                true,
                "text",
                comment: $"comment_{ordinal}",
                defaultValue: SafeMigrationDefaultValue.Literal($"default_{ordinal}")))
            .ToArray();
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition("oversized_parameters", columns),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("oversized-parameters")));

        Assert.Contains("operation 0 exceeds a bounded query limit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("parameters=", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'oversized_parameters';"));
    }

    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task Analyzer_AcceptsQualifiedCallerOwnedReadOnlyTransaction(
        System.Data.IsolationLevel isolationLevel
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(
            isolationLevel,
            CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY;", CancellationToken.None);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("qualified-caller-transaction"));

        Assert.Equal(SafeMigrationReportStatus.NoOperations, report.Status);
        Assert.Same(transaction, context.Database.CurrentTransaction);
        Assert.Equal("on", await ScalarOnCurrentTransactionAsync(context, "SHOW transaction_read_only;"));
    }

    [Fact]
    public async Task Analyzer_RejectsCallerOwnedReadCommittedTransaction()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("read-committed-caller-transaction")));

        Assert.Contains("RepeatableRead or Serializable", exception.Message, StringComparison.Ordinal);
        Assert.Same(transaction, context.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Analyzer_RejectsCallerOwnedReadWriteTransaction()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await using var transaction = await context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("read-write-caller-transaction")));

        Assert.Contains("to be read-only", exception.Message, StringComparison.Ordinal);
        Assert.Same(transaction, context.Database.CurrentTransaction);
    }

    [Fact]
    public void Analyzer_UsesOneDatabaseLocalSignedBigintAdvisoryKey()
    {
        Assert.Equal(
            "SELECT pg_catalog.pg_advisory_xact_lock(1397574913::bigint);",
            PostgreSqlSafeMigrationProviderAnalyzer.AnalysisAdvisoryLockSql);
        Assert.DoesNotContain("oid", PostgreSqlSafeMigrationProviderAnalyzer.AnalysisAdvisoryLockSql);
    }

    [Fact]
    public async Task OpaqueSqlExpression_IsUnsupportedWithStableReasonBeforeTargetDdl()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE opaque_expression (value integer NULL);");
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
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint WHERE conname = 'ck_opaque_expression';"));
    }

    [Fact]
    public async Task MatchingDerivedContext_UsesTheCanonicalSnapshotAndMigrationHistory()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = new MatchingDerivedContext(connectionString);
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
                $"SELECT COUNT(*) FROM \"__CoreDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';"));
    }

    [Fact]
    public async Task Postflight_BlocksWhenTheExpectedTargetStateWasNotReached()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
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
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() "
                + "AND table_name = '__CoreDbContextMigrationsHistory';"));
    }

    [Fact]
    public async Task Runner_CancellationDuringCatalogAccessClosesItsOwnedConnection()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var analyzer = new BlockingProviderAnalyzer("npgsql_postgresql");
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
            "INSERT INTO \"__CoreDbContextMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") "
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
                await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context, [operation]));

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
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
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

        Assert.Contains(
            report.UnexpectedObjects,
            value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.PrimaryKey, Name: "pk_inventory_constraints"
            });
        Assert.Contains(
            report.UnexpectedObjects,
            value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.UniqueConstraint, Name: "uq_inventory_code"
            });
        Assert.Contains(
            report.UnexpectedObjects,
            value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.CheckConstraint, Name: "ck_inventory_quantity"
            });
        Assert.Contains(
            report.UnexpectedObjects,
            value => value is { ObjectKind: SafeMigrationDatabaseObjectKind.ForeignKey, Name: "fk_inventory_parent" });
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

    private sealed class MatchingDerivedContext(string connectionString) : SafeMigrationDbContext(connectionString);

    private static async Task<string> ScalarOnCurrentTransactionAsync(
        DbContext context,
        string sql
    )
    {
        await using var command = context
            .Database
            .GetDbConnection()
            .CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;

        return Convert.ToString(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture) ?? "<null>";
    }

    private sealed class BlockingProviderAnalyzer(string providerId) : ISafeMigrationProviderAnalyzer
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId { get; } = providerId;

        public Task Started => _started.Task;

        public void ValidateContext(
            DbContext context
        )
        {
            ArgumentNullException.ThrowIfNull(context);
        }

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
