namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task StrictMismatch_FailsWithStableSqlState()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE accounts (id integer NOT NULL, name character varying(20) NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>("name", "accounts", type: "character varying(100)", nullable: true);

        var exception =
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context, builder.Operations));

        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal("doka_sm_different", exception.MessageText);
    }

    [Fact]
    public async Task MissingAdapter_FailsClosedDuringGeneration()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString, registerSafeMigrations: false);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "must_not_exist",
            table => new { Id = table.Column<int>(type: "integer", nullable: false) });
        var generator = context.GetService<IMigrationsSqlGenerator>();

        Assert.Throws<InvalidOperationException>(() => generator.Generate(builder.Operations, context.Model));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'must_not_exist';"));
    }

    [Fact]
    public async Task Migrator_MixesStandardAndSafeOperationsAndWritesOneHistoryRow()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE pipeline_state (id integer NOT NULL PRIMARY KEY);");
        await using var context = CreateContext(connectionString);

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        await context
            .GetService<IMigrator>()
            .MigrateAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'pipeline_state' "
                + "AND column_name = 'payload';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM \"__CoreDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';"));
    }

    [Fact]
    public async Task RuntimeGuardFailureAfterStandardDdl_RollsBackAndRetriesWithoutHistoryDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE VIEW pipeline_state AS SELECT 1 AS id;");
        await using var context = CreateContext(connectionString);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync(cancellationToken: CancellationToken.None));

        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'pipeline_probe';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM \"__CoreDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';"));

        await ExecuteSqlAsync(connectionString, "DROP VIEW pipeline_state;");
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM \"__CoreDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';"));
    }

    [Fact]
    public async Task ExternalInternalServiceProvider_ResolvesTheComposedAdapterAndRunner()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var services = new ServiceCollection();
        services.AddEntityFrameworkNpgsql();
        services.AddPostgreSqlSafeMigrations();
        await using var serviceProvider = services.BuildServiceProvider(validateScopes: true);
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>()
            .UseInternalServiceProvider(serviceProvider)
            .UseNpgsql(
                connectionString,
                provider => provider
                    .MigrationsAssembly(typeof(SafeMigrationDbContext).Assembly.FullName)
                    .MigrationsHistoryTable("__CoreDbContextMigrationsHistory"))
            .UsePostgreSqlSafeMigrations()
            .Options;

        await using var context = new SafeMigrationDbContext(options);

        Assert.NotNull(context.GetService<ISafeMigrationRunner>());
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'pipeline_state' "
                + "AND column_name = 'payload';"));
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
            await ExecuteSqlAsync(connectionString, script);
            if (script != scripts[0])
            {
                await ExecuteSqlAsync(connectionString, script);
            }

            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COUNT(*) FROM information_schema.columns "
                    + "WHERE table_schema = current_schema() AND table_name = 'pipeline_state' "
                    + "AND column_name = 'payload';"));
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connectionString,
                    $"SELECT COUNT(*) FROM \"__CoreDbContextMigrationsHistory\" "
                    + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';"));
        }
    }

    [Fact]
    public async Task ConcurrentMigrators_SerializePerDatabaseAndRunDifferentDatabasesInParallel()
    {
        var sharedConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            sharedConnectionString,
            "CREATE TABLE \"__LegacyMigrationsHistory\" (\"MigrationId\" text NOT NULL); "
            + "INSERT INTO \"__LegacyMigrationsHistory\" VALUES ('legacy-unchanged');");
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
                $"SELECT COUNT(*) FROM \"__CoreDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                sharedConnectionString,
                "SELECT COUNT(*) FROM \"__LegacyMigrationsHistory\" " + "WHERE \"MigrationId\" = 'legacy-unchanged';"));

        var leftConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var rightConnectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var left = CreateContext(leftConnectionString);
        await using var right = CreateContext(rightConnectionString);
        await Task.WhenAll(left.Database.MigrateAsync(cancellationToken: CancellationToken.None), right.Database.MigrateAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                leftConnectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'pipeline_state';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                rightConnectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'pipeline_state';"));
    }

    [Fact]
    public async Task PreflightAndPostflight_AreReadOnlyAndUseRuntimeClassification()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "analysis_target",
            table => new { Id = table.Column<int>(type: "integer", nullable: false) },
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
        Assert.Equal("postgresql", preflight.Environment.EngineFamily);
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
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'analysis_target';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() "
                + "AND table_name = '__CoreDbContextMigrationsHistory';"));

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
                "SELECT COUNT(*) FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = current_schema() AND c.relname = 'pipeline_state';"));
    }

    [Fact]
    public async Task PreflightProjectsProviderAddColumnForFollowingSafeIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE mixed_customers (id integer PRIMARY KEY);"
            + "CREATE TABLE mixed_evolution (id integer PRIMARY KEY);"
            + "INSERT INTO mixed_customers (id) VALUES (7);"
            + "INSERT INTO mixed_evolution (id) VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        _ = builder.AddColumn<int>(
            name: "customer_id",
            table: "mixed_evolution",
            type: "integer",
            nullable: false,
            defaultValue: 0);
        _ = builder.CreateIndexIfNotExistsFromModel(
            "ix_mixed_evolution_customer_id",
            "mixed_evolution",
            "customer_id");
        _ = builder.Sql("UPDATE mixed_evolution SET customer_id = 7;");
        _ = builder.Sql("ALTER TABLE mixed_evolution ALTER COLUMN customer_id DROP DEFAULT;");
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
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'mixed_evolution' "
                + "AND column_name = 'customer_id';"));

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
        Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT customer_id FROM mixed_evolution;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_constraint "
                + "WHERE conname = 'fk_mixed_evolution_customers_customer_id' AND contype = 'f';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'mixed_evolution' "
                + "AND column_name = 'customer_id' AND column_default IS NULL;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_indexes "
                + "WHERE schemaname = current_schema() AND tablename = 'mixed_evolution' "
                + "AND indexname = 'ix_mixed_evolution_customer_id';"));
    }

    [Fact]
    public async Task Preflight_RejectsASchemaChangingDerivedContextAgainstTheMigrationSnapshot()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = new SchemaChangingDerivedContext(connectionString);

        await Assert.ThrowsAsync<SafeMigrationModelMismatchException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("test-instance")));
    }

    [Fact]
    public async Task ColumnTableIndexLifecycle_IsIdempotentAcrossEveryOperationFamily()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE lifecycle (id integer NOT NULL, old_name character varying(40) NULL); "
            + "CREATE INDEX ix_lifecycle_old_name ON lifecycle (old_name);");
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
            storeType: "character varying(40)",
            maxLength: 40);

        var targetColumn = new ExpectedColumnDefinition(
            "name",
            typeof(string),
            isNullable: true,
            storeType: "character varying(40)",
            maxLength: 40,
            comment: "canonical name",
            defaultValue: SafeMigrationDefaultValue.Literal("unknown"));

        var alter = new MigrationBuilder(context.Database.ProviderName!);
        alter.AlterColumnIfDifferent("lifecycle", targetColumn, oldColumn, SafeMigrationPolicy.RepairIfSafe);
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
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'renamed_lifecycle';"));
    }

    [Fact]
    public async Task Preflight_ReportsButDoesNotDeleteUnexpectedLegacyObjects()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE inventory_target (id integer NOT NULL, legacy_column integer NULL); "
            + "CREATE INDEX ix_inventory_legacy ON inventory_target (legacy_column); "
            + "CREATE TABLE legacy_extra (id integer NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "inventory_target",
            table => new { Id = table.Column<int>(type: "integer", nullable: false) },
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Contains(
            report.UnexpectedObjects,
            value => value is { ObjectKind: SafeMigrationDatabaseObjectKind.Table, Name: "legacy_extra" });
        Assert.Contains(
            report.UnexpectedObjects,
            value => value is { ObjectKind: SafeMigrationDatabaseObjectKind.Column, Table: "inventory_target", Name: "legacy_column" });
        Assert.Contains(
            report.UnexpectedObjects,
            value => value is { ObjectKind: SafeMigrationDatabaseObjectKind.Index, Name: "ix_inventory_legacy" });
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'legacy_extra';"));
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

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = current_schema() "
                + "AND c.relname IN ('missing_table', 'renamed_table');"));
    }
}
