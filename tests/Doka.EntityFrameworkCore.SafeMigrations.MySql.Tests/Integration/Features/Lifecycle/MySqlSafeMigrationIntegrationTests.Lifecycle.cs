namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task StrictMismatch_FailsWithStableCategoryAndNextOperationRecoversSameSession()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `accounts` (`id` int NOT NULL, `name` varchar(20) NULL);");
        await using var context = CreateContext(connectionString);
        var mismatch = new MigrationBuilder(context.Database.ProviderName!);
        mismatch.AddColumnIfNotExists<string>(
            "name",
            "accounts",
            type: "varchar(100)",
            nullable: true,
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        var exception =
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteOperationsAsync(context, mismatch.Operations));

        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);

        var valid = new MigrationBuilder(context.Database.ProviderName!);
        valid.AddColumnIfNotExists<string>("description", "accounts", type: "varchar(100)", nullable: true);
        await ExecuteOperationsAsync(context, valid.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'accounts' "
                + "AND COLUMN_NAME = 'description';"));
    }

    [Fact]
    public async Task SessionSqlModes_DoNotDisableAssertionsAndFailureRecovery()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `sql_mode_guard` ("
            + "`id` int NOT NULL, `name` varchar(20) NULL, `quantity` int NOT NULL); "
            + "INSERT INTO `sql_mode_guard` VALUES (1, 'legacy', -1);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var sessionConfiguration = "SET SESSION sql_mode = CONCAT_WS(',', @@SESSION.sql_mode, 'NO_BACKSLASH_ESCAPES');";
        if (Fixture.IsMariaDb)
        {
            sessionConfiguration += " SET SESSION check_constraint_checks = OFF;";
        }

        await context.Database.ExecuteSqlRawAsync(sessionConfiguration, CancellationToken.None);

        var mismatch = new MigrationBuilder(context.Database.ProviderName!);
        mismatch.AddColumnIfNotExists<string>(
            "name",
            "sql_mode_guard",
            type: "varchar(100)",
            nullable: true,
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        var mismatchException =
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteOperationsAsync(context, mismatch.Operations));

        Assert.Contains("doka_sm_different", mismatchException.Message, StringComparison.OrdinalIgnoreCase);

        var blockedCheck = new MigrationBuilder(context.Database.ProviderName!);
        blockedCheck.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_sql_mode_guard_quantity",
                "sql_mode_guard",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var blockedException =
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteOperationsAsync(context, blockedCheck.Operations));

        Assert.Contains("doka_sm_data_blocked", blockedException.Message, StringComparison.OrdinalIgnoreCase);

        var recovery = new MigrationBuilder(context.Database.ProviderName!);
        recovery.AddColumnIfNotExists<string>(
            "note",
            "sql_mode_guard",
            type: "varchar(40)",
            nullable: true,
            comment: "mode\\safe");
        await ExecuteOperationsAsync(context, recovery.Operations);
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE `sql_mode_guard` SET `quantity` = 0 WHERE `quantity` < 0;",
            CancellationToken.None);
        await ExecuteOperationsAsync(context, blockedCheck.Operations);

        Assert.Equal(
            "mode\\safe",
            await ScalarStringAsync(
                connectionString,
                "SELECT COLUMN_COMMENT FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'sql_mode_guard' "
                + "AND COLUMN_NAME = 'note';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'sql_mode_guard' "
                + "AND CONSTRAINT_NAME = 'ck_sql_mode_guard_quantity';"));
    }

    [Fact]
    public async Task MissingHandler_FailsClosedDuringGeneration()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString, registerSafeMigrations: false);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "must_not_exist",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });

        var generator = context.GetService<IMigrationsSqlGenerator>();

        var exception = Assert.Throws<MySqlMigrationOperationHandlerException>(() =>
            generator.Generate(builder.Operations, context.Model));

        Assert.Equal(MySqlMigrationHandlerFailureCode.UnknownOperationType, exception.FailureCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'must_not_exist';"));
    }

    [Fact]
    public async Task Migrator_MixesStandardAndSafeOperationsAndWritesHistoryOnlyAfterSuccess()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `pipeline_state` (`id` int NOT NULL, PRIMARY KEY (`id`));");
        await using var context = CreateContext(connectionString);

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        await context
            .GetService<IMigrator>()
            .MigrateAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state' "
                + "AND COLUMN_NAME = 'payload';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `__CoreDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';"));
    }

    [Fact]
    public async Task MigrationScripts_ExecuteNormalIdempotentAndNoTransactionPaths()
    {
        var generationConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var generationContext = CreateContext(generationConnectionString);
        var migrator = generationContext.GetService<IMigrator>();
        var scripts = new[]
        {
            migrator.GenerateScript(), migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent),
            migrator.GenerateScript(
                options: MigrationsSqlGenerationOptions.Idempotent | MigrationsSqlGenerationOptions.NoTransactions),
        };

        foreach (var script in scripts)
        {
            Assert.Contains(CoreConvergenceMigration.MigrationIdentifier, script, StringComparison.Ordinal);
            var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
            await ExecuteMigrationScriptAsync(connectionString, script);
            if (script != scripts[0])
            {
                await ExecuteMigrationScriptAsync(connectionString, script);
            }

            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                    + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state' "
                    + "AND COLUMN_NAME = 'payload';"));
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connectionString,
                    $"SELECT COUNT(*) FROM `__CoreDbContextMigrationsHistory` "
                    + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';"));
        }
    }

    [Fact]
    public async Task Migrator_WithoutHandlerWritesNeitherTargetDdlNorHistoryRow()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString, registerSafeMigrations: false);

        var exception = await Assert.ThrowsAsync<MySqlMigrationOperationHandlerException>(() =>
            context.Database.MigrateAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(MySqlMigrationHandlerFailureCode.UnknownOperationType, exception.FailureCode);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = '__CoreDbContextMigrationsHistory';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `__CoreDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';"));
    }

    [Fact]
    public async Task RuntimeGuardFailureAfterStandardDdl_RequiresForwardFixAndRemainsRetryable()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE VIEW `pipeline_state` AS SELECT 1 AS `id`;");
        await using var context = CreateContext(connectionString);

        var exception = await Assert.ThrowsAsync<MySqlException>(() => context.Database.MigrateAsync(cancellationToken: CancellationToken.None));

        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_probe' "
                + "AND TABLE_TYPE = 'BASE TABLE';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `__CoreDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';"));

        await ExecuteSqlAsync(connectionString, "DROP VIEW `pipeline_state`; DROP TABLE `pipeline_probe`;");
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `__CoreDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';"));
    }

    [Fact]
    public async Task ExternalInternalServiceProvider_ResolvesTheAdditiveHandlerAndRunner()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var services = new ServiceCollection();
        services.AddEntityFrameworkDokaMySql();
        services.AddEntityFrameworkDokaMySqlSafeMigrations();
        await using var serviceProvider = services.BuildServiceProvider(validateScopes: true);
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseInternalServiceProvider(serviceProvider)
            .UseMySql(
                connectionString,
                Fixture.ServerVersion,
                provider => provider
                    .MigrationsAssembly(typeof(SafeMigrationDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__CoreDbContextMigrationsHistory"))
            .UseMySqlSafeMigrations()
            .Options;

        await using var context = new SafeMigrationDbContext(options);

        Assert.NotNull(context.GetService<ISafeMigrationRunner>());
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state' "
                + "AND COLUMN_NAME = 'payload';"));
    }

    [Fact]
    public async Task ConflictingHandler_FailsBeforeTargetDdlAndHistory()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var services = new ServiceCollection();
        services.AddEntityFrameworkDokaMySql();
        services.AddEntityFrameworkDokaMySqlSafeMigrations();
        services.AddScoped<IMySqlMigrationOperationHandler, ConflictingSafeMigrationHandler>();
        await using var serviceProvider = services.BuildServiceProvider(validateScopes: true);
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseInternalServiceProvider(serviceProvider)
            .UseMySql(
                connectionString,
                Fixture.ServerVersion,
                provider => provider
                    .MigrationsAssembly(typeof(SafeMigrationDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__CoreDbContextMigrationsHistory"))
            .UseMySqlSafeMigrations()
            .Options;

        await using var context = new SafeMigrationDbContext(options);

        var exception =
            await Assert.ThrowsAsync<MySqlMigrationOperationHandlerException>(() => context.Database.MigrateAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(MySqlMigrationHandlerFailureCode.DuplicateOperationOwnership, exception.FailureCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = '__CoreDbContextMigrationsHistory';"));
    }

    [Fact]
    public async Task ConcurrentMigrators_SerializePerDatabaseAndRunDifferentDatabasesInParallel()
    {
        var sharedConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            sharedConnectionString,
            "CREATE TABLE `__LegacyMigrationsHistory` (`MigrationId` varchar(150) NOT NULL); "
            + "INSERT INTO `__LegacyMigrationsHistory` VALUES ('legacy-unchanged');");
        await using var first = CreateContext(sharedConnectionString);
        await using var second = CreateContext(sharedConnectionString);
        await Task.WhenAll(
            first.Database.MigrateAsync(cancellationToken: CancellationToken.None),
            second
                .GetService<IMigrator>()
                .MigrateAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                sharedConnectionString,
                $"SELECT COUNT(*) FROM `__CoreDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                sharedConnectionString,
                "SELECT COUNT(*) FROM `__LegacyMigrationsHistory` " + "WHERE `MigrationId` = 'legacy-unchanged';"));

        var leftConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var rightConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var left = CreateContext(leftConnectionString);
        await using var right = CreateContext(rightConnectionString);
        await Task.WhenAll(left.Database.MigrateAsync(cancellationToken: CancellationToken.None), right.Database.MigrateAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                leftConnectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                rightConnectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';"));
    }

    [Fact]
    public async Task PreflightAndPostflight_AreReadOnlyAndUseRuntimeClassification()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "analysis_target",
            table => new { Id = table.Column<int>(type: "int", nullable: false) },
            mode: SafeMigrationTableMode.ConvergenceContainer,
            policy: SafeMigrationPolicy.ExistenceOnly);
        var runner = context.GetService<ISafeMigrationRunner>();
        var fingerprint = SafeMigrationModelFingerprint.Create(
            context.GetService<IDesignTimeModel>()
                .Model,
            context.Database.ProviderName!);
        var runOptions = new SafeMigrationRunOptions("test-instance", expectedModelFingerprint: fingerprint);

        var preflight = await runner.AnalyzeAsync(context, builder.Operations, runOptions);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationRunReport.CurrentSchemaVersion, preflight.SchemaVersion);
        Assert.Equal("test-instance", preflight.InstanceId);
        Assert.Equal(Fixture.IsMariaDb ? "mariadb" : "mysql", preflight.Environment.EngineFamily);
        Assert.NotEmpty(preflight.Environment.ServerVersion);
        Assert.Equal(64, preflight.ContractFingerprint.Length);
        Assert.Equal(
            SafeMigrationObservedState.Missing,
            Assert.Single(preflight.Assessments)
                .ObservedState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'analysis_target';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() "
                + "AND TABLE_NAME = '__CoreDbContextMigrationsHistory';"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations, runOptions);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(
            Assert.Single(postflight.Assessments)
                .PostconditionSatisfied);
    }

    [Fact]
    public async Task PendingPreflight_ProjectsACompleteMissingConvergenceTable()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzePendingMigrationsAsync(context, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Contains(
            report.Assessments,
            assessment => assessment is
            {
                OperationKind: SafeMigrationOperationKind.EnsureTable,
                ObservedState: SafeMigrationObservedState.Missing
            });
        Assert.Contains(
            report.Assessments,
            assessment => assessment is
            {
                OperationKind: SafeMigrationOperationKind.EnsureColumn,
                ObservedState: SafeMigrationObservedState.Missing,
                Code: "projected_missing"
            });
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';"));
    }

    [Fact]
    public async Task PreflightProjectsProviderAddColumnForFollowingSafeIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `mixed_customers` ("
            + "`id` int NOT NULL, CONSTRAINT `pk_mixed_customers` PRIMARY KEY (`id`));"
            + "CREATE TABLE `mixed_evolution` ("
            + "`id` int NOT NULL, CONSTRAINT `pk_mixed_evolution` PRIMARY KEY (`id`));"
            + "INSERT INTO `mixed_customers` (`id`) VALUES (7);"
            + "INSERT INTO `mixed_evolution` (`id`) VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.AddColumn<int>(
            name: "customer_id",
            table: "mixed_evolution",
            type: "int",
            nullable: false,
            defaultValue: 0);
        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_mixed_evolution_customer_id",
            "mixed_evolution",
            "customer_id");
        _ = builder.Sql("UPDATE `mixed_evolution` SET `customer_id` = 7;");
        _ = builder.Sql("ALTER TABLE `mixed_evolution` ALTER COLUMN `customer_id` DROP DEFAULT;");
        _ = builder.AddForeignKey(
            name: "fk_mixed_evolution_customers_customer_id",
            table: "mixed_evolution",
            column: "customer_id",
            principalTable: "mixed_customers",
            principalColumn: "id",
            onDelete: ReferentialAction.Cascade);

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("mixed-provider-safe-operations");
        var preflight = await runner.AnalyzeAsync(context, builder.Operations, options);
        var projectedIndex = preflight.Assessments.Single(static assessment => assessment.IsSafeOperation);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(5, preflight.Assessments.Count);
        Assert.All(
            preflight.Assessments.Where(static assessment => !assessment.IsSafeOperation),
            static assessment => Assert.Equal("provider_owned_not_analyzed", assessment.Code));
        Assert.Equal(SafeMigrationObservedState.Missing, projectedIndex.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, projectedIndex.Action);
        Assert.Equal("projected_missing", projectedIndex.Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'mixed_evolution' "
                + "AND COLUMN_NAME = 'customer_id';"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await runner.VerifyAsync(context, builder.Operations, options);
        var replay = await runner.AnalyzeAsync(context, builder.Operations, options);
        var postflightIndex = postflight.Assessments.Single(static assessment => assessment.IsSafeOperation);
        var replayIndex = replay.Assessments.Single(static assessment => assessment.IsSafeOperation);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, postflightIndex.ObservedState);
        Assert.True(postflightIndex.PostconditionSatisfied);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, replayIndex.Action);
        Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT `customer_id` FROM `mixed_evolution`;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS "
                + "WHERE CONSTRAINT_SCHEMA = DATABASE() "
                + "AND CONSTRAINT_NAME = 'fk_mixed_evolution_customers_customer_id';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'mixed_evolution' "
                + "AND COLUMN_NAME = 'customer_id' AND COLUMN_DEFAULT IS NULL;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'mixed_evolution' "
                + "AND INDEX_NAME = 'ix_mixed_evolution_customer_id';"));
    }

    [Fact]
    public async Task PreflightPreservesSafeStructureAcrossProviderSeedData()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.CreateTableIfNotExists(
            "seed_projection",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                GroupId = table.Column<int>(type: "int", nullable: false),
            });
        _ = builder.InsertData(
            table: "seed_projection",
            columns: ["Id", "GroupId"],
            columnTypes: ["int", "int"],
            values: new object[,] { { 1, 7, }, });
        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_seed_projection_group_id",
            "seed_projection",
            "GroupId");

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("provider-seed-projection");
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(3, preflight.Assessments.Count);
        Assert.Equal("provider_owned_not_analyzed", preflight.Assessments[1].Code);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[2].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Equal("projected_missing", preflight.Assessments[2].Code);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, replay.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, replay.Assessments[2].Action);
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `seed_projection`;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'seed_projection' "
                + "AND INDEX_NAME = 'ix_seed_projection_group_id';"));
    }

    [Theory]
    [InlineData(
        SafeMigrationScaffoldingMode.Strict,
        DokaMySqlGuidFormat.Binary16)]
    [InlineData(
        SafeMigrationScaffoldingMode.Strict,
        DokaMySqlGuidFormat.Char36)]
    [InlineData(
        SafeMigrationScaffoldingMode.LegacyConvergence,
        DokaMySqlGuidFormat.Binary16)]
    [InlineData(
        SafeMigrationScaffoldingMode.LegacyConvergence,
        DokaMySqlGuidFormat.Char36)]
    public async Task ProviderSeedJourneyPreservesStructureGuidValuesAndCascade(
        SafeMigrationScaffoldingMode mode,
        DokaMySqlGuidFormat guidFormat
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var storeType = GuidStoreType(guidFormat);
        var maxLength = guidFormat == DokaMySqlGuidFormat.Binary16 ? 16 : 36;
        var principalId = Guid.Parse("818f242c-e003-402c-bc14-97b7942d48fa");
        var publicId = Guid.Parse("e858586f-4854-42e6-ab8d-435ad58a8a4c");
        var optionalGuid = Guid.Parse("30892e1d-6d6f-48eb-926e-1ea3d23ad4eb");
        // InsertDataOperation contains provider values after EF's model-differ
        // conversion. Char36 is therefore a string here while Binary16 remains
        // a Guid; snapshots and migration designers preserve model-side Guid
        // literals and are qualified by the Doka provider repository.
        var principalSeedValue = ProviderGuidValue(guidFormat, principalId);
        var publicSeedValue = ProviderGuidValue(guidFormat, publicId);
        var optionalSeedValue = ProviderGuidValue(guidFormat, optionalGuid);

        _ = AddScaffoldedTable(
            builder,
            mode,
            name: "seed_journey_principals",
            columns: table => new
            {
                id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
            },
            constraints: table => table.PrimaryKey("pk_seed_journey_principals", value => value.id));

        _ = AddScaffoldedTable(
            builder,
            mode,
            name: "seed_journey_scalars",
            columns: table => new
            {
                id = table.Column<int>(type: "int", nullable: false),
                public_id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
                optional_guid = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: true)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
            },
            constraints: table => table.PrimaryKey("pk_seed_journey_scalars", value => value.id));

        _ = AddScaffoldedTable(
            builder,
            mode,
            name: "seed_journey_dependents",
            columns: table => new
            {
                id = table.Column<int>(type: "int", nullable: false),
                principal_id = table
                    .Column<Guid>(
                        type: storeType,
                        maxLength: maxLength,
                        fixedLength: true,
                        nullable: false)
                    .Annotation("Doka:MySql:GuidFormat", guidFormat),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_seed_journey_dependents", value => value.id);
                table.ForeignKey(
                    "fk_seed_journey_dependents_principals",
                    value => value.principal_id,
                    "seed_journey_principals",
                    "id",
                    onDelete: ReferentialAction.Cascade);
            });

        _ = builder.InsertData(
            table: "seed_journey_principals",
            column: "id",
            columnType: storeType,
            value: principalSeedValue);
        _ = builder.InsertData(
            table: "seed_journey_scalars",
            columns: ["id", "public_id", "optional_guid"],
            columnTypes: ["int", storeType, storeType],
            values: new object[,] { { 1, publicSeedValue, optionalSeedValue, }, });
        _ = builder.InsertData(
            table: "seed_journey_dependents",
            columns: ["id", "principal_id"],
            columnTypes: ["int", storeType],
            values: new object[,] { { 1, principalSeedValue, }, });
        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_seed_journey_dependents_principal_id",
            "seed_journey_dependents",
            "principal_id");

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions($"provider-seed-journey-{mode}-{guidFormat}");
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);
        var providerAssessments = preflight.Assessments
            .Where(static assessment => !assessment.IsSafeOperation)
            .ToArray();
        var indexAssessment = preflight.Assessments.Single(assessment =>
            assessment.OperationKind == SafeMigrationOperationKind.EnsureIndex
            && assessment.ObjectName == "ix_seed_journey_dependents_principal_id");

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(3, providerAssessments.Length);
        Assert.All(
            providerAssessments,
            static assessment => Assert.Equal("provider_owned_not_analyzed", assessment.Code));
        Assert.Equal(
            providerAssessments[0].Ordinal + 1,
            providerAssessments[1].Ordinal);
        Assert.Equal(
            providerAssessments[1].Ordinal + 1,
            providerAssessments[2].Ordinal);
        Assert.Equal(SafeMigrationObservedState.Missing, indexAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, indexAssessment.Action);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var storedPrincipalId = guidFormat == DokaMySqlGuidFormat.Binary16
            ? await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(HEX(`id`)) FROM `seed_journey_principals`;")
            : await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(`id`) FROM `seed_journey_principals`;");
        var storedPublicId = guidFormat == DokaMySqlGuidFormat.Binary16
            ? await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(HEX(`public_id`)) FROM `seed_journey_scalars`;")
            : await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(`public_id`) FROM `seed_journey_scalars`;");
        var storedOptionalGuid = guidFormat == DokaMySqlGuidFormat.Binary16
            ? await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(HEX(`optional_guid`)) FROM `seed_journey_scalars`;")
            : await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(`optional_guid`) FROM `seed_journey_scalars`;");
        var expectedFormat = guidFormat == DokaMySqlGuidFormat.Binary16 ? "N" : "D";

        Assert.Equal(principalId.ToString(expectedFormat), storedPrincipalId);
        Assert.Equal(publicId.ToString(expectedFormat), storedPublicId);
        Assert.Equal(optionalGuid.ToString(expectedFormat), storedOptionalGuid);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `seed_journey_dependents` AS `dependent` "
                + "INNER JOIN `seed_journey_principals` AS `principal` "
                + "ON `dependent`.`principal_id` = `principal`.`id`;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'seed_journey_dependents' "
                + "AND INDEX_NAME = 'ix_seed_journey_dependents_principal_id';"));

        await ExecuteSqlAsync(connectionString, "DELETE FROM `seed_journey_principals`;");

        Assert.Equal(0, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `seed_journey_dependents`;"));
        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `seed_journey_scalars`;"));
    }

    [Fact]
    public async Task PreflightDoesNotInferUniqueSafetyAcrossProviderSeedData()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.CreateTableIfNotExists(
            "seed_unique_projection",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                GroupId = table.Column<int>(type: "int", nullable: false),
            });
        _ = builder.InsertData(
            table: "seed_unique_projection",
            columns: ["Id", "GroupId"],
            columnTypes: ["int", "int"],
            values: new object[,]
            {
                { 1, 7, },
                { 2, 7, },
            });
        _ = builder.CreateIndexIfNotExistsFromModel(
            "ux_seed_unique_projection_group_id",
            "seed_unique_projection",
            "GroupId",
            unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("provider-seed-unique-projection"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[2].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[2].Action);
        Assert.Equal("prerequisite_missing", report.Assessments[2].Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'seed_unique_projection';"));
    }

    [Fact]
    public async Task PreflightPreservesLiveNonUniqueIndexAcrossProviderSeedData()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `existing_seed_projection` (`Id` int NOT NULL, `GroupId` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.InsertData(
            table: "existing_seed_projection",
            columns: ["Id", "GroupId"],
            columnTypes: ["int", "int"],
            values: new object[,] { { 1, 7, }, });
        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_existing_seed_projection_group_id",
            "existing_seed_projection",
            "GroupId");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("provider-existing-seed-projection"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        Assert.Equal(1, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `existing_seed_projection`;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'existing_seed_projection' "
                + "AND INDEX_NAME = 'ix_existing_seed_projection_group_id';"));
    }

    [Fact]
    public async Task PreflightInvalidatesLiveUniqueIndexSafetyAcrossProviderSeedData()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `existing_seed_unique_projection` (`Id` int NOT NULL, `GroupId` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.InsertData(
            table: "existing_seed_unique_projection",
            columns: ["Id", "GroupId"],
            columnTypes: ["int", "int"],
            values: new object[,]
            {
                { 1, 7, },
                { 2, 7, },
            });
        _ = builder.CreateIndexIfNotExistsFromModel(
            "ux_existing_seed_unique_projection_group_id",
            "existing_seed_unique_projection",
            "GroupId",
            unique: true);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("provider-existing-seed-unique-projection"),
                CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.Equal("prerequisite_missing", report.Assessments[1].Code);
        Assert.Equal(0, await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `existing_seed_unique_projection`;"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'existing_seed_unique_projection' "
                + "AND INDEX_NAME = 'ux_existing_seed_unique_projection_group_id';"));
    }

    [Fact]
    public async Task Preflight_RejectsWrongCanonicalModelFingerprintBeforeCatalogAccess()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();

        await Assert.ThrowsAsync<SafeMigrationModelMismatchException>(() => runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions(
                "test-instance",
                expectedModelFingerprint: "safe-relational-model:v1:Doka.EntityFrameworkCore.MySql:sha256:"
                + new string('0', 64))));
    }

    [Fact]
    public async Task Preflight_RejectsASchemaChangingDerivedContextAgainstTheMigrationSnapshot()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = new SchemaChangingDerivedContext(connectionString, Fixture.ServerVersion);

        await Assert.ThrowsAsync<SafeMigrationModelMismatchException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("test-instance")));
    }

    [Fact]
    public async Task Preflight_ReportsButDoesNotDeleteUnexpectedLegacyObjects()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `inventory_target` ("
            + "`id` int NOT NULL, `legacy_column` int NULL); "
            + "CREATE INDEX `ix_inventory_legacy` ON `inventory_target` (`legacy_column`); "
            + "CREATE TABLE `legacy_extra` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "inventory_target",
            table => new { Id = table.Column<int>(type: "int", nullable: false) },
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.Table && value.Name == "legacy_extra");
        Assert.Contains(
            report.UnexpectedObjects,
            value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.Column, Table: "inventory_target", Name: "legacy_column"
            });
        Assert.Contains(
            report.UnexpectedObjects,
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.Index && value.Name == "ix_inventory_legacy");
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'legacy_extra';"));
    }

    [Fact]
    public async Task ColumnTableIndexLifecycle_IsIdempotentAcrossEveryOperationFamily()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `lifecycle` (`id` int NOT NULL, `old_name` varchar(40) NULL); "
            + "CREATE INDEX `ix_lifecycle_old_name` ON `lifecycle` (`old_name`);");
        await using var context = CreateContext(connectionString);

        var rename = new MigrationBuilder(context.Database.ProviderName!);
        rename.RenameColumnIfExists("old_name", "lifecycle", "name");
        rename.RenameIndexIfExists("ix_lifecycle_old_name", "lifecycle", "ix_lifecycle_name");
        await ExecuteOperationsAsync(context, rename.Operations);
        await ExecuteOperationsAsync(context, rename.Operations);

        var oldColumn = new ExpectedColumnDefinition(
            "name",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40);

        var newColumn = new ExpectedColumnDefinition(
            "name",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40,
            comment: "canonical name",
            defaultValue: SafeMigrationDefaultValue.Literal("unknown"));

        var alter = new MigrationBuilder(context.Database.ProviderName!);
        alter.AlterColumnIfDifferent("lifecycle", newColumn, oldColumn, SafeMigrationPolicy.RepairIfSafe);
        await ExecuteOperationsAsync(context, alter.Operations);
        await ExecuteOperationsAsync(context, alter.Operations);

        var drop = new MigrationBuilder(context.Database.ProviderName!);
        drop.DropIndexIfExists("ix_lifecycle_name", "lifecycle");
        drop.DropColumnIfExists("name", "lifecycle");
        drop.RenameTableIfExists("lifecycle", "renamed_lifecycle");
        await ExecuteOperationsAsync(context, drop.Operations);
        await ExecuteOperationsAsync(context, drop.Operations);

        var dropTable = new MigrationBuilder(context.Database.ProviderName!);
        dropTable.DropTableIfExists("renamed_lifecycle");
        await ExecuteOperationsAsync(context, dropTable.Operations);
        await ExecuteOperationsAsync(context, dropTable.Operations);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'renamed_lifecycle';"));
    }

    [Fact]
    public async Task MissingRenameSources_AreIdempotentNoOps()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("missing_table", "renamed_table");
        builder.RenameColumnIfExists("missing_column", "missing_table", "renamed_column");
        builder.RenameIndexIfExists("missing_index", "missing_table", "renamed_index");

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('missing_table', 'renamed_table');"));
    }

    [Fact]
    public async Task GuardPlan_FailedBodyCleansSameSessionAndRemainsRetryable()
    {
        var databaseConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var connectionString = new MySqlConnectionStringBuilder(databaseConnectionString)
        {
            Pooling = true,
            ConnectionReset = false,
            MaximumPoolSize = 1,
        }.ConnectionString;

        await ExecuteSqlAsync(connectionString, "CREATE TABLE `fault_target` (`id` int NOT NULL);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var physicalConnectionId = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var invalid = new MigrationBuilder(context.Database.ProviderName!);
        invalid.AddColumnIfNotExists<string>("guarded_value", "fault_target", type: "varchar(70000)", nullable: true);

        await Assert.ThrowsAsync<MySqlException>(() => ExecuteOperationsAsync(context, invalid.Operations));

        Assert.Equal(physicalConnectionId, await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();"));
        Assert.Equal(
            1,
            await ContextScalarIntAsync(
                context,
                "SELECT @doka_sm_state IS NULL "
                + "AND @doka_sm_action IS NULL "
                + "AND @doka_sm_repair_ok IS NULL "
                + "AND @doka_sm_prerequisite_ok IS NULL "
                + "AND @doka_sm_sql IS NULL "
                + "AND @doka_sm_post_ok IS NULL;"));

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TEMPORARY TABLE `__doka_sm_assert` (`value` int NOT NULL); "
            + "DROP TEMPORARY TABLE `__doka_sm_assert`;",
            CancellationToken.None);

        var retry = new MigrationBuilder(context.Database.ProviderName!);
        retry.AddColumnIfNotExists<string>("guarded_value", "fault_target", type: "varchar(40)", nullable: true);

        await ExecuteOperationsAsync(context, retry.Operations);
        await ExecuteOperationsAsync(context, retry.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'fault_target' "
                + "AND COLUMN_NAME = 'guarded_value';"));

        await MySqlConnection.ClearAllPoolsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GuardPlan_CancelledBodyCleansSameSessionAndRemainsRetryable()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `cancel_target` (`id` int NOT NULL);");
        await using var blocker = new MySqlConnection(connectionString);
        await blocker.OpenAsync(CancellationToken.None);
        await using var lockCommand = blocker.CreateCommand();
        lockCommand.CommandText = "LOCK TABLES `cancel_target` WRITE;";
        await lockCommand.ExecuteNonQueryAsync(CancellationToken.None);

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var physicalConnectionId = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("guarded_value", "cancel_target", type: "int", nullable: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ExecuteOperationsAsync(context, builder.Operations, cancellation.Token));
        }
        finally
        {
            await using var unlockCommand = blocker.CreateCommand();
            unlockCommand.CommandText = "UNLOCK TABLES;";
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        Assert.Equal(physicalConnectionId, await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();"));
        Assert.Equal(
            1,
            await ContextScalarIntAsync(
                context,
                "SELECT @doka_sm_state IS NULL "
                + "AND @doka_sm_action IS NULL "
                + "AND @doka_sm_repair_ok IS NULL "
                + "AND @doka_sm_prerequisite_ok IS NULL "
                + "AND @doka_sm_sql IS NULL "
                + "AND @doka_sm_post_ok IS NULL;"));

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'cancel_target' "
                + "AND COLUMN_NAME = 'guarded_value';"));
    }

    [Fact]
    public async Task ScopedCommand_CleanupFailureEvictsThePhysicalSession()
    {
        var databaseConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var connectionString = new MySqlConnectionStringBuilder(databaseConnectionString)
        {
            Pooling = true,
            ConnectionReset = false,
            MaximumPoolSize = 1,
        }.ConnectionString;
        var services = new ServiceCollection();
        services.AddEntityFrameworkDokaMySql();
        services.AddScoped<IMySqlMigrationOperationHandler, CleanupFailureHandler>();
        await using var serviceProvider = services.BuildServiceProvider(validateScopes: true);
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseInternalServiceProvider(serviceProvider)
            .UseMySql(connectionString, Fixture.ServerVersion)
            .Options;
        await using var context = new DbContext(options);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var originalConnectionId = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");
        var command = Assert.Single(
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate([new CleanupFailureOperation()], context.Model));

        var exception = await Record.ExceptionAsync(() =>
            command.ExecuteNonQueryAsync(context.GetService<IRelationalConnection>()));

        var cleanupException = Assert.IsType<InvalidOperationException>(exception, exactMatch: false);

        Assert.Equal(
            "MySqlMigrationSessionCleanupException",
            cleanupException.GetType()
                .Name);
        Assert.Equal(
            System.Data.ConnectionState.Closed,
            context.Database.GetDbConnection()
                .State);

        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var replacementConnectionId = await ContextScalarIntAsync(context, "SELECT CONNECTION_ID();");

        Assert.NotEqual(originalConnectionId, replacementConnectionId);
        Assert.Equal(1, await ContextScalarIntAsync(context, "SELECT @safe_migrations_cleanup_probe IS NULL;"));

        await MySqlConnection.ClearAllPoolsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GuardPlan_RunsWithLeastPrivilegeWithoutCreateRoutine()
    {
        var rootConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(rootConnectionString, "CREATE TABLE `least_privilege_target` (`id` int NOT NULL);");
        var connectionString = await Fixture.CreateLeastPrivilegeConnectionStringAsync(
            rootConnectionString,
            CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("guarded_value", "least_privilege_target", type: "int", nullable: true);

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        Assert.DoesNotContain(
            commands,
            command => command.CommandText.Contains("ROUTINE", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase));
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                rootConnectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'least_privilege_target' "
                + "AND COLUMN_NAME = 'guarded_value';"));
    }

    private static object ProviderGuidValue(
        DokaMySqlGuidFormat format,
        Guid value
    ) => format switch
    {
        DokaMySqlGuidFormat.Binary16 => value,
        DokaMySqlGuidFormat.Char36 => value.ToString("D"),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported GUID format."),
    };
}
