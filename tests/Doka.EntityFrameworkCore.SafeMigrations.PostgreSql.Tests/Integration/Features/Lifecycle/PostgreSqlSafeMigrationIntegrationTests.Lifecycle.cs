namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task StrictMismatch_FailsWithStableSqlState()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
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
        var connectionString = await Fixture.CreateDatabaseAsync();
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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE pipeline_state (id integer NOT NULL PRIMARY KEY);");
        await using var context = CreateContext(connectionString);

        await context.Database.MigrateAsync();
        await context
            .GetService<IMigrator>()
            .MigrateAsync();

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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE VIEW pipeline_state AS SELECT 1 AS id;");
        await using var context = CreateContext(connectionString);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync());

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
        await context.Database.MigrateAsync();

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
        var connectionString = await Fixture.CreateDatabaseAsync();
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
        await context.Database.MigrateAsync();

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
        var sharedConnectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            sharedConnectionString,
            "CREATE TABLE \"__LegacyMigrationsHistory\" (\"MigrationId\" text NOT NULL); "
            + "INSERT INTO \"__LegacyMigrationsHistory\" VALUES ('legacy-unchanged');");
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
                $"SELECT COUNT(*) FROM \"__CoreDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                sharedConnectionString,
                "SELECT COUNT(*) FROM \"__LegacyMigrationsHistory\" " + "WHERE \"MigrationId\" = 'legacy-unchanged';"));

        var leftConnectionString = await Fixture.CreateDatabaseAsync();
        var rightConnectionString = await Fixture.CreateDatabaseAsync();
        await using var left = CreateContext(leftConnectionString);
        await using var right = CreateContext(rightConnectionString);
        await Task.WhenAll(left.Database.MigrateAsync(), right.Database.MigrateAsync());

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
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "analysis_target",
            table => new { Id = table.Column<int>(type: "integer", nullable: false) },
            mode: SafeMigrationTableMode.ConvergenceContainer,
            policy: SafeMigrationPolicy.ExistenceOnly);
        var runner = context.GetService<ISafeMigrationRunner>();
        var fingerprint = SafeMigrationModelFingerprint.Create(context.Model);
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
                "SELECT COUNT(*) FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = current_schema() AND c.relname = 'pipeline_state';"));
    }

    [Fact]
    public async Task Preflight_RejectsASchemaChangingDerivedContextAgainstTheMigrationSnapshot()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = new SchemaChangingDerivedContext(connectionString);

        await Assert.ThrowsAsync<SafeMigrationModelMismatchException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("test-instance")));
    }

    [Fact]
    public async Task ColumnTableIndexLifecycle_IsIdempotentAcrossEveryOperationFamily()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
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
        var connectionString = await Fixture.CreateDatabaseAsync();
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
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'legacy_extra';"));
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
                "SELECT COUNT(*) FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = current_schema() "
                + "AND c.relname IN ('missing_table', 'renamed_table');"));
    }
}
