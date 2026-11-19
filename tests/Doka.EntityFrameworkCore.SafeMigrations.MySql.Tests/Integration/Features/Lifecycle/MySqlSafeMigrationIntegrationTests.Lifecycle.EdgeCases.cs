namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the LifecycleEdgeCases migration contracts in an independent class collection.</summary>
public sealed class MySqlLifecycleEdgeCasesIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlLifecycleEdgeCasesIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>
    /// Rejects a connection without the required user-variable capability before querying migration history.
    /// </summary>
    [Fact]
    public async Task ConnectionReplacement_ValidatesRequiredCapabilityBeforeMigrationHistoryQuery()
    {
        // Arrange
        var validConnectionString = await CreateDatabaseAsync(CancellationToken.None);
        var invalidConnectionString = new MySqlConnectionStringBuilder(validConnectionString)
        {
            AllowUserVariables = false,
            GuidFormat = ConnectorMySqlGuidFormat.Binary16,
        }.ConnectionString;

        var interceptor = new HistoryCommandInterceptor();

        await using var validConnection = new MySqlConnection(validConnectionString);
        var validOptions = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseMySql(
                validConnection,
                Fixture.ServerVersion,
                provider => provider
                    .MigrationsAssembly(typeof(SafeMigrationDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__ApplicationDbContextMigrationsHistory"))
            .AddInterceptors(interceptor)
            .UseMySqlSafeMigrations()
            .Options;

        await using var context = new SafeMigrationDbContext(validOptions);
        _ = context.GetService<ISafeMigrationRunner>();
        await using var invalidConnection = new MySqlConnection(invalidConnectionString);

        // A replacement is caller-owned even when the original connection was
        // compatible. Doka validates it during assignment so it cannot inherit
        // the original connection's capability proof.
        // Act
        var failure = Record.Exception(() =>
            context.Database.SetDbConnection(invalidConnection, contextOwnsConnection: false));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure, exactMatch: false);

        Assert.Contains("AllowUserVariables=true", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, interceptor.CommandCount);
        Assert.Equal(ConnectionState.Closed, invalidConnection.State);
    }

    /// <summary>Classifies every child-object family as prerequisite-missing when the owner table is absent.</summary>
    [Fact]
    public async Task MissingParentTable_IsReportedAsPrerequisiteFailureAcrossChildFamilies()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "missing_parent",
            new ExpectedColumnDefinition("value", typeof(int), false, "int"),
            SafeMigrationPolicy.RepairIfSafe);

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

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("missing-parent"),
                CancellationToken.None);

        var exception = await AssertOwnedGuardFailureAsync(
            operationToken =>
                ExecuteOperationsAsync(context, [builder.Operations[0]], operationToken),
            cancellationToken: CancellationToken.None);

        var unexpectedParentTables = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'missing_parent';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, assessment.Action);
                Assert.Equal("prerequisite_missing", assessment.Code);
            });

        Assert.Equal(1062, exception.Number);
        Assert.Contains("doka_sm_prerequisite_missing", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, unexpectedParentTables);
    }

    /// <summary>
    /// Creates the complete required legacy table once without misclassifying a fresh empty owner as data-blocked.
    /// </summary>
    [Fact]
    public async Task LegacyConvergence_CreatesMissingTableWithRequiredColumnsAndRemainsIdempotent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "missing_legacy_table",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                new ExpectedColumnDefinition(
                    "required_value",
                    typeof(string),
                    false,
                    "varchar(40)",
                    maxLength: 40),
            ]);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("missing-legacy-table-preflight"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("missing-legacy-table-postflight"),
            CancellationToken.None);

        var requiredColumnCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'missing_legacy_table'
            AND IS_NULLABLE = 'NO';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.All(
            preflight.Assessments.Skip(1),
            static assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
            });

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(2, requiredColumnCount);
    }

    /// <summary>Detects missing referenced columns before performing data probes that require those columns.</summary>
    [Fact]
    public async Task MissingReferencedColumns_AreClassifiedBeforeAnyDataProbe()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `column_prerequisite_child` (`id` int NOT NULL); CREATE TABLE
            `column_prerequisite_parent` (`id` int NOT NULL);
            """,
            cancellationToken: CancellationToken.None);

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

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("missing-column-prerequisites"),
                CancellationToken.None);

        // Assert
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

    /// <summary>
    /// Projects a nullable legacy column before a following unique index and preserves idempotent replay.
    /// </summary>
    [Fact]
    public async Task LegacyConvergence_AddsNullableColumnBeforeUniqueIndexAndRemainsIdempotent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `legacy_unique_users` (`id` int NOT NULL); "
            + "INSERT INTO `legacy_unique_users` (`id`) VALUES (1), (2);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "legacy_unique_users",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("email", typeof(string), true, "varchar(200)", maxLength: 200),
            ]);

        var index = new ExpectedIndexDefinition(
            "ux_legacy_unique_users_email",
            "legacy_unique_users",
            [new ExpectedIndexKeyDefinition(column: "email")],
            unique: true);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition, [index]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("legacy-unique-users-preflight"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("legacy-unique-users-postflight"),
            CancellationToken.None);

        var uniqueIndexCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'legacy_unique_users'
            AND INDEX_NAME = 'ux_legacy_unique_users_email';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[^1].ObservedState);
        Assert.Equal("projected_missing", preflight.Assessments[^1].Code);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(1, uniqueIndexCount);
    }

    /// <summary>Does not assume a non-null default supplies unique values to existing rows.</summary>
    [Fact]
    public async Task LegacyConvergence_DoesNotProjectUniqueSafetyThroughNonNullDefault()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `legacy_default_users` (`id` int NOT NULL); "
            + "INSERT INTO `legacy_default_users` (`id`) VALUES (1), (2);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "legacy_default_users",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                new ExpectedColumnDefinition(
                    "tenant_id",
                    typeof(int),
                    true,
                    "int",
                    defaultValue: SafeMigrationDefaultValue.Literal(0)),
            ]);

        var index = new ExpectedIndexDefinition(
            "ux_legacy_default_users_tenant",
            "legacy_default_users",
            [new ExpectedIndexKeyDefinition(column: "tenant_id")],
            unique: true);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(definition, [index]);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("legacy-default-users"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[^1].ObservedState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'legacy_default_users'
                AND COLUMN_NAME = 'tenant_id';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Preserves global assessment order across more than one bounded classification chunk.</summary>
    [Fact]
    public async Task Analyzer_ProcessesMoreThanOneBoundedClassificationChunkInGlobalOrder()
    {
        // Arrange
        const int operationCount = 513;
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `chunked_analysis` (`id` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var ordinal = 0; ordinal < operationCount; ordinal++)
        {
            builder.EnsureColumn(
                "chunked_analysis",
                new ExpectedColumnDefinition($"value_{ordinal}", typeof(int), false, "int"),
                SafeMigrationPolicy.RepairIfSafe);
        }

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("chunked-analysis"),
                CancellationToken.None);

        // Assert
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

    /// <summary>Rejects a single oversized UTF-8 catalog request before sending classification SQL.</summary>
    [Fact]
    public async Task Analyzer_RejectsSingleOperationAboveTheUtf8PayloadLimitBeforeClassificationExecution()
    {
        // Arrange
        const int maximumUtf8PayloadBytes = 4 * 1024 * 1024;
        var oversizedComment = new string('\u00e4', maximumUtf8PayloadBytes / 2 + 1);
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `oversized_analysis` (`id` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "oversized_analysis",
            new ExpectedColumnDefinition("payload", typeof(string), true, "longtext", comment: oversizedComment),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var failure = await Record.ExceptionAsync(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("oversized-analysis"),
                CancellationToken.None));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);

        Assert.Contains("operation 0 exceeds a bounded query limit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("utf8_payload_bytes=", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'oversized_analysis'
                AND COLUMN_NAME = 'payload';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Reports opaque SQL as unsupported with a stable reason rather than executing target DDL.</summary>
    [Fact]
    public async Task OpaqueSqlExpression_IsUnsupportedWithStableReasonBeforeTargetDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `opaque_expression` (`value` int NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_opaque_expression",
                "opaque_expression",
                SafeMigrationSql.Opaque("value >= 0")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("opaque-expression"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, assessment.Action);
        Assert.Equal("opaque_sql_expression", assessment.Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                WHERE CONSTRAINT_SCHEMA = DATABASE()
                AND CONSTRAINT_NAME = 'ck_opaque_expression';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Reuses the canonical snapshot and history for a derived context with an unchanged schema.</summary>
    [Fact]
    public async Task MatchingDerivedContext_UsesTheCanonicalSnapshotAndMigrationHistory()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = new MatchingDerivedContext(connectionString, Fixture.ServerVersion);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var emptyReport = await runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("matching-derived"),
            CancellationToken.None);

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        var pendingReport = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions("matching-derived"),
            CancellationToken.None);

        var canonicalHistoryRows = await ScalarIntAsync(
            connectionString,
            $"SELECT COUNT(*) FROM `__ApplicationDbContextMigrationsHistory` "
            + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.NoOperations, emptyReport.Status);

        Assert.Equal(SafeMigrationReportStatus.NoOperations, pendingReport.Status);
        Assert.Equal(1, canonicalHistoryRows);
    }

    /// <summary>
    /// Reports an unmet expected target as blocked rather than treating postflight as a second migration.
    /// </summary>
    [Fact]
    public async Task Postflight_BlocksWhenTheExpectedTargetStateWasNotReached()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "postflight_missing",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .VerifyAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("postflight-negative"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.False(assessment.PostconditionSatisfied);
        Assert.Equal("postcondition_failed", assessment.Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'postflight_missing';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Honors caller cancellation before catalog access and does not create migration history.</summary>
    [Fact]
    public async Task Runner_HonorsCancellationBeforeCatalogAccess()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var failure = await Record.ExceptionAsync(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("cancelled"), cancellation.Token));

        // Assert
        Assert.IsType<OperationCanceledException>(failure, exactMatch: false);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = '__ApplicationDbContextMigrationsHistory';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Closes the runner-owned connection after cancellation at actual catalog admission.</summary>
    [Fact]
    public async Task Runner_CancellationDuringCatalogAccessClosesItsOwnedConnection()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var analyzer = new BlockingProviderAnalyzer("doka_mysql");
        var runner = new SafeMigrationRunner(analyzer);
        using var cancellation = new CancellationTokenSource();

        // Act
        var run = runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("cancelled-during-catalog"),
            cancellation.Token);

        try
        {
            // WHY: A completed/faulted runner is not evidence of analyzer admission.
            // Race actual completion against the exact ready signal, not a machine-speed watchdog.
            var completed = await Task.WhenAny(run, analyzer.Started);
            if (completed == run)
            {
                await run;

                throw new InvalidOperationException("The runner completed before catalog admission.");
            }

            await analyzer.Started;
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        var failure = await Record.ExceptionAsync(async () => await run);

        // Assert
        Assert.IsType<OperationCanceledException>(failure, exactMatch: false);
        Assert.Equal(
            ConnectionState.Closed,
            context.Database.GetDbConnection()
                .State);
    }

    /// <summary>Separately classifies an unknown target, completed history, and a backward migration request.</summary>
    /// <param name="historyState">The independently prepared target/history contract.</param>
    [Theory]
    [InlineData("unknown-target")]
    [InlineData("completed")]
    [InlineData("backward-target")]
    public async Task PendingPreflight_RejectsUnknownAndBackwardTargetsAndReportsCompletedHistory(
        string historyState
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();
        if (historyState != "unknown-target")
        {
            await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        }

        if (historyState == "backward-target")
        {
            await ExecuteSqlAsync(
                connectionString,
                """
                INSERT INTO `__ApplicationDbContextMigrationsHistory` (`MigrationId`,
                `ProductVersion`) VALUES ('209901010000_Future', '10.0.0');
                """,
                cancellationToken: CancellationToken.None);
        }

        var target = historyState switch
        {
            "unknown-target" => "209901010000_Unknown",
            "backward-target" => CoreConvergenceMigration.MigrationIdentifier,
            "completed" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(historyState)),
        };

        SafeMigrationRunReport? report = null;

        // Act
        var failure = await Record.ExceptionAsync(async () => report = await runner.AnalyzePendingMigrationsAsync(
            context,
            new SafeMigrationRunOptions(historyState, target),
            CancellationToken.None));

        // Assert
        if (historyState == "unknown-target")
        {
            Assert.IsType<ArgumentException>(failure);
        }
        else if (historyState == "backward-target")
        {
            Assert.IsType<InvalidOperationException>(failure);
        }
        else
        {
            Assert.Null(failure);
            Assert.NotNull(report);
            Assert.Equal(SafeMigrationReportStatus.NoOperations, report.Status);
            Assert.Empty(report.Assessments);
        }
    }

    /// <summary>
    /// Rejects reversal of the forward-only convergence baseline before producing destructive operations.
    /// </summary>
    [Fact]
    public void ConvergenceBaseline_DownFailsBeforeProducingDestructiveOperations()
    {
        // Arrange
        var migration = new CoreConvergenceMigration();

        // Act
        var failure = Record.Exception(() => migration.DownOperations);

        // Assert
        var exception = Assert.IsType<NotSupportedException>(failure);

        Assert.Contains("forward-only", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rejects occupied rename destinations without changing source or target objects.</summary>
    [Fact]
    public async Task RenameTargetCollisions_AreRejectedWithoutMutatingEitherObject()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `rename_source` (`source_column` int NULL, `target_column`
            int NULL); CREATE TABLE `rename_target` (`id` int NOT NULL); CREATE INDEX
            `rename_source_index` ON `rename_source` (`source_column`); CREATE INDEX
            `rename_target_index` ON `rename_source` (`target_column`);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("rename_source", "rename_target");
        builder.RenameColumnIfExists("source_column", "rename_source", "target_column");
        builder.RenameIndexIfExists("rename_source_index", "rename_source", "rename_target_index");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("rename-collisions"),
                CancellationToken.None);

        List<MySqlException> failures = [];
        foreach (var operation in builder.Operations)
        {
            var exception =
                await AssertOwnedGuardFailureAsync(
                    operationToken => ExecuteOperationsAsync(
                        context,
                        [operation],
                        operationToken),
                    cancellationToken: CancellationToken.None);

            failures.Add(exception);
        }

        var preservedTables = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME IN ('rename_source', 'rename_target');
            """,
            cancellationToken: CancellationToken.None);

        var preservedColumns = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'rename_source'
            AND COLUMN_NAME IN ('source_column', 'target_column');
            """,
            cancellationToken: CancellationToken.None);

        var preservedIndexes = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(DISTINCT INDEX_NAME)
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'rename_source'
            AND INDEX_NAME IN ('rename_source_index', 'rename_target_index');
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.All(
            report.Assessments,
            assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
            });

        Assert.Equal(
            2,
            preservedTables);

        Assert.Equal(
            2,
            preservedColumns);

        Assert.Equal(
            2,
            preservedIndexes);

        Assert.Equal(builder.Operations.Count, failures.Count);
        Assert.All(failures,
            exception =>
                Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reports unexpected legacy objects across all constraint families without deleting them.</summary>
    [Fact]
    public async Task UnexpectedObjectInventory_CoversEveryConstraintFamilyWithoutMutation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `inventory_parent` (`id` int NOT NULL, PRIMARY KEY (`id`));
            CREATE TABLE `inventory_constraints` (`id` int NOT NULL,
            `parent_id` int NULL, `code` varchar(30) NULL, `quantity` int NOT NULL,
            PRIMARY KEY (`id`), CONSTRAINT `uq_inventory_code` UNIQUE (`code`), CONSTRAINT
            `ck_inventory_quantity` CHECK (`quantity` >= 0),
            CONSTRAINT `fk_inventory_parent` FOREIGN KEY (`parent_id`) REFERENCES `inventory_parent`
            (`id`));
            """,
            cancellationToken: CancellationToken.None);

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

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("unexpected-constraints"),
                CancellationToken.None);

        // Assert
        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.PrimaryKey);

        Assert.Contains(
            report.UnexpectedObjects,
            value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.UniqueConstraint, Name: "uq_inventory_code",
            });

        Assert.Contains(
            report.UnexpectedObjects,
            value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.CheckConstraint, Name: "ck_inventory_quantity",
            });

        Assert.Contains(
            report.UnexpectedObjects,
            value => value is { ObjectKind: SafeMigrationDatabaseObjectKind.ForeignKey, Name: "fk_inventory_parent" });

        Assert.Equal(
            4,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                WHERE CONSTRAINT_SCHEMA = DATABASE()
                AND TABLE_NAME = 'inventory_constraints';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Serializes concurrent migrators into one canonical schema and one migration history row.</summary>
    [Fact]
    public async Task FourConcurrentMigrators_ProduceOneHistoryRowAndOneCanonicalSchema()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var contexts = Enumerable
            .Range(0, 4)
            .Select(_ => CreateContext(connectionString))

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        // Act
        try
        {
            await Task.WhenAll(
                contexts.Select(context => context.Database.MigrateAsync(cancellationToken: CancellationToken.None)));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }

        // Assert
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `__ApplicationDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'pipeline_state'
                AND COLUMN_NAME = 'payload';
                """,
                cancellationToken: CancellationToken.None));
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Retains the canonical schema while exercising derived-context snapshot and history reuse.</summary>
    protected sealed class MatchingDerivedContext : SafeMigrationDbContext
    {
        /// <summary>Initializes the explicit dependencies of this isolated test helper.</summary>
        /// <param name="connectionString">The uniquely owned native test database.</param>
        /// <param name="serverVersion">The native engine contract selected by the fixture.</param>
        public MatchingDerivedContext(
            string connectionString,
            MySqlServerVersion serverVersion
        ) : base(connectionString, serverVersion) { }
    }

    /// <summary>Counts actual native command dispatch before migration-history capability rejection.</summary>
    protected sealed class HistoryCommandInterceptor : DbCommandInterceptor
    {
        private int _commandCount;

        /// <summary>Gets the number of actual native command dispatches observed by this interceptor.</summary>
        public int CommandCount => Volatile.Read(ref _commandCount);

        /// <inheritdoc />
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result
        )
        {
            Increment();

            return result;
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            Increment();

            return ValueTask.FromResult(result);
        }

        /// <inheritdoc />
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result
        )
        {
            Increment();

            return result;
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default
        )
        {
            Increment();

            return ValueTask.FromResult(result);
        }

        /// <inheritdoc />
        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result
        )
        {
            Increment();

            return result;
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default
        )
        {
            Increment();

            return ValueTask.FromResult(result);
        }

        private void Increment() => Interlocked.Increment(ref _commandCount);
    }

    /// <summary>Signals real analyzer admission and remains pending until caller cancellation.</summary>
    protected sealed class BlockingProviderAnalyzer : ISafeMigrationProviderAnalyzer
    {
        private readonly TaskCompletionSource _started;

        /// <summary>Initializes the explicit dependencies of this isolated test helper.</summary>
        /// <param name="providerId">The exact provider identity accepted by this analyzer.</param>
        public BlockingProviderAnalyzer(
            string providerId
        )
        {
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ProviderId = providerId;
        }

        /// <summary>Gets completion once the actual provider analysis has started.</summary>
        public Task Started => _started.Task;

        /// <inheritdoc />
        public string ProviderId { get; }

        /// <inheritdoc />
        public void ValidateContext(
            DbContext context
        ) => ArgumentNullException.ThrowIfNull(context);

        /// <inheritdoc />
        public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
            DbContext context,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Analysis scope acquisition must not be reached.");

        /// <inheritdoc />
        public async Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
            DbContext context,
            CancellationToken cancellationToken
        )
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            throw new InvalidOperationException("The blocking analyzer completed without cancellation.");
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
            DbContext context,
            IReadOnlyList<SafeMigrationOperation> operations,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Operation analysis must not be reached.");

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
            DbContext context,
            IReadOnlyList<MigrationOperation> operations,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("Unexpected-object analysis must not be reached.");
    }
}
