namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the Columns migration contracts in an independent class collection.</summary>
public sealed partial class MySqlColumnsIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlColumnsIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Rejects ordinary nullable-timestamp tightening without an explicit application backfill.</summary>
    [Fact]
    public async Task OrdinaryNullableTimestampWithoutBackfill_IsRejectedBeforeDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `timestamp_repair` (`id` int NOT NULL, `occurred_at` timestamp(6)
            NULL); INSERT INTO `timestamp_repair` (`id`,
            `occurred_at`) VALUES (1, NULL);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var operation = new AlterColumnOperation
        {
            Table = "timestamp_repair",
            Name = "occurred_at",
            ClrType = typeof(DateTime),
            ColumnType = "timestamp(6)",
            IsNullable = false,
            OldColumn =
            {
                ClrType = typeof(DateTime),
                ColumnType = "timestamp(6)",
                IsNullable = true,
            },
        };

        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var exception = Record.Exception(() =>
            generator.Generate([operation], context.Model));

        // Assert
        var providerException = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains(
            "explicit DefaultValue or DefaultValueSql",
            providerException.Message,
            StringComparison.Ordinal);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `timestamp_repair` WHERE `occurred_at` IS NULL;",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Leaves an ordinary AlterColumn operation provider-owned during SafeMigrations analysis.</summary>
    [Fact]
    public async Task OrdinaryAlterColumnRemainsProviderOwnedDuringPreflight()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `historical_alter` (`id` int NOT NULL, `caption` varchar(100) NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var operation = new AlterColumnOperation
        {
            Table = "historical_alter",
            Name = "caption",
            ClrType = typeof(string),
            ColumnType = "varchar(200)",
            IsNullable = true,
            OldColumn =
            {
                ClrType = typeof(string),
                ColumnType = "varchar(100)",
                IsNullable = true,
            },
        };

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                [operation],
                new SafeMigrationRunOptions("historical-alter"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.False(assessment.IsSafeOperation);
        Assert.Equal("provider_owned_not_analyzed", assessment.Code);
        Assert.Equal(
            100,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT CHARACTER_MAXIMUM_LENGTH
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'historical_alter'
                AND COLUMN_NAME = 'caption';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>
    /// Executes an explicitly backfilled ordinary nullable timestamp before verifying its required target.
    /// </summary>
    [Fact]
    public async Task OrdinaryNullableTimestampWithBackfill_ConvergesTheColumn()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `timestamp_repair` (`id` int NOT NULL, `occurred_at` timestamp(6)
            NULL); INSERT INTO `timestamp_repair` (`id`,
            `occurred_at`) VALUES (1, NULL);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(
            "timestamp_repair",
            new ExpectedColumnDefinition(
                "occurred_at",
                typeof(DateTime),
                isNullable: false,
                storeType: "timestamp(6)",
                defaultValue: SafeMigrationDefaultValue.Sql(
                    SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp, precision: 6))),
            new ExpectedColumnDefinition(
                "occurred_at",
                typeof(DateTime),
                isNullable: true,
                storeType: "timestamp(6)"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("timestamp-backfill"),
                CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.Repair,
            Assert.Single(report.Assessments)
                .Action);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `timestamp_repair` WHERE `occurred_at` IS NULL;",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            "NO",
            await ScalarStringAsync(
                connectionString,
                """
                SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'timestamp_repair'
                AND COLUMN_NAME = 'occurred_at';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Rejects a required SQL NULL default before DDL without replacing the existing NULL value.</summary>
    [Fact]
    public async Task OrdinaryNullableTimestampWithNullSqlBackfill_IsRejectedBeforeDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `timestamp_null_backfill` (`id` int NOT NULL, `occurred_at`
            timestamp(6) NULL); INSERT INTO `timestamp_null_backfill` (`id`,
            `occurred_at`) VALUES (1, NULL);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent(
            "timestamp_null_backfill",
            new ExpectedColumnDefinition(
                "occurred_at",
                typeof(DateTime),
                isNullable: false,
                storeType: "timestamp(6)",
                defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(null))),
            new ExpectedColumnDefinition(
                "occurred_at",
                typeof(DateTime),
                isNullable: true,
                storeType: "timestamp(6)"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("timestamp-null-backfill"),
                CancellationToken.None);

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            builder.Operations,
            CancellationToken.None));

        // Assert
        var providerException = AssertOwnedGuardFailure(exception);
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);

        // WHY: MariaDB cannot represent DEFAULT (NULL) on a required column;
        // MySQL reaches the separate proof that NULL rows cannot be backfilled.
        Assert.Equal(
            Fixture.IsMariaDb
                ? SafeMigrationAction.RejectUnsupported
                : SafeMigrationAction.RejectDataBlocked,
            assessment.Action);

        Assert.Equal(
            Fixture.IsMariaDb
                ? SafeMigrationObservedState.Unsupported
                : SafeMigrationObservedState.DataBlocked,
            assessment.ObservedState);

        Assert.Contains(
            Fixture.IsMariaDb
                ? "doka_sm_unsupported"
                : "doka_sm_data_blocked",
            providerException.Message,
            StringComparison.Ordinal);

        if (Fixture.IsMariaDb)
        {
            Assert.Equal("literal_default_catalog_representation", assessment.AnalysisCode);
        }

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `timestamp_null_backfill` WHERE `occurred_at` IS NULL;",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            "YES",
            await ScalarStringAsync(
                connectionString,
                """
                SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'timestamp_null_backfill'
                AND COLUMN_NAME = 'occurred_at';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Rejects schema-qualified column collations before target DDL.</summary>
    [Fact]
    public async Task SchemaQualifiedColumnCollation_IsUnsupportedBeforeTargetDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE qualified_collation (id int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "qualified_collation",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                true,
                "varchar(40)",
                maxLength: 40,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin", "other_database")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("qualified-collation"),
                CancellationToken.None);

        var exception =
            await AssertOwnedGuardFailureAsync(
                operationToken => ExecuteOperationsAsync(
                    context,
                    builder.Operations,
                    operationToken),
                cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'qualified_collation'
                AND COLUMN_NAME = 'value';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Interprets a null authored collation as the table default, never as a wildcard.</summary>
    [Fact]
    public async Task NullCollation_MeansTableDefaultAndNeverWildcard()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `collation_defaults` (`inherited` varchar(40) NULL, `explicit`
            varchar(40) COLLATE utf8mb4_bin NULL) DEFAULT CHARACTER SET utf8mb4 COLLATE
            utf8mb4_general_ci;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var inherited = new MigrationBuilder(context.Database.ProviderName!);
        inherited.EnsureColumn(
            "collation_defaults",
            new ExpectedColumnDefinition("inherited", typeof(string), true, "varchar(40)", maxLength: 40),
            SafeMigrationPolicy.ThrowIfDifferent);

        var explicitDrift = new MigrationBuilder(context.Database.ProviderName!);
        explicitDrift.EnsureColumn(
            "collation_defaults",
            new ExpectedColumnDefinition("explicit", typeof(string), true, "varchar(40)", maxLength: 40),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var inheritedReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                inherited.Operations,
                new SafeMigrationRunOptions("inherited-collation"),
                CancellationToken.None);

        var driftReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                explicitDrift.Operations,
                new SafeMigrationRunOptions("explicit-collation"),
                CancellationToken.None);

        // Assert
        Assert.Equal(
            SafeMigrationObservedState.Matching,
            Assert.Single(inheritedReport.Assessments)
                .ObservedState);

        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(driftReport.Assessments)
                .ObservedState);
    }

    /// <summary>Reports explicit text-collation drift using the logical authored collation identity.</summary>
    [Fact]
    public async Task ExplicitTextCollationDrift_ReportsTheLogicalExpectedCollation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `text_collation_diagnostic` (`value` varchar(40) COLLATE utf8mb4_bin
            NULL) DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "text_collation_diagnostic",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                true,
                "varchar(40)",
                maxLength: 40,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_unicode_ci")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("text-collation-diagnostic"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        var difference = Assert.Single(assessment.Differences);

        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal("column_collation", difference.Facet);
        Assert.Equal("utf8mb4_unicode_ci", difference.Expected);
        Assert.Equal("utf8mb4_bin", difference.Actual);
    }

    /// <summary>Uses Doka's physical MariaDB JSON collation contract for matching and drift diagnostics.</summary>
    [Fact]
    public async Task MariaDbJsonCollationDrift_ReportsAndMatchesTheProviderPhysicalContract()
    {
        // Arrange
        if (!Fixture.IsMariaDb)
        {
            return;
        }

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `json_collation_diagnostic` ("
            + "`Config` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL); ",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "json_collation_diagnostic",
            new ExpectedColumnDefinition(
                "Config",
                typeof(string),
                true,
                "json",
                collation: new SafeMigrationCollationIdentifier("utf8mb4_unicode_ci")),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var differentReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("json-collation-different"),
                CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "ALTER TABLE `json_collation_diagnostic` MODIFY COLUMN "
            + "`Config` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL;",
            cancellationToken: CancellationToken.None);

        var matchingReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("json-collation-matching"),
                CancellationToken.None);

        // Assert
        var differentAssessment = Assert.Single(differentReport.Assessments);
        var difference = Assert.Single(differentAssessment.Differences);
        var matchingAssessment = Assert.Single(matchingReport.Assessments);

        Assert.Equal(SafeMigrationObservedState.Different, differentAssessment.ObservedState);
        Assert.Equal("column_collation", difference.Facet);
        Assert.Equal("utf8mb4_bin", difference.Expected);
        Assert.Equal("utf8mb4_unicode_ci", difference.Actual);

        Assert.Equal(SafeMigrationObservedState.Matching, matchingAssessment.ObservedState);
        Assert.Empty(matchingAssessment.Differences);
    }

    /// <summary>Uses native MySQL JSON's null-collation contract instead of imposing a text-table collation.</summary>
    [Fact]
    public async Task MySqlNativeJson_UsesTheNativeNullCollationContract()
    {
        // Arrange
        if (Fixture.IsMariaDb)
        {
            return;
        }

        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `native_json_collation` (`Config` json NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "native_json_collation",
            new ExpectedColumnDefinition(
                "Config",
                typeof(string),
                true,
                "json"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("native-json-collation"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
        Assert.Empty(assessment.Differences);
    }

    /// <summary>
    /// Rejects a required column addition without sufficient row backfill evidence before native DDL.
    /// </summary>
    [Fact]
    public async Task UnsafeNotNullAdd_FailsBeforeTargetDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `orders` (`id` int NOT NULL); INSERT INTO `orders` VALUES (1);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("sequence", "orders", type: "int", nullable: false);

        // Act
        var exception =
            await AssertOwnedGuardFailureAsync(
                operationToken => ExecuteOperationsAsync(
                    context,
                    builder.Operations,
                    operationToken),
                cancellationToken: CancellationToken.None);

        // Assert
        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'orders' AND COLUMN_NAME = 'sequence';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Requires the live source to match the explicitly declared old column before safe alteration.</summary>
    [Fact]
    public async Task RepairIfSafe_RequiresTheLiveColumnToMatchTheDeclaredOldDefinition()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `repair_guard` (" + "`safe_value` varchar(40) NULL, `drifted_value` varchar(30) NULL); ",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var declaredOld = new ExpectedColumnDefinition(
            "safe_value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40);

        var target = new ExpectedColumnDefinition(
            "safe_value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40,
            comment: "approved repair");

        var safeRepair = new MigrationBuilder(context.Database.ProviderName!);
        safeRepair.AlterColumnIfDifferent("repair_guard", target, declaredOld, SafeMigrationPolicy.RepairIfSafe);

        // Act
        await ExecuteOperationsAsync(context, safeRepair.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, safeRepair.Operations, CancellationToken.None);

        var approvedRepairColumns = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'repair_guard'
            AND COLUMN_NAME = 'safe_value'
            AND COLUMN_COMMENT = 'approved repair';
            """,
            cancellationToken: CancellationToken.None);

        var driftedOld = new ExpectedColumnDefinition(
            "drifted_value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40);

        var driftedTarget = new ExpectedColumnDefinition(
            "drifted_value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40,
            comment: "must not land");

        var blockedRepair = new MigrationBuilder(context.Database.ProviderName!);
        blockedRepair.AlterColumnIfDifferent(
            "repair_guard",
            driftedTarget,
            driftedOld,
            SafeMigrationPolicy.RepairIfSafe);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                blockedRepair.Operations,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

        var exception =
            await AssertOwnedGuardFailureAsync(
                operationToken => ExecuteOperationsAsync(
                    context,
                    blockedRepair.Operations,
                    operationToken),
                cancellationToken: CancellationToken.None);

        var preservedDriftedColumns = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'repair_guard'
            AND COLUMN_NAME = 'drifted_value'
            AND CHARACTER_MAXIMUM_LENGTH = 30
            AND COLUMN_COMMENT = '';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(1, approvedRepairColumns);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);

        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, preservedDriftedColumns);
    }

    /// <summary>Repairs only accepted mutable legacy column facets and preserves idempotent replay.</summary>
    [Fact]
    public async Task LegacyConvergenceRepair_RepairsMutableColumnDriftAndIsIdempotent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `legacy_repair` (`value` varchar(40) NULL DEFAULT 'legacy'
            COMMENT 'legacy'); INSERT INTO `legacy_repair` (`value`) VALUES ('existing');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "legacy_repair",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: false,
                storeType: "varchar(40)",
                maxLength: 40,
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("legacy-repair"),
                CancellationToken.None);

        var providerAnalysis = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(
                context,
                builder
                    .Operations
                    .Cast<SafeMigrationOperation>()

                    // WHY: Retain this explicit materialization/copy path and its collection lowering.
                    // ReSharper disable once UseCollectionExpression
                    .ToArray(),
                CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var rerun = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("legacy-repair-rerun"),
                CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `legacy_repair` () VALUES ();",
            cancellationToken: CancellationToken.None);

        var canonicalColumnCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'legacy_repair'
            AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'NO'
            AND COLUMN_COMMENT = 'canonical';
            """,
            cancellationToken: CancellationToken.None);

        var canonicalDefaultRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM `legacy_repair` WHERE `value` = 'canonical';",
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        var providerAssessment = Assert.Single(providerAnalysis);
        var rerunAssessment = Assert.Single(rerun.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Repair, assessment.Action);
        Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, assessment.OperationalImpact);
        Assert.True(providerAssessment.RequiresLiveDataProof);

        Assert.Equal(SafeMigrationObservedState.Matching, rerunAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, rerunAssessment.Action);

        Assert.Equal(1, canonicalColumnCount);

        Assert.Equal(1, canonicalDefaultRows);
    }

    /// <summary>Keeps legacy nullability tightening data-blocked while incompatible NULL rows remain.</summary>
    [Fact]
    public async Task LegacyConvergenceRepair_NullabilityTighteningWithNullRowsIsDataBlocked()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `legacy_nulls` (`value` varchar(40) NULL COMMENT 'legacy');
            INSERT INTO `legacy_nulls` (`value`) VALUES (NULL);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "legacy_nulls",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: false,
                storeType: "varchar(40)",
                maxLength: 40,
                comment: "must not land"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("legacy-null-block"),
                CancellationToken.None);

        var providerAnalysis = await context
            .GetService<ISafeMigrationProviderAnalyzer>()
            .AnalyzeAsync(
                context,
                builder
                    .Operations
                    .Cast<SafeMigrationOperation>()

                    // WHY: Retain this explicit materialization/copy path and its collection lowering.
                    // ReSharper disable once UseCollectionExpression
                    .ToArray(),
                CancellationToken.None);

        var exception = await AssertOwnedGuardFailureAsync(
            operationToken =>
                ExecuteOperationsAsync(context, builder.Operations, operationToken),
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        var providerAssessment = Assert.Single(providerAnalysis);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, assessment.Action);
        Assert.Equal(SafeMigrationOperationalImpact.TableRewritePossible, assessment.OperationalImpact);
        Assert.True(providerAssessment.RequiresLiveDataProof);
        Assert.Contains("doka_sm_data_blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'legacy_nulls'
                AND COLUMN_NAME = 'value' AND IS_NULLABLE = 'YES'
                AND COLUMN_COMMENT = 'legacy';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Rejects invariant legacy type drift without changing the column or its data.</summary>
    [Fact]
    public async Task LegacyConvergenceRepair_InvariantTypeDriftRejectsWithoutMutation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `legacy_type_drift` (`value` char(30) NULL COMMENT 'legacy');",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "legacy_type_drift",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "varchar(40)",
                maxLength: 40,
                comment: "must not land"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("legacy-type-drift"),
                CancellationToken.None);

        var exception = await AssertOwnedGuardFailureAsync(
            operationToken =>
                ExecuteOperationsAsync(context, builder.Operations, operationToken),
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'legacy_type_drift'
                AND COLUMN_NAME = 'value' AND DATA_TYPE = 'char'
                AND CHARACTER_MAXIMUM_LENGTH = 30
                AND COLUMN_COMMENT = 'legacy';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Rejects an unmodeled native ON UPDATE modifier rather than discarding it during repair.</summary>
    [Fact]
    public async Task LegacyConvergenceRepair_UnmodeledOnUpdateModifierRejectsWithoutMutation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `legacy_on_update` (`value` timestamp(6) NULL DEFAULT CURRENT_TIMESTAMP(6)
            ON UPDATE CURRENT_TIMESTAMP(6) COMMENT 'legacy');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "legacy_on_update",
            new ExpectedColumnDefinition(
                "value",
                typeof(DateTime),
                isNullable: true,
                storeType: "timestamp(6)",
                comment: "must not land"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("legacy-on-update"),
                CancellationToken.None);

        var exception = await AssertOwnedGuardFailureAsync(
            operationToken =>
                ExecuteOperationsAsync(context, builder.Operations, operationToken),
            cancellationToken: CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "on update",
            await ScalarStringAsync(
                connectionString,
                """
                SELECT LOWER(EXTRA) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'legacy_on_update'
                AND COLUMN_NAME = 'value';
                """,
                cancellationToken: CancellationToken.None),
            StringComparison.Ordinal);

        Assert.Equal(
            "legacy",
            await ScalarStringAsync(
                connectionString,
                """
                SELECT COLUMN_COMMENT FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'legacy_on_update'
                AND COLUMN_NAME = 'value';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Preserves supported typed literal defaults through initial convergence and replay.</summary>
    [Fact]
    public async Task LiteralDefaultMatrix_ConvergesAndIsIdempotent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `default_values` (`id` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var definitions = new[]
        {
            new ExpectedColumnDefinition(
                "boolean_value",
                typeof(bool),
                false,
                "tinyint(1)",
                defaultValue: SafeMigrationDefaultValue.Literal(true)),
            new ExpectedColumnDefinition(
                "byte_value",
                typeof(byte),
                false,
                "tinyint unsigned",
                defaultValue: SafeMigrationDefaultValue.Literal((byte)7)),
            new ExpectedColumnDefinition(
                "sbyte_value",
                typeof(sbyte),
                false,
                "tinyint",
                defaultValue: SafeMigrationDefaultValue.Literal((sbyte)-7)),
            new ExpectedColumnDefinition(
                "short_value",
                typeof(short),
                false,
                "smallint",
                defaultValue: SafeMigrationDefaultValue.Literal((short)-12)),
            new ExpectedColumnDefinition(
                "ushort_value",
                typeof(ushort),
                false,
                "smallint unsigned",
                defaultValue: SafeMigrationDefaultValue.Literal((ushort)12)),
            new ExpectedColumnDefinition(
                "integer_value",
                typeof(int),
                false,
                "int",
                defaultValue: SafeMigrationDefaultValue.Literal(42)),
            new ExpectedColumnDefinition(
                "uint_value",
                typeof(uint),
                false,
                "int unsigned",
                defaultValue: SafeMigrationDefaultValue.Literal(42U)),
            new ExpectedColumnDefinition(
                "long_value",
                typeof(long),
                false,
                "bigint",
                defaultValue: SafeMigrationDefaultValue.Literal(4200L)),
            new ExpectedColumnDefinition(
                "ulong_value",
                typeof(ulong),
                false,
                "bigint unsigned",
                defaultValue: SafeMigrationDefaultValue.Literal(4200UL)),
            new ExpectedColumnDefinition(
                "decimal_value",
                typeof(decimal),
                false,
                "decimal(10,2)",
                precision: 10,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "float_value",
                typeof(float),
                false,
                "float",
                defaultValue: SafeMigrationDefaultValue.Literal(1.5f)),
            new ExpectedColumnDefinition(
                "double_value",
                typeof(double),
                false,
                "double",
                defaultValue: SafeMigrationDefaultValue.Literal(1.75d)),
            new ExpectedColumnDefinition(
                "string_value",
                typeof(string),
                false,
                "varchar(80)",
                maxLength: 80,
                defaultValue: SafeMigrationDefaultValue.Literal("O'Reilly")),
            new ExpectedColumnDefinition(
                "char_value",
                typeof(char),
                false,
                "char(1)",
                maxLength: 1,
                isFixedLength: true,
                defaultValue: SafeMigrationDefaultValue.Literal('x')),
            new ExpectedColumnDefinition(
                "binary_value",
                typeof(byte[]),
                false,
                "varbinary(4)",
                maxLength: 4,
                defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 1, 2, 3 })),
            new ExpectedColumnDefinition(
                "guid_value",
                typeof(Guid),
                false,
                "binary(16)",
                maxLength: 16,
                isFixedLength: true,
                defaultValue:
                SafeMigrationDefaultValue.Literal(Guid.Parse("0198bfe2-5573-7000-8000-000000000001"))),
            new ExpectedColumnDefinition(
                "enum_value",
                typeof(DayOfWeek),
                false,
                "int",
                defaultValue: SafeMigrationDefaultValue.Literal(DayOfWeek.Wednesday)),
            new ExpectedColumnDefinition(
                "date_value",
                typeof(DateOnly),
                false,
                "date",
                defaultValue: SafeMigrationDefaultValue.Literal(new DateOnly(2026, 8, 17))),
            new ExpectedColumnDefinition(
                "time_value",
                typeof(TimeOnly),
                false,
                "time(6)",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Literal(new TimeOnly(12, 34, 56))),
            new ExpectedColumnDefinition(
                "datetime_value",
                typeof(DateTime),
                false,
                "datetime(6)",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Literal(
                    new DateTime(
                        2026,
                        8,
                        17,
                        12,
                        34,
                        56,
                        DateTimeKind.Unspecified))),
            new ExpectedColumnDefinition(
                "datetime_offset_value",
                typeof(DateTimeOffset),
                false,
                defaultValue: SafeMigrationDefaultValue.Literal(
                    new DateTimeOffset(
                        2026,
                        8,
                        17,
                        12,
                        34,
                        56,
                        TimeSpan.FromMinutes(330)).AddTicks(1_234_567))),
            new ExpectedColumnDefinition(
                "duration_value",
                typeof(TimeSpan),
                false,
                "time(6)",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Literal(TimeSpan.FromMinutes(90))),
            new ExpectedColumnDefinition(
                "literal_null_value",
                typeof(string),
                true,
                "varchar(20)",
                maxLength: 20,
                defaultValue: SafeMigrationDefaultValue.Literal(null)),
            new ExpectedColumnDefinition("no_default_value", typeof(string), true, "varchar(20)", maxLength: 20),
            new ExpectedColumnDefinition(
                "sql_default_value",
                typeof(DateTime),
                false,
                "datetime(6)",
                precision: 6,
                defaultValue: SafeMigrationDefaultValue.Sql(
                    SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp, precision: 6))),
        };

        var coveredLiteralTypes = definitions
            .Where(static definition => definition.DefaultValue.Kind == SafeMigrationDefaultValueKind.Literal)
            .Select(static definition => definition.DefaultValue.LiteralValue)
            .Where(static value => value is not null)
            .Select(static value => value!.GetType())
            .Distinct()
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var expectedColumnCount = 1;
        List<string> unsupportedColumns = [];
        List<(SafeMigrationRunReport Report, MySqlException? Failure, int? ColumnCount)> observations = [];

        // Act
        foreach (var definition in definitions)
        {
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.EnsureColumn("default_values", definition, SafeMigrationPolicy.ThrowIfDifferent);
            var preflight = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(
                    context,
                    builder.Operations,
                    new SafeMigrationRunOptions("default-matrix"),
                    CancellationToken.None);

            var assessment = preflight.Assessments.Single();
            if (assessment.ObservedState == SafeMigrationObservedState.Unsupported)
            {
                unsupportedColumns.Add(definition.Name);

                var unsupported =
                    await AssertOwnedGuardFailureAsync(
                        operationToken => ExecuteOperationsAsync(
                            context,
                            builder.Operations,
                            operationToken),
                        cancellationToken: CancellationToken.None);

                var unsupportedColumnCount = await ScalarIntAsync(
                    connectionString,
                    $"""
                    SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE()
                    AND TABLE_NAME = 'default_values'
                    AND COLUMN_NAME = '{definition.Name}';
                    """,
                    cancellationToken: CancellationToken.None);

                observations.Add((preflight, unsupported, unsupportedColumnCount));

                continue;
            }

            observations.Add((preflight, null, null));

            try
            {
                await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
                await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
                expectedColumnCount++;
            }
            catch (Exception exception)
            {
                var catalog = await DescribeColumnAsync(
                    connectionString,
                    definition.Name,
                    cancellationToken: CancellationToken.None);

                throw new InvalidOperationException(
                    $"Literal default failed for '{definition.Name}'. Catalog: {catalog}",
                    exception);
            }
        }

        // Assert
        foreach (var observation in observations)
        {
            var assessment = Assert.Single(observation.Report.Assessments);
            if (assessment.ObservedState == SafeMigrationObservedState.Unsupported)
            {
                Assert.NotNull(observation.Failure);
                Assert.Contains("doka_sm_unsupported", observation.Failure.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, observation.ColumnCount);
            }
            else
            {
                Assert.Equal(SafeMigrationObservedState.Missing, assessment.ObservedState);
            }
        }

        Assert.Equal(
            SupportsBinaryGuidCatalogDefaults()
                ? []
                : ["guid_value"],
            unsupportedColumns);

        Assert.Equal(SafeMigrationLiteralContract.CreateSupportedNonNullTypes(), coveredLiteralTypes);
        Assert.Equal(
            expectedColumnCount,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'default_values';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Performs the describe column test operation with explicit cancellation.</summary>
    /// <param name="connectionString">The connection string for this operation.</param>
    /// <param name="column">The column for this operation.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task<string> DescribeColumnAsync(
        string connectionString,
        string column,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT COLUMN_TYPE, COLUMN_DEFAULT, IS_NULLABLE, EXTRA, HEX(COLUMN_DEFAULT)
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME = 'default_values'
        AND COLUMN_NAME = @column;
        """;

        command.Parameters.AddWithValue("column", column);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return "missing";
        }

        return string.Join(
            " | ",
            Enumerable
                .Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index)
                    ? "<null>"
                    : reader
                        .GetValue(index)
                        .ToString()));
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Performs the describe check constraint test operation with explicit cancellation.</summary>
    /// <param name="connectionString">The connection string for this operation.</param>
    /// <param name="constraint">The constraint for this operation.</param>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    protected static async Task<string> DescribeCheckConstraintAsync(
        string connectionString,
        string constraint,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
        SELECT CHECK_CLAUSE, HEX(CHECK_CLAUSE)
        FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS
        WHERE CONSTRAINT_SCHEMA = DATABASE()
        AND CONSTRAINT_NAME = @constraint;
        """;

        command.Parameters.AddWithValue("constraint", constraint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return "missing";
        }

        return $"{reader.GetString(0)} | {reader.GetString(1)}";
    }
}

public sealed partial class MySqlColumnsIntegrationTests
{
    /// <summary>Rejects an unmapped CLR type before attempting native column creation.</summary>
    [Fact]
    public async Task UnmappedClrType_IsClassifiedUnsupportedBeforeDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `unsupported_values` (`id` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "unsupported_values",
            new ExpectedColumnDefinition("unmapped_value", typeof(UnmappedValue), true),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'unsupported_values'
                AND COLUMN_NAME = 'unmapped_value';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Does not claim binary GUID default equality beyond the captured engine's catalog fidelity.</summary>
    [Fact]
    public async Task BinaryGuidDefault_RespectsEngineCatalogFidelity()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `unsupported_binary_default` (`id` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "unsupported_binary_default",
            new ExpectedColumnDefinition(
                "binary_guid",
                typeof(Guid),
                false,
                "binary(16)",
                maxLength: 16,
                isFixedLength: true,
                defaultValue: SafeMigrationDefaultValue.Literal(Guid.Parse("0198bfe2-5573-7000-8000-000000000001"))),
            SafeMigrationPolicy.ThrowIfDifferent);

        var supportsDefaults = SupportsBinaryGuidCatalogDefaults();
        MySqlException? unsupportedFailure = null;
        SafeMigrationRunReport? matching = null;
        SafeMigrationRunReport? mismatch = null;

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

        if (!supportsDefaults)
        {
            unsupportedFailure =
                await AssertOwnedGuardFailureAsync(
                    operationToken => ExecuteOperationsAsync(
                        context,
                        builder.Operations,
                        operationToken),
                    cancellationToken: CancellationToken.None);
        }
        else
        {
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

            matching = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(
                    context,
                    builder.Operations,
                    new SafeMigrationRunOptions("test-instance"),
                    CancellationToken.None);

            var different = new MigrationBuilder(context.Database.ProviderName!);
            different.EnsureColumn(
                "unsupported_binary_default",
                new ExpectedColumnDefinition(
                    "binary_guid",
                    typeof(Guid),
                    false,
                    "binary(16)",
                    maxLength: 16,
                    isFixedLength: true,
                    defaultValue: SafeMigrationDefaultValue.Literal(
                        Guid.Parse("0198bfe2-5573-7000-8000-000000000002"))),
                SafeMigrationPolicy.ThrowIfDifferent);

            mismatch = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(
                    context,
                    different.Operations,
                    new SafeMigrationRunOptions("test-instance"),
                    CancellationToken.None);
        }

        // Assert
        if (!supportsDefaults)
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(
                SafeMigrationObservedState.Unsupported,
                Assert.Single(report.Assessments)
                    .ObservedState);

            Assert.NotNull(unsupportedFailure);
            Assert.Contains("doka_sm_unsupported", unsupportedFailure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                0,
                await ScalarIntAsync(
                    connectionString,
                    """
                    SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE()
                    AND TABLE_NAME = 'unsupported_binary_default'
                    AND COLUMN_NAME = 'binary_guid';
                    """,
                    cancellationToken: CancellationToken.None));

            return;
        }

        Assert.NotNull(matching);
        Assert.NotNull(mismatch);
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Missing,
            Assert.Single(report.Assessments)
                .ObservedState);

        Assert.Equal(SafeMigrationReportStatus.Ready, matching.Status);
        Assert.Equal(
            SafeMigrationObservedState.Matching,
            Assert.Single(matching.Assessments)
                .ObservedState);

        Assert.Equal(SafeMigrationReportStatus.Blocked, mismatch.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(mismatch.Assessments)
                .ObservedState);
    }

    private bool SupportsBinaryGuidCatalogDefaults()
    {
        var version = Fixture.ServerVersion.Version;

        return Fixture.IsMariaDb
            && version is { Major: 11, Minor: 8 } or { Major: 12, Minor: 3 };
    }

    /// <summary>Preserves computed, collation, and comment contracts while detecting independent facet drift.</summary>
    [Fact]
    public async Task ComputedCollationAndCommentFacets_ConvergeAndDetectSingleFieldDrift()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `advanced_columns` (`a` int NOT NULL, `b` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(
            "advanced_columns",
            new ExpectedColumnDefinition(
                "display_name",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "quote ' slash \\ umlaut \u00fc"),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.EnsureColumn(
            "advanced_columns",
            new ExpectedColumnDefinition(
                "virtual_sum",
                typeof(int),
                true,
                "int",
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Add, "b"),
                isStored: false),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.EnsureColumn(
            "advanced_columns",
            new ExpectedColumnDefinition(
                "stored_sum",
                typeof(int),
                true,
                "int",
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Add, "b"),
                isStored: true),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var mismatch = new MigrationBuilder(context.Database.ProviderName!);
        mismatch.EnsureColumn(
            "advanced_columns",
            new ExpectedColumnDefinition(
                "display_name",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "different"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                mismatch.Operations,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);
    }
}
