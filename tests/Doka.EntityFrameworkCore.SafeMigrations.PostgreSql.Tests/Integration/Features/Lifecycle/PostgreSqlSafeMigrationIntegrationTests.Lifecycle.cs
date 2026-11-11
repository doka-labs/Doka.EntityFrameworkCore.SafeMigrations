namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the Lifecycle PostgreSQL feature cases independently.</summary>
public sealed partial class PostgreSqlLifecycleIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlLifecycleIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    [Fact]
    public async Task StrictMismatch_FailsWithStableSqlState()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE accounts (id integer NOT NULL, name character varying(20) NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>("name", "accounts", type: "character varying(100)", nullable: true);

        var exception =
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(
                context,
                builder.Operations,
                CancellationToken.None));

        Assert.Equal("P1001", exception.SqlState);
        Assert.StartsWith("doka_sm_different:", exception.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingAdapter_FailsClosedDuringGeneration()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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
                + "WHERE table_schema = current_schema() AND table_name = 'must_not_exist';",
                cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task Migrator_MixesStandardAndSafeOperationsAndWritesOneHistoryRow()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE pipeline_state (id integer NOT NULL PRIMARY KEY);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        await context
            .GetService<IMigrator>()
            .MigrateAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'pipeline_state'
                AND column_name = 'payload';
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM \"__ApplicationDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task RuntimeGuardFailureAfterStandardDdl_RollsBackAndRetriesWithoutHistoryDrift()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE VIEW pipeline_state AS SELECT 1 AS id;",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Database.MigrateAsync(
            cancellationToken: CancellationToken.None));

        Assert.Equal("P1002", exception.SqlState);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'pipeline_probe';",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM \"__ApplicationDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None));

        await ExecuteSqlAsync(connectionString, "DROP VIEW pipeline_state;", cancellationToken: CancellationToken.None);
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM \"__ApplicationDbContextMigrationsHistory\" "
                + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';",
                cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task ExternalInternalServiceProvider_ResolvesTheComposedAdapterAndRunner()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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
                    .MigrationsHistoryTable("__ApplicationDbContextMigrationsHistory"))
            .UsePostgreSqlSafeMigrations()
            .Options;

        await using var context = new SafeMigrationDbContext(options);

        Assert.NotNull(context.GetService<ISafeMigrationRunner>());
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'pipeline_state'
                AND column_name = 'payload';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Applies one script mode and retains its original replay/history contract.</summary>
    /// <param name="caseId">The independently selectable generation mode.</param>
    [Theory]
    [InlineData("normal")]
    [InlineData("idempotent")]
    [InlineData("idempotent-no-transactions")]
    public async Task MigrationScripts_ExecuteSelectedMode(
        string caseId
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var options = caseId switch
        {
            "normal" => default,
            "idempotent" => MigrationsSqlGenerationOptions.Idempotent,
            "idempotent-no-transactions" => MigrationsSqlGenerationOptions.Idempotent
                | MigrationsSqlGenerationOptions.NoTransactions,
            _ => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Unknown script mode."),
        };

        // Act
        var script = context
            .GetService<IMigrator>()
            .GenerateScript(options: options);

        await ExecuteSqlAsync(connectionString, script, cancellationToken: CancellationToken.None);
        if (caseId != "normal")
        {
            await ExecuteSqlAsync(connectionString, script, cancellationToken: CancellationToken.None);
        }

        var payloadColumns = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_schema = current_schema()
            AND table_name = 'pipeline_state'
            AND column_name = 'payload';
            """,
            cancellationToken: CancellationToken.None);

        var historyRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM \"__ApplicationDbContextMigrationsHistory\" "
            + $"WHERE \"MigrationId\" = '{CoreConvergenceMigration.MigrationIdentifier}';",
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Contains(CoreConvergenceMigration.MigrationIdentifier, script, StringComparison.Ordinal);
        Assert.Equal(1, payloadColumns);
        Assert.Equal(1, historyRows);
    }

    [Fact]
    public async Task PreflightAndPostflight_AreReadOnlyAndUseRuntimeClassification()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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

        var preflight = await runner.AnalyzeAsync(context, builder.Operations, runOptions, CancellationToken.None);

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
                """
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema = current_schema()
                AND table_name = 'analysis_target';
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema = current_schema()
                AND table_name = '__ApplicationDbContextMigrationsHistory';
                """,
                cancellationToken: CancellationToken.None));

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations, runOptions, CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(
            Assert.Single(postflight.Assessments)
                .PostconditionSatisfied);
    }

    /// <summary>Reports the complete missing table shape without creating the pending migration's table.</summary>
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

        var tables = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema()
            AND c.relname = 'pipeline_state';
            """,
            cancellationToken: CancellationToken.None);

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

        Assert.Equal(0, tables);
    }

    [Fact]
    public async Task PreflightProjectsProviderAddColumnForFollowingSafeIndex()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE mixed_customers (id integer PRIMARY KEY);CREATE TABLE mixed_evolution
            (id integer PRIMARY KEY);INSERT INTO mixed_customers (id) VALUES (7);INSERT
            INTO mixed_evolution (id) VALUES (1);
            """,
            cancellationToken: CancellationToken.None);

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
            "customer_id",
            schema: "public");

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
        var preflight = await runner.AnalyzeAsync(context, builder.Operations, options, CancellationToken.None);
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
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'mixed_evolution'
                AND column_name = 'customer_id';
                """,
                cancellationToken: CancellationToken.None));

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(context, builder.Operations, options, CancellationToken.None);
        var replay = await runner.AnalyzeAsync(context, builder.Operations, options, CancellationToken.None);
        var postflightIndex = postflight.Assessments.Single(static assessment => assessment.IsSafeOperation);
        var replayIndex = replay.Assessments.Single(static assessment => assessment.IsSafeOperation);

        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, postflightIndex.ObservedState);
        Assert.True(postflightIndex.PostconditionSatisfied);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, replayIndex.Action);
        Assert.Equal(
            7,
            await ScalarIntAsync(
                connectionString,
                "SELECT customer_id FROM mixed_evolution;",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM pg_catalog.pg_constraint WHERE conname = 'fk_mixed_evolution_customers_customer_id'
                AND contype = 'f';
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'mixed_evolution'
                AND column_name = 'customer_id'
                AND column_default IS NULL;
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM pg_catalog.pg_indexes WHERE schemaname = current_schema()
                AND tablename = 'mixed_evolution'
                AND indexname = 'ix_mixed_evolution_customer_id';
                """,
                cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task Preflight_RejectsASchemaChangingDerivedContextAgainstTheMigrationSnapshot()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = new SchemaChangingDerivedContext(connectionString);

        await Assert.ThrowsAsync<SafeMigrationModelMismatchException>(() => context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, [], new SafeMigrationRunOptions("test-instance"), CancellationToken.None));
    }

    [Fact]
    public async Task ColumnTableIndexLifecycle_IsIdempotentAcrossEveryOperationFamily()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE lifecycle (id integer NOT NULL, old_name character varying(40)
            NULL); CREATE INDEX ix_lifecycle_old_name ON lifecycle (old_name);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var rename = new MigrationBuilder(context.Database.ProviderName!);
        rename.RenameColumnIfExists("old_name", "lifecycle", "name");
        rename.RenameIndexIfExists("ix_lifecycle_old_name", "lifecycle", "ix_lifecycle_name");
        await ExecuteOperationsAsync(context, rename.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, rename.Operations, CancellationToken.None);

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

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.tables
                WHERE table_schema = current_schema()
                AND table_name = 'renamed_lifecycle';
                """,
                cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task Preflight_ReportsButDoesNotDeleteUnexpectedLegacyObjects()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE inventory_target (id integer NOT NULL, legacy_column integer
            NULL); CREATE INDEX ix_inventory_legacy ON inventory_target (legacy_column);
            CREATE TABLE legacy_extra (id integer NOT NULL);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "inventory_target",
            table => new { Id = table.Column<int>(type: "integer", nullable: false) },
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

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
                "SELECT COUNT(*) FROM information_schema.tables "
                + "WHERE table_schema = current_schema() AND table_name = 'legacy_extra';",
                cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task MissingRenameSources_AreIdempotentNoOps()
    {
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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
                """
                SELECT COUNT(*) FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = current_schema()
                AND c.relname IN ('missing_table', 'renamed_table');
                """,
                cancellationToken: CancellationToken.None));
    }
}
