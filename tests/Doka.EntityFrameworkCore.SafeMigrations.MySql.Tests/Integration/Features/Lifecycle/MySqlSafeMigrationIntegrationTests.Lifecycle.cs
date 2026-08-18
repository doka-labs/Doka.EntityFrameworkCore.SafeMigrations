namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task StrictMismatch_FailsWithStableCategoryAndNextOperationRecoversSameSession()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
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
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, mismatch.Operations));
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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `sql_mode_guard` ("
            + "`id` int NOT NULL, `name` varchar(20) NULL, `quantity` int NOT NULL); "
            + "INSERT INTO `sql_mode_guard` VALUES (1, 'legacy', -1);");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        var sessionConfiguration = "SET SESSION sql_mode = CONCAT_WS(',', @@SESSION.sql_mode, 'NO_BACKSLASH_ESCAPES');";
        if (Fixture.IsMariaDb)
        {
            sessionConfiguration += " SET SESSION check_constraint_checks = OFF;";
        }

        await context.Database.ExecuteSqlRawAsync(sessionConfiguration);

        var mismatch = new MigrationBuilder(context.Database.ProviderName!);
        mismatch.AddColumnIfNotExists<string>(
            "name",
            "sql_mode_guard",
            type: "varchar(100)",
            nullable: true,
            policy: SafeMigrationPolicy.ThrowIfDifferent);
        var mismatchException =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, mismatch.Operations));
        Assert.Contains("doka_sm_different", mismatchException.Message, StringComparison.OrdinalIgnoreCase);

        var blockedCheck = new MigrationBuilder(context.Database.ProviderName!);
        blockedCheck.AddCheckConstraintIfNotExists("ck_sql_mode_guard_quantity", "sql_mode_guard", "quantity >= 0");
        var blockedException =
            await Assert.ThrowsAnyAsync<Exception>(() => ExecuteOperationsAsync(context, blockedCheck.Operations));
        Assert.Contains("doka_sm_data_blocked", blockedException.Message, StringComparison.OrdinalIgnoreCase);

        var recovery = new MigrationBuilder(context.Database.ProviderName!);
        recovery.AddColumnIfNotExists<string>(
            "note",
            "sql_mode_guard",
            type: "varchar(40)",
            nullable: true,
            comment: "mode\\safe");
        await ExecuteOperationsAsync(context, recovery.Operations);
        await context.Database.ExecuteSqlRawAsync("UPDATE `sql_mode_guard` SET `quantity` = 0 WHERE `quantity` < 0;");
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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString, registerSafeMigrations: false);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "must_not_exist",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });

        var generator = context.GetService<IMigrationsSqlGenerator>();
        var exception = Assert.ThrowsAny<Exception>(() => generator.Generate(builder.Operations, context.Model));
        Assert.Contains("handler", exception.Message, StringComparison.OrdinalIgnoreCase);
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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `pipeline_state` (`id` int NOT NULL, PRIMARY KEY (`id`));");
        await using var context = CreateContext(connectionString);

        await context.Database.MigrateAsync();
        await context
            .GetService<IMigrator>()
            .MigrateAsync();

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
        var generationConnectionString = await Fixture.CreateDatabaseAsync();
        await using var generationContext = CreateContext(generationConnectionString);
        var migrator = generationContext.GetService<IMigrator>();
        var scripts = new[]
        {
            migrator.GenerateScript(),
            migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent),
            migrator.GenerateScript(
                options: MigrationsSqlGenerationOptions.Idempotent | MigrationsSqlGenerationOptions.NoTransactions),
        };

        foreach (var script in scripts)
        {
            Assert.Contains(CoreConvergenceMigration.MigrationIdentifier, script, StringComparison.Ordinal);
            var connectionString = await Fixture.CreateDatabaseAsync();
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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString, registerSafeMigrations: false);

        await Assert.ThrowsAnyAsync<Exception>(() => context.Database.MigrateAsync());

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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE VIEW `pipeline_state` AS SELECT 1 AS `id`;");
        await using var context = CreateContext(connectionString);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.MigrateAsync());

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
        await context.Database.MigrateAsync();

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
        var connectionString = await Fixture.CreateDatabaseAsync();
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
        await context.Database.MigrateAsync();

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
        var connectionString = await Fixture.CreateDatabaseAsync();
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
            await Assert.ThrowsAsync<MySqlMigrationOperationHandlerException>(() => context.Database.MigrateAsync());

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
        var sharedConnectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            sharedConnectionString,
            "CREATE TABLE `__LegacyMigrationsHistory` (`MigrationId` varchar(150) NOT NULL); "
            + "INSERT INTO `__LegacyMigrationsHistory` VALUES ('legacy-unchanged');");
        await using var first = CreateContext(sharedConnectionString);
        await using var second = CreateContext(sharedConnectionString);
        await Task.WhenAll(
            first.Database.MigrateAsync(),
            second
                .GetService<IMigrator>()
                .MigrateAsync());

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

        var leftConnectionString = await Fixture.CreateDatabaseAsync();
        var rightConnectionString = await Fixture.CreateDatabaseAsync();
        await using var left = CreateContext(leftConnectionString);
        await using var right = CreateContext(rightConnectionString);
        await Task.WhenAll(left.Database.MigrateAsync(), right.Database.MigrateAsync());
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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "analysis_target",
            table => new { Id = table.Column<int>(type: "int", nullable: false) },
            mode: SafeMigrationTableMode.ConvergenceContainer,
            policy: SafeMigrationPolicy.ExistenceOnly);
        var runner = context.GetService<ISafeMigrationRunner>();
        var fingerprint = SafeMigrationModelFingerprint.Create(context.Model);
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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzePendingMigrationsAsync(context, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Contains(
            report.Assessments,
            assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureTable
                && assessment.ObservedState == SafeMigrationObservedState.Missing);
        Assert.Contains(
            report.Assessments,
            assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureColumn
                && assessment.ObservedState == SafeMigrationObservedState.Missing
                && assessment.Code == "projected_missing");
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';"));
    }

    [Fact]
    public async Task Preflight_RejectsWrongCanonicalModelFingerprintBeforeCatalogAccess()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();

        await Assert.ThrowsAsync<SafeMigrationModelMismatchException>(() => runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions("test-instance", expectedModelFingerprint: new string('0', 64))));
    }

    [Fact]
    public async Task Preflight_RejectsASchemaChangingDerivedContextAgainstTheMigrationSnapshot()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = new SchemaChangingDerivedContext(connectionString, Fixture.ServerVersion);

        await Assert.ThrowsAsync<SafeMigrationModelMismatchException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("test-instance")));
    }

    [Fact]
    public async Task Preflight_ReportsButDoesNotDeleteUnexpectedLegacyObjects()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
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
            value => value.ObjectKind == SafeMigrationDatabaseObjectKind.Column
                && value.Table == "inventory_target"
                && value.Name == "legacy_column");
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
        var connectionString = await Fixture.CreateDatabaseAsync();
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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("missing_table", "renamed_table");
        builder.RenameColumnIfExists("missing_column", "missing_table", "renamed_column");
        builder.RenameIndexIfExists("missing_index", "missing_table", "renamed_index");

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('missing_table', 'renamed_table');"));
    }

    [Fact]
    public async Task GuardPlan_RecoversAfterEveryCommandBoundaryAndPooledConnectionReset()
    {
        var templateConnectionString = await Fixture.CreateDatabaseAsync();
        await using var templateContext = CreateContext(templateConnectionString);
        var templateBuilder = new MigrationBuilder(templateContext.Database.ProviderName!);
        templateBuilder.AddColumnIfNotExists<int>("guarded_value", "fault_target", type: "int", nullable: true);
        var generator = templateContext.GetService<IMigrationsSqlGenerator>();
        var commandCount = generator.Generate(templateBuilder.Operations, templateContext.Model)
            .Count;
        Assert.True(commandCount > 10);

        for (var boundary = 1; boundary <= commandCount; boundary++)
        {
            var connectionString = await Fixture.CreateDatabaseAsync();
            await ExecuteSqlAsync(connectionString, "CREATE TABLE `fault_target` (`id` int NOT NULL);");
            await using (var interruptedContext = CreateContext(connectionString))
            {
                var builder = new MigrationBuilder(interruptedContext.Database.ProviderName!);
                builder.AddColumnIfNotExists<int>("guarded_value", "fault_target", type: "int", nullable: true);
                var commands = interruptedContext
                    .GetService<IMigrationsSqlGenerator>()
                    .Generate(builder.Operations, interruptedContext.Model);
                var connection = interruptedContext.Database.GetDbConnection();
                await connection.OpenAsync();
                for (var index = 0; index < boundary; index++)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = commands[index].CommandText;
                    await command.ExecuteNonQueryAsync();
                }

                await connection.CloseAsync();
            }

            await using var retryContext = CreateContext(connectionString);
            var retry = new MigrationBuilder(retryContext.Database.ProviderName!);
            retry.AddColumnIfNotExists<int>("guarded_value", "fault_target", type: "int", nullable: true);
            await ExecuteOperationsAsync(retryContext, retry.Operations);
            await ExecuteOperationsAsync(retryContext, retry.Operations);
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                    + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'fault_target' "
                    + "AND COLUMN_NAME = 'guarded_value';"));
        }
    }

    [Fact]
    public async Task GuardPlan_RunsWithLeastPrivilegeWithoutCreateRoutine()
    {
        var rootConnectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(rootConnectionString, "CREATE TABLE `least_privilege_target` (`id` int NOT NULL);");
        var connectionString = await Fixture.CreateLeastPrivilegeConnectionStringAsync(rootConnectionString);
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
}
