namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the Lifecycle migration contracts in an independent class collection.</summary>
public sealed partial class MySqlLifecycleIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlLifecycleIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>
    /// Preserves the stable mismatch category and allows a following operation to recover the same native session.
    /// </summary>
    [Fact]
    public async Task StrictMismatch_FailsWithStableCategoryAndNextOperationRecoversSameSession()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `accounts` (`id` int NOT NULL, `name` varchar(20) NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var mismatch = new MigrationBuilder(context.Database.ProviderName!);
        mismatch.AddColumnIfNotExists<string>(
            "name",
            "accounts",
            type: "varchar(100)",
            nullable: true,
            policy: SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var exception =
            await AssertOwnedGuardFailureAsync(
                operationToken => ExecuteOperationsAsync(
                    context,
                    mismatch.Operations,
                    operationToken),
                cancellationToken: CancellationToken.None);

        var valid = new MigrationBuilder(context.Database.ProviderName!);
        valid.AddColumnIfNotExists<string>("description", "accounts", type: "varchar(100)", nullable: true);
        await ExecuteOperationsAsync(context, valid.Operations, CancellationToken.None);

        var recoveredDescriptionColumns = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'accounts' AND COLUMN_NAME = 'description';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Contains("doka_sm_different", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, recoveredDescriptionColumns);
    }

    /// <summary>Keeps rejection guards and cleanup effective under the tested SQL-mode profiles.</summary>
    [Fact]
    public async Task SessionSqlModes_DoNotDisableAssertionsAndFailureRecovery()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `sql_mode_guard` (`id` int NOT NULL, `name` varchar(20) NULL,
            `quantity` int NOT NULL); INSERT INTO `sql_mode_guard` VALUES (1, 'legacy',
            -1);
            """,
            cancellationToken: CancellationToken.None);

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

        // Act
        var mismatchException =
            await AssertOwnedGuardFailureAsync(
                operationToken => ExecuteOperationsAsync(
                    context,
                    mismatch.Operations,
                    operationToken),
                cancellationToken: CancellationToken.None);

        var blockedCheck = new MigrationBuilder(context.Database.ProviderName!);
        blockedCheck.EnsureCheckConstraint(
            ExpectedCheckConstraintDefinition.FromExpression(
                "ck_sql_mode_guard_quantity",
                "sql_mode_guard",
                SqlColumnAndInt("quantity", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var blockedException =
            await AssertOwnedGuardFailureAsync(
                operationToken => ExecuteOperationsAsync(
                    context,
                    blockedCheck.Operations,
                    operationToken),
                cancellationToken: CancellationToken.None);

        var recovery = new MigrationBuilder(context.Database.ProviderName!);
        recovery.AddColumnIfNotExists<string>(
            "note",
            "sql_mode_guard",
            type: "varchar(40)",
            nullable: true,
            comment: "mode\\safe");

        await ExecuteOperationsAsync(context, recovery.Operations, CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE `sql_mode_guard` SET `quantity` = 0 WHERE `quantity` < 0;",
            CancellationToken.None);

        await ExecuteOperationsAsync(context, blockedCheck.Operations, CancellationToken.None);

        var storedComment = await ScalarStringAsync(
            connectionString,
            """
            SELECT COLUMN_COMMENT FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'sql_mode_guard'
            AND COLUMN_NAME = 'note';
            """,
            cancellationToken: CancellationToken.None);

        var recoveredCheckCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE()
            AND TABLE_NAME = 'sql_mode_guard'
            AND CONSTRAINT_NAME = 'ck_sql_mode_guard_quantity';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Contains("doka_sm_different", mismatchException.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("doka_sm_data_blocked", blockedException.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("mode\\safe", storedComment);

        Assert.Equal(1, recoveredCheckCount);
    }

    /// <summary>
    /// Fails generation with the exact missing-handler category rather than executing an unowned operation.
    /// </summary>
    [Fact]
    public async Task MissingHandler_FailsClosedDuringGeneration()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString, registerSafeMigrations: false);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "must_not_exist",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });

        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var failure = Record.Exception(() =>
            generator.Generate(builder.Operations, context.Model));

        // Assert
        var exception = Assert.IsType<MySqlMigrationOperationHandlerException>(failure);

        Assert.Equal(MySqlMigrationHandlerFailureCode.UnknownOperationType, exception.FailureCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'must_not_exist';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Applies mixed ordinary and guarded migrations before writing their successful history row.</summary>
    [Fact]
    public async Task Migrator_MixesStandardAndSafeOperationsAndWritesHistoryOnlyAfterSuccess()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `pipeline_state` (`id` int NOT NULL, PRIMARY KEY (`id`));",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);

        // Act
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        await context
            .GetService<IMigrator>()
            .MigrateAsync(cancellationToken: CancellationToken.None);

        // Assert
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

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `__ApplicationDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Executes generated normal, idempotent, and no-transaction scripts against the native catalog.</summary>
    /// <param name="scriptKind">The independently generated script execution mode.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task MigrationScripts_ExecuteNormalIdempotentAndNoTransactionPaths(
        int scriptKind
    )
    {
        // Arrange
        var generationConnectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var generationContext = CreateContext(generationConnectionString);
        var migrator = generationContext.GetService<IMigrator>();
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);

        // Act
        var script = scriptKind switch
        {
            0 => migrator.GenerateScript(),
            1 => migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent),
            2 => migrator.GenerateScript(
                options: MigrationsSqlGenerationOptions.Idempotent | MigrationsSqlGenerationOptions.NoTransactions),
            _ => throw new ArgumentOutOfRangeException(nameof(scriptKind)),
        };

        await ExecuteMigrationScriptAsync(connectionString, script, cancellationToken: CancellationToken.None);
        if (scriptKind != 0)
        {
            await ExecuteMigrationScriptAsync(connectionString, script, cancellationToken: CancellationToken.None);
        }

        // Assert
        Assert.Contains(CoreConvergenceMigration.MigrationIdentifier, script, StringComparison.Ordinal);
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

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `__ApplicationDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Writes neither target objects nor migration history when operation handling is unavailable.</summary>
    [Fact]
    public async Task Migrator_WithoutHandlerWritesNeitherTargetDdlNorHistoryRow()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString, registerSafeMigrations: false);

        // Act
        var failure = await Record.ExceptionAsync(() =>
            context.Database.MigrateAsync(cancellationToken: CancellationToken.None));

        // Assert
        var exception = Assert.IsType<MySqlMigrationOperationHandlerException>(failure);

        Assert.Equal(MySqlMigrationHandlerFailureCode.UnknownOperationType, exception.FailureCode);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = '__ApplicationDbContextMigrationsHistory';
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM `__ApplicationDbContextMigrationsHistory` "
                + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Retains forward-recovery semantics after ordinary DDL precedes a guarded runtime failure.</summary>
    [Fact]
    public async Task RuntimeGuardFailureAfterStandardDdl_RequiresForwardFixAndRemainsRetryable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE VIEW `pipeline_state` AS SELECT 1 AS `id`;",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);

        // Act
        var exception = await AssertOwnedGuardFailureAsync(
            operationToken =>
                context.Database.MigrateAsync(cancellationToken: operationToken),
            cancellationToken: CancellationToken.None);

        var probeTablesAfterFailure = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'pipeline_probe'
            AND TABLE_TYPE = 'BASE TABLE';
            """,
            cancellationToken: CancellationToken.None);

        var historyRowsAfterFailure = await ScalarIntAsync(
            connectionString,
            $"SELECT COUNT(*) FROM `__ApplicationDbContextMigrationsHistory` "
            + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';",
            cancellationToken: CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "DROP VIEW `pipeline_state`; DROP TABLE `pipeline_probe`;",
            cancellationToken: CancellationToken.None);

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        var historyRowsAfterRecovery = await ScalarIntAsync(
            connectionString,
            $"SELECT COUNT(*) FROM `__ApplicationDbContextMigrationsHistory` "
            + $"WHERE `MigrationId` = '{CoreConvergenceMigration.MigrationIdentifier}';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Contains("doka_sm_unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, probeTablesAfterFailure);

        Assert.Equal(0, historyRowsAfterFailure);

        Assert.Equal(1, historyRowsAfterRecovery);
    }

    /// <summary>
    /// Resolves the additive handler and runner through an explicitly supplied internal service provider.
    /// </summary>
    [Fact]
    public async Task ExternalInternalServiceProvider_ResolvesTheAdditiveHandlerAndRunner()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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
                    .MigrationsHistoryTable("__ApplicationDbContextMigrationsHistory"))
            .UseMySqlSafeMigrations()
            .Options;

        await using var context = new SafeMigrationDbContext(options);

        // Act
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        var migratedPayloadColumns = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'pipeline_state'
            AND COLUMN_NAME = 'payload';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.NotNull(context.GetService<ISafeMigrationRunner>());

        Assert.Equal(1, migratedPayloadColumns);
    }

    /// <summary>Rejects duplicate operation ownership before target DDL or migration history mutation.</summary>
    [Fact]
    public async Task ConflictingHandler_FailsBeforeTargetDdlAndHistory()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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
                    .MigrationsHistoryTable("__ApplicationDbContextMigrationsHistory"))
            .UseMySqlSafeMigrations()
            .Options;

        await using var context = new SafeMigrationDbContext(options);

        // Act
        var failure =
            await Record.ExceptionAsync(() =>
                context.Database.MigrateAsync(cancellationToken: CancellationToken.None));

        // Assert
        var exception = Assert.IsType<MySqlMigrationOperationHandlerException>(failure);

        Assert.Equal(MySqlMigrationHandlerFailureCode.DuplicateOperationOwnership, exception.FailureCode);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';",
                cancellationToken: CancellationToken.None));

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

    /// <summary>Keeps analysis and verification read-only while preserving runtime classification semantics.</summary>
    [Fact]
    public async Task PreflightAndPostflight_AreReadOnlyAndUseRuntimeClassification()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations, runOptions, CancellationToken.None);
        var targetsAfterPreflight = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
            + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'analysis_target';",
            cancellationToken: CancellationToken.None);

        var historiesAfterPreflight = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = '__ApplicationDbContextMigrationsHistory';
            """,
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations, runOptions, CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationRunReport.CurrentSchemaVersion, preflight.SchemaVersion);
        Assert.Equal("test-instance", preflight.InstanceId);
        Assert.Equal(
            Fixture.IsMariaDb
                ? "mariadb"
                : "mysql",
            preflight.Environment.EngineFamily);

        Assert.NotEmpty(preflight.Environment.ServerVersion);
        Assert.Equal(64, preflight.ContractFingerprint.Length);
        Assert.Equal(SafeMigrationObservedState.Missing,
            Assert.Single(preflight.Assessments)
                .ObservedState);

        Assert.Equal(0, targetsAfterPreflight);
        Assert.Equal(0, historiesAfterPreflight);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(
            Assert.Single(postflight.Assessments)
                .PostconditionSatisfied);
    }

    /// <summary>
    /// Projects the full missing convergence table rather than rejecting its dependent initial operations.
    /// </summary>
    [Fact]
    public async Task PendingPreflight_ProjectsACompleteMissingConvergenceTable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzePendingMigrationsAsync(
                context,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Contains(
            report.Assessments,
            assessment => assessment is
            {
                OperationKind: SafeMigrationOperationKind.EnsureTable,
                ObservedState: SafeMigrationObservedState.Missing,
            });

        Assert.Contains(
            report.Assessments,
            assessment => assessment is
            {
                OperationKind: SafeMigrationOperationKind.EnsureColumn,
                ObservedState: SafeMigrationObservedState.Missing,
                Code: "projected_missing",
            });

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'pipeline_state';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Uses an ordinary provider column addition as a prerequisite for a following guarded index.</summary>
    [Fact]
    public async Task PreflightProjectsProviderAddColumnForFollowingSafeIndex()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `mixed_customers` (`id` int NOT NULL, CONSTRAINT `pk_mixed_customers`
            PRIMARY KEY (`id`));CREATE TABLE `mixed_evolution` (`id` int NOT NULL,
            CONSTRAINT `pk_mixed_evolution` PRIMARY KEY (`id`));INSERT INTO `mixed_customers`
            (`id`) VALUES (7);INSERT INTO `mixed_evolution` (`id`) VALUES (1);
            """,
            cancellationToken: CancellationToken.None);

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

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations, options, CancellationToken.None);
        var projectedIndex = preflight.Assessments.Single(static assessment => assessment.IsSafeOperation);

        var columnsAfterPreflight = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'mixed_evolution'
            AND COLUMN_NAME = 'customer_id';
            """,
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(context, builder.Operations, options, CancellationToken.None);
        var replay = await runner.AnalyzeAsync(context, builder.Operations, options, CancellationToken.None);
        var postflightIndex = postflight.Assessments.Single(static assessment => assessment.IsSafeOperation);
        var replayIndex = replay.Assessments.Single(static assessment => assessment.IsSafeOperation);

        var storedCustomerId = await ScalarIntAsync(
            connectionString,
            "SELECT `customer_id` FROM `mixed_evolution`;",
            cancellationToken: CancellationToken.None);

        var foreignKeyCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = DATABASE()
            AND CONSTRAINT_NAME = 'fk_mixed_evolution_customers_customer_id';
            """,
            cancellationToken: CancellationToken.None);

        var columnsWithoutDefault = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'mixed_evolution'
            AND COLUMN_NAME = 'customer_id'
            AND COLUMN_DEFAULT IS NULL;
            """,
            cancellationToken: CancellationToken.None);

        var supportIndexCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'mixed_evolution'
            AND INDEX_NAME = 'ix_mixed_evolution_customer_id';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(5, preflight.Assessments.Count);
        Assert.All(
            preflight.Assessments.Where(static assessment => !assessment.IsSafeOperation),
            static assessment => Assert.Equal("provider_owned_not_analyzed", assessment.Code));

        Assert.Equal(SafeMigrationObservedState.Missing, projectedIndex.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, projectedIndex.Action);
        Assert.Equal("projected_missing", projectedIndex.Code);
        Assert.Equal(0, columnsAfterPreflight);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, postflightIndex.ObservedState);
        Assert.True(postflightIndex.PostconditionSatisfied);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, replayIndex.Action);
        Assert.Equal(7, storedCustomerId);

        Assert.Equal(1, foreignKeyCount);

        Assert.Equal(1, columnsWithoutDefault);

        Assert.Equal(1, supportIndexCount);
    }

    /// <summary>
    /// Preserves structural prerequisites across provider seed operations without claiming to analyze their data
    /// effects.
    /// </summary>
    [Fact]
    public async Task PreflightPreservesSafeStructureAcrossProviderSeedData()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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
            values: new object[,] { { 1, 7 } });

        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_seed_projection_group_id",
            "seed_projection",
            "GroupId");

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("provider-seed-projection");

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);

        var seededRowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM `seed_projection`;",
            cancellationToken: CancellationToken.None);

        var supportIndexCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'seed_projection'
            AND INDEX_NAME = 'ix_seed_projection_group_id';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, preflight.Status);
        Assert.Equal(3, preflight.Assessments.Count);
        Assert.Equal("provider_owned_not_analyzed", preflight.Assessments[1].Code);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[2].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Equal("projected_missing", preflight.Assessments[2].Code);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, replay.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.NoOp, replay.Assessments[2].Action);
        Assert.Equal(1, seededRowCount);

        Assert.Equal(1, supportIndexCount);
    }

    /// <summary>
    /// Preserves GUID values and referential cascade behavior across the provider seed migration journey.
    /// </summary>
    /// <param name="mode">The explicitly selected strict or legacy-convergence scaffolding mode.</param>
    /// <param name="guidFormat">The native GUID storage-format annotation under test.</param>
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
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var storeType = GuidStoreType(guidFormat);
        var maxLength = guidFormat == DokaMySqlGuidFormat.Binary16
            ? 16
            : 36;

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
            values: new[,] { { 1, publicSeedValue, optionalSeedValue } });

        _ = builder.InsertData(
            table: "seed_journey_dependents",
            columns: ["id", "principal_id"],
            columnTypes: ["int", storeType],
            values: new[,] { { 1, principalSeedValue } });

        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_seed_journey_dependents_principal_id",
            "seed_journey_dependents",
            "principal_id");

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions($"provider-seed-journey-{mode}-{guidFormat}");

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            options,
            CancellationToken.None);

        var providerAssessments = preflight
            .Assessments
            .Where(static assessment => !assessment.IsSafeOperation)

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        var indexAssessment = preflight.Assessments.Single(assessment => assessment is
        {
            OperationKind: SafeMigrationOperationKind.EnsureIndex,
            ObjectName: "ix_seed_journey_dependents_principal_id",
        });

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var storedPrincipalId = guidFormat == DokaMySqlGuidFormat.Binary16
            ? await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(HEX(`id`)) FROM `seed_journey_principals`;",
                cancellationToken: CancellationToken.None)
            : await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(`id`) FROM `seed_journey_principals`;",
                cancellationToken: CancellationToken.None);

        var storedPublicId = guidFormat == DokaMySqlGuidFormat.Binary16
            ? await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(HEX(`public_id`)) FROM `seed_journey_scalars`;",
                cancellationToken: CancellationToken.None)
            : await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(`public_id`) FROM `seed_journey_scalars`;",
                cancellationToken: CancellationToken.None);

        var storedOptionalGuid = guidFormat == DokaMySqlGuidFormat.Binary16
            ? await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(HEX(`optional_guid`)) FROM `seed_journey_scalars`;",
                cancellationToken: CancellationToken.None)
            : await ScalarStringAsync(
                connectionString,
                "SELECT LOWER(`optional_guid`) FROM `seed_journey_scalars`;",
                cancellationToken: CancellationToken.None);

        var expectedFormat = guidFormat == DokaMySqlGuidFormat.Binary16
            ? "N"
            : "D";

        var joinedRowsBeforeCascade = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM `seed_journey_dependents` AS `dependent`
            INNER JOIN `seed_journey_principals` AS `principal` ON `dependent`.`principal_id`
            = `principal`.`id`;
            """,
            cancellationToken: CancellationToken.None);

        var supportIndexesBeforeCascade = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(DISTINCT INDEX_NAME)
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'seed_journey_dependents'
            AND COLUMN_NAME = 'principal_id'
            AND SEQ_IN_INDEX = 1;
            """,
            cancellationToken: CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "DELETE FROM `seed_journey_principals`;",
            cancellationToken: CancellationToken.None);

        var dependentsAfterCascade = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM `seed_journey_dependents`;",
            cancellationToken: CancellationToken.None);

        var scalarsAfterCascade = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM `seed_journey_scalars`;",
            cancellationToken: CancellationToken.None);

        // Assert
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

        Assert.Equal(principalId.ToString(expectedFormat), storedPrincipalId);
        Assert.Equal(publicId.ToString(expectedFormat), storedPublicId);
        Assert.Equal(optionalGuid.ToString(expectedFormat), storedOptionalGuid);
        Assert.Equal(1, joinedRowsBeforeCascade);

        Assert.Equal(1, supportIndexesBeforeCascade);

        Assert.Equal(0, dependentsAfterCascade);

        Assert.Equal(1, scalarsAfterCascade);
    }

    /// <summary>Does not carry an earlier uniqueness proof across provider-owned seed mutations.</summary>
    [Fact]
    public async Task PreflightDoesNotInferUniqueSafetyAcrossProviderSeedData()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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
            values: new object[,] { { 1, 7 }, { 2, 7 } });

        _ = builder.CreateIndexIfNotExistsFromModel(
            "ux_seed_unique_projection_group_id",
            "seed_unique_projection",
            "GroupId",
            unique: true);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("provider-seed-unique-projection"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[2].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[2].Action);
        Assert.Equal("prerequisite_missing", report.Assessments[2].Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'seed_unique_projection';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Retains a live nonunique index's structural prerequisites across provider seed operations.</summary>
    [Fact]
    public async Task PreflightPreservesLiveNonUniqueIndexAcrossProviderSeedData()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `existing_seed_projection` (`Id` int NOT NULL, `GroupId` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.InsertData(
            table: "existing_seed_projection",
            columns: ["Id", "GroupId"],
            columnTypes: ["int", "int"],
            values: new object[,] { { 1, 7 } });

        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_existing_seed_projection_group_id",
            "existing_seed_projection",
            "GroupId");

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("provider-existing-seed-projection"),
                CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var seededRowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM `existing_seed_projection`;",
            cancellationToken: CancellationToken.None);

        var preservedIndexCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'existing_seed_projection'
            AND INDEX_NAME = 'ix_existing_seed_projection_group_id';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);

        Assert.Equal(1, seededRowCount);

        Assert.Equal(1, preservedIndexCount);
    }

    /// <summary>Invalidates uniqueness evidence after a provider-owned seed operation may change its rows.</summary>
    [Fact]
    public async Task PreflightInvalidatesLiveUniqueIndexSafetyAcrossProviderSeedData()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `existing_seed_unique_projection` (`Id` int NOT NULL, `GroupId` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.InsertData(
            table: "existing_seed_unique_projection",
            columns: ["Id", "GroupId"],
            columnTypes: ["int", "int"],
            values: new object[,] { { 1, 7 }, { 2, 7 } });

        _ = builder.CreateIndexIfNotExistsFromModel(
            "ux_existing_seed_unique_projection_group_id",
            "existing_seed_unique_projection",
            "GroupId",
            unique: true);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("provider-existing-seed-unique-projection"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, report.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.Equal("prerequisite_missing", report.Assessments[1].Code);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM `existing_seed_unique_projection`;",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'existing_seed_unique_projection'
                AND INDEX_NAME = 'ux_existing_seed_unique_projection_group_id';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Rejects an incorrect canonical fingerprint before any catalog interaction.</summary>
    [Fact]
    public async Task Preflight_RejectsWrongCanonicalModelFingerprintBeforeCatalogAccess()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var failure = await Record.ExceptionAsync(() => runner.AnalyzeAsync(
            context,
            [],
            new SafeMigrationRunOptions(
                "test-instance",
                expectedModelFingerprint: "safe-relational-model:v1:Doka.EntityFrameworkCore.MySql:sha256:"
                + new string('0', 64)),
            CancellationToken.None));

        // Assert
        Assert.IsType<SafeMigrationModelMismatchException>(failure);
    }

    /// <summary>Rejects a schema-changing derived model against the canonical migration snapshot.</summary>
    [Fact]
    public async Task Preflight_RejectsASchemaChangingDerivedContextAgainstTheMigrationSnapshot()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = new SchemaChangingDerivedContext(connectionString, Fixture.ServerVersion);

        // Act
        var failure = await Record.ExceptionAsync(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("test-instance"), CancellationToken.None));

        // Assert
        Assert.IsType<SafeMigrationModelMismatchException>(failure);
    }

    /// <summary>Includes unexpected legacy objects in diagnostics without removing their native state.</summary>
    [Fact]
    public async Task Preflight_ReportsButDoesNotDeleteUnexpectedLegacyObjects()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `inventory_target` (`id` int NOT NULL, `legacy_column` int
            NULL); CREATE INDEX `ix_inventory_legacy` ON `inventory_target` (`legacy_column`);
            CREATE TABLE `legacy_extra` (`id` int NOT NULL);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "inventory_target",
            table => new { Id = table.Column<int>(type: "int", nullable: false) },
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

        // Assert
        Assert.Contains(
            report.UnexpectedObjects,
            value => value is { ObjectKind: SafeMigrationDatabaseObjectKind.Table, Name: "legacy_extra" });

        Assert.Contains(
            report.UnexpectedObjects,
            value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.Column, Table: "inventory_target", Name: "legacy_column",
            });

        Assert.Contains(
            report.UnexpectedObjects,
            value => value is { ObjectKind: SafeMigrationDatabaseObjectKind.Index, Name: "ix_inventory_legacy" });

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'legacy_extra';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Applies and replays the column, table, and index lifecycle without duplicate native objects.</summary>
    [Fact]
    public async Task ColumnTableIndexLifecycle_IsIdempotentAcrossEveryOperationFamily()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `lifecycle` (`id` int NOT NULL, `old_name` varchar(40) NULL);
            CREATE INDEX `ix_lifecycle_old_name` ON `lifecycle` (`old_name`);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);

        var rename = new MigrationBuilder(context.Database.ProviderName!);
        rename.RenameColumnIfExists("old_name", "lifecycle", "name");
        rename.RenameIndexIfExists("ix_lifecycle_old_name", "lifecycle", "ix_lifecycle_name");

        // Act
        await ExecuteOperationsAsync(context, rename.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, rename.Operations, CancellationToken.None);

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
        await ExecuteOperationsAsync(context, alter.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, alter.Operations, CancellationToken.None);

        var drop = new MigrationBuilder(context.Database.ProviderName!);
        drop.DropIndexIfExists("ix_lifecycle_name", "lifecycle");
        drop.DropColumnIfExists("name", "lifecycle");
        drop.RenameTableIfExists("lifecycle", "renamed_lifecycle");
        await ExecuteOperationsAsync(context, drop.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, drop.Operations, CancellationToken.None);

        var dropTable = new MigrationBuilder(context.Database.ProviderName!);
        dropTable.DropTableIfExists("renamed_lifecycle");
        await ExecuteOperationsAsync(context, dropTable.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, dropTable.Operations, CancellationToken.None);

        // Assert
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'renamed_lifecycle';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>
    /// Treats an absent rename source as an idempotent no-op rather than inventing a replacement source.
    /// </summary>
    [Fact]
    public async Task MissingRenameSources_AreIdempotentNoOps()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.RenameTableIfExists("missing_table", "renamed_table");
        builder.RenameColumnIfExists("missing_column", "missing_table", "renamed_column");
        builder.RenameIndexIfExists("missing_index", "missing_table", "renamed_index");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Assert
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME IN ('missing_table', 'renamed_table');
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Cleans native guard resources after a body failure and retries on the same physical session.</summary>
    [Fact]
    public async Task GuardPlan_FailedBodyCleansSameSessionAndRemainsRetryable()
    {
        // Arrange
        var databaseConnectionString = await CreateDatabaseAsync(CancellationToken.None);
        var connectionString = new MySqlConnectionStringBuilder(databaseConnectionString)
        {
            Pooling = true,
            ConnectionReset = false,
            MaximumPoolSize = 1,
        }.ConnectionString;

        TrackOwnedPool(connectionString);

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `fault_target` (`id` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var physicalConnectionId = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: CancellationToken.None);

        var invalid = new MigrationBuilder(context.Database.ProviderName!);
        invalid.AddColumnIfNotExists<string>("guarded_value", "fault_target", type: "varchar(70000)", nullable: true);

        // Act
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            invalid.Operations,
            CancellationToken.None));

        var connectionIdAfterFailure = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: CancellationToken.None);

        var cleanVariablesAfterFailure = await ContextScalarIntAsync(
            context,
            """
            SELECT @doka_sm_state IS NULL
            AND @doka_sm_action IS NULL AND @doka_sm_repair_ok IS NULL
            AND @doka_sm_prerequisite_ok IS NULL
            AND @doka_sm_sql IS NULL AND @doka_sm_post_ok IS NULL;
            """,
            cancellationToken: CancellationToken.None);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TEMPORARY TABLE `__doka_sm_assert` (`value` int NOT NULL); "
            + "DROP TEMPORARY TABLE `__doka_sm_assert`;",
            CancellationToken.None);

        var retry = new MigrationBuilder(context.Database.ProviderName!);
        retry.AddColumnIfNotExists<string>("guarded_value", "fault_target", type: "varchar(40)", nullable: true);

        await ExecuteOperationsAsync(context, retry.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, retry.Operations, CancellationToken.None);

        var recoveredColumnCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'fault_target'
            AND COLUMN_NAME = 'guarded_value';
            """,
            cancellationToken: CancellationToken.None);

        await MySqlConnection.ClearPoolAsync((MySqlConnection)context.Database.GetDbConnection(),
            CancellationToken.None);

        // Assert
        Assert.IsType<MySqlException>(failure);
        Assert.Equal(physicalConnectionId, connectionIdAfterFailure);

        Assert.Equal(1, cleanVariablesAfterFailure);

        Assert.Equal(1, recoveredColumnCount);
    }

    /// <summary>
    /// Cancels an actually dispatched guarded body, cleans its resources, and permits same-session recovery.
    /// </summary>
    [Fact]
    public async Task GuardPlan_CancelledBodyCleansSameSessionAndRemainsRetryable()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `cancel_target` (`id` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var blocker = new MySqlConnection(connectionString);
        await blocker.OpenAsync(CancellationToken.None);
        await using var lockCommand = blocker.CreateCommand();
        lockCommand.CommandText = "LOCK TABLES `cancel_target` WRITE;";
        await lockCommand.ExecuteNonQueryAsync(CancellationToken.None);

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var physicalConnectionId = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: CancellationToken.None);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("guarded_value", "cancel_target", type: "int", nullable: true);
        using var cancellation = new CancellationTokenSource();

        Exception? exception;

        // Act
        try
        {
            exception = await MySqlNativeCancellationCheckpoint.CancelWhenDispatchedAsync(
                connectionString,
                ((MySqlConnection)context.Database.GetDbConnection()).ServerThread,
                "ALTER TABLE `cancel_target`",
                cancellation,
                Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations, cancellation.Token)),
                CancellationToken.None);
        }
        finally
        {
            await using var unlockCommand = blocker.CreateCommand();
            unlockCommand.CommandText = "UNLOCK TABLES;";
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var connectionIdAfterCancellation = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: CancellationToken.None);

        var cleanVariablesAfterCancellation = await ContextScalarIntAsync(
            context,
            """
            SELECT @doka_sm_state IS NULL
            AND @doka_sm_action IS NULL AND @doka_sm_repair_ok IS NULL
            AND @doka_sm_prerequisite_ok IS NULL
            AND @doka_sm_sql IS NULL AND @doka_sm_post_ok IS NULL;
            """,
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var recoveredColumnCount = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'cancel_target'
            AND COLUMN_NAME = 'guarded_value';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(physicalConnectionId, connectionIdAfterCancellation);

        Assert.Equal(1, cleanVariablesAfterCancellation);

        Assert.Equal(1, recoveredColumnCount);
    }

    /// <summary>
    /// Evicts a session whose scoped cleanup failed instead of returning contaminated state to the pool.
    /// </summary>
    [Fact]
    public async Task ScopedCommand_CleanupFailureEvictsThePhysicalSession()
    {
        // Arrange
        var databaseConnectionString = await CreateDatabaseAsync(CancellationToken.None);
        var connectionString = new MySqlConnectionStringBuilder(databaseConnectionString)
        {
            Pooling = true,
            ConnectionReset = false,
            MaximumPoolSize = 1,
        }.ConnectionString;

        TrackOwnedPool(connectionString);

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
        var originalConnectionId = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: CancellationToken.None);

        var command = Assert.Single(
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate([new CleanupFailureOperation()], context.Model));

        // Act
        var exception = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync(
            context.GetService<IRelationalConnection>(),
            cancellationToken: CancellationToken.None));

        var connectionStateAfterCleanup = context.Database.GetDbConnection()
            .State;

        await context.Database.OpenConnectionAsync(CancellationToken.None);
        var replacementConnectionId = await ContextScalarIntAsync(
            context,
            "SELECT CONNECTION_ID();",
            cancellationToken: CancellationToken.None);

        var cleanVariablesOnReplacement = await ContextScalarIntAsync(
            context,
            "SELECT @safe_migrations_cleanup_probe IS NULL;",
            cancellationToken: CancellationToken.None);

        await MySqlConnection.ClearPoolAsync((MySqlConnection)context.Database.GetDbConnection(),
            CancellationToken.None);

        // Assert
        var cleanupException = Assert.IsType<InvalidOperationException>(exception, exactMatch: false);

        Assert.Equal(
            "MySqlMigrationSessionCleanupException",
            cleanupException.GetType()
                .Name);

        Assert.Equal(ConnectionState.Closed, connectionStateAfterCleanup);

        Assert.NotEqual(originalConnectionId, replacementConnectionId);
        Assert.Equal(1, cleanVariablesOnReplacement);
    }

    /// <summary>Runs guarded migrations without requiring stored-routine creation privileges.</summary>
    [Fact]
    public async Task GuardPlan_RunsWithLeastPrivilegeWithoutCreateRoutine()
    {
        // Arrange
        var rootConnectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            rootConnectionString,
            "CREATE TABLE `least_privilege_target` (`id` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        var connectionString = await Fixture.CreateLeastPrivilegeConnectionStringAsync(
            rootConnectionString,
            CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("guarded_value", "least_privilege_target", type: "int", nullable: true);

        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var guardedColumnCount = await ScalarIntAsync(
            rootConnectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'least_privilege_target'
            AND COLUMN_NAME = 'guarded_value';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.DoesNotContain(
            commands,
            command => command.CommandText.Contains("ROUTINE", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, guardedColumnCount);
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
