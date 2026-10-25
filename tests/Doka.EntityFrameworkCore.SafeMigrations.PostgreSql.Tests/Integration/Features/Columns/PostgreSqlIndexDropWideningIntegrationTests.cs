namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Exercises exact integer upgrades after accepted owned index removal.</summary>
public sealed class PostgreSqlIndexDropWideningIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the assembly-owned native engine and test-local database ownership.</summary>
    /// <param name="fixture">The shared PostgreSQL fixture.</param>
    public PostgreSqlIndexDropWideningIntegrationTests(PostgreSqlContainerFixture fixture) : base(fixture) { }

    /// <summary>Applies the complete pending upgrade through EF and replays the generated idempotent script.</summary>
    [Fact]
    public async Task PendingIntegerUpgradeAdvancesHistoryOnceAndIdempotentScriptReplays()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = new IntegerUpgradeDbContext(connectionString);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(IntegerBaseline.MigrationId, CancellationToken.None);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzePendingMigrationsAsync(
            context, new SafeMigrationRunOptions("pending-integer-upgrade"));
        var initialHistory = (await context.Database.GetAppliedMigrationsAsync(CancellationToken.None)).ToArray();
        var initialType = await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'pending_integer_rows' AND column_name = 'coordinate';");
        report.ThrowIfBlocked();
        await context.Database.MigrateAsync(CancellationToken.None);
        await context.Database.MigrateAsync(CancellationToken.None);
        var script = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        await ExecuteSqlAsync(connectionString, script);
        await ExecuteSqlAsync(connectionString, script);

        // Assert
        Assert.Equal([IntegerBaseline.MigrationId], initialHistory);
        Assert.Equal("integer", initialType);
        Assert.Equal([IntegerBaseline.MigrationId, IntegerUpgrade.MigrationId],
            await context.Database.GetAppliedMigrationsAsync(CancellationToken.None));
        Assert.Equal("bigint", await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'pending_integer_rows' AND column_name = 'coordinate';"));
        Assert.Equal("2147483647:retained", await ScalarStringAsync(connectionString,
            "SELECT coordinate::text || ':' || payload FROM pending_integer_rows;"));
    }

    /// <summary>Rejects widening proofs with unsupported index roles, callbacks or stale sources.</summary>
    /// <param name="caseId">The independently selectable proof-boundary adversary.</param>
    [Theory]
    [InlineData("unique-index")]
    [InlineData("expression-index")]
    [InlineData("partial-index")]
    [InlineData("hash-index")]
    [InlineData("stale-source")]
    [InlineData("source-default")]
    [InlineData("foreign-key")]
    [InlineData("generated-dependent")]
    [InlineData("check-dependent")]
    [InlineData("view-dependent")]
    [InlineData("opaque-boundary")]
    [InlineData("event-trigger")]
    public async Task IndexRemovalDoesNotAuthorizeUnprovenTransitions(string caseId)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var sourceType = caseId == "stale-source" ? "smallint" : "integer";
        var defaultClause = caseId == "source-default" ? " DEFAULT 7" : string.Empty;
        var indexSql = caseId switch
        {
            "unique-index" => "CREATE UNIQUE INDEX ix_boundary ON boundary_rows (value);",
            "expression-index" => "CREATE INDEX ix_boundary ON boundary_rows ((value + 1));",
            "partial-index" => "CREATE INDEX ix_boundary ON boundary_rows (value) WHERE value > 0;",
            "hash-index" => "CREATE INDEX ix_boundary ON boundary_rows USING hash (value);",
            _ => "CREATE INDEX ix_boundary ON boundary_rows (value);",
        };

        var dependentSql = caseId switch
        {
            "foreign-key" => "CREATE TABLE referenced_rows (id integer PRIMARY KEY); "
                + "INSERT INTO referenced_rows VALUES (7); ALTER TABLE boundary_rows "
                + "ADD FOREIGN KEY (value) REFERENCES referenced_rows(id);",
            "generated-dependent" => "ALTER TABLE boundary_rows ADD generated_value integer "
                + "GENERATED ALWAYS AS (value + 1) STORED;",
            "check-dependent" => "ALTER TABLE boundary_rows ADD CHECK (value > 0);",
            "view-dependent" => "CREATE VIEW boundary_view AS SELECT value FROM boundary_rows;",
            "event-trigger" => "CREATE FUNCTION boundary_event() RETURNS event_trigger LANGUAGE plpgsql "
                + "AS $$ BEGIN RETURN; END $$; CREATE EVENT TRIGGER boundary_callback ON ddl_command_end "
                + "EXECUTE FUNCTION boundary_event();",
            _ => string.Empty,
        };

        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE boundary_rows (value {sourceType} NOT NULL{defaultClause}, payload text NOT NULL); "
            + "INSERT INTO boundary_rows VALUES (7, 'retained'); " + indexSql + dependentSql);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_boundary", "boundary_rows");
        if (caseId == "opaque-boundary")
        {
            builder.Sql("SELECT 1;");
        }

        builder.AlterColumnIfDifferent("boundary_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("index-drop-widening-boundary"));

        // Assert
        var expectedAction = caseId switch
        {
            "opaque-boundary" or "event-trigger" => SafeMigrationAction.ValidateAtRuntime,
            "unique-index" => SafeMigrationAction.RejectDifferent,
            _ => SafeMigrationAction.RejectPrerequisiteMissing,
        };

        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(expectedAction, report.Assessments[^1].Action);
        Assert.Equal("7:retained", await ScalarStringAsync(connectionString,
            "SELECT value::text || ':' || payload FROM boundary_rows;"));
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM pg_catalog.pg_indexes WHERE schemaname = 'public' AND indexname = 'ix_boundary';"));
    }

    /// <summary>Rechecks source facets at execution instead of trusting a successful earlier preflight.</summary>
    [Fact]
    public async Task RuntimeRechecksTheSourceAfterSuccessfulPreflight()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE stale_runtime_rows (value integer NOT NULL); INSERT INTO stale_runtime_rows VALUES (7); "
            + "CREATE INDEX ix_stale_runtime ON stale_runtime_rows (value);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_stale_runtime", "stale_runtime_rows");
        builder.AlterColumnIfDifferent("stale_runtime_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("stale-runtime-integer"));
        report.ThrowIfBlocked();
        await ExecuteSqlAsync(connectionString, "ALTER TABLE stale_runtime_rows ALTER COLUMN value SET DEFAULT 7;");

        // Act
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context,
            builder.Operations));

        // Assert
        Assert.Equal("P1001", exception.SqlState);
        Assert.Equal("integer", await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'stale_runtime_rows' AND column_name = 'value';"));
        Assert.Equal(7, await ScalarIntAsync(connectionString, "SELECT value FROM stale_runtime_rows;"));
    }

    /// <summary>Does not confuse installed DML callbacks with a callback actually reachable from the prefix.</summary>
    /// <param name="precedingDml">Whether an accepted typed row write can invoke the callback.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DmlCallbackInvalidatesOnlyAnActuallyReachableRowWrite(bool precedingDml)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE trigger_integer_rows (value integer NOT NULL, payload text NOT NULL); "
            + "INSERT INTO trigger_integer_rows VALUES (7, 'retained'); "
            + "CREATE INDEX ix_trigger_integer ON trigger_integer_rows (value); "
            + "CREATE FUNCTION integer_dml_callback() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN RAISE EXCEPTION USING ERRCODE = 'P1991', MESSAGE = 'unexpected_integer_backfill'; END $$; "
            + "CREATE TRIGGER integer_dml_callback AFTER UPDATE ON trigger_integer_rows "
            + "FOR EACH STATEMENT EXECUTE FUNCTION integer_dml_callback();");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_trigger_integer", "trigger_integer_rows");
        if (precedingDml)
        {
            builder.Operations.Add(new UpdateDataOperation
            {
                Table = "trigger_integer_rows", KeyColumns = ["value"], KeyColumnTypes = ["integer"],
                KeyValues = new object[,] { { 7 } }, Columns = ["payload"], ColumnTypes = ["text"],
                Values = new object[,] { { "changed" } },
            });
        }

        builder.AlterColumnIfDifferent("trigger_integer_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("integer-dml-callback"));
        if (!precedingDml)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(precedingDml ? SafeMigrationAction.ValidateAtRuntime : SafeMigrationAction.Repair,
            report.Assessments[^1].Action);
        Assert.Equal("7:retained", await ScalarStringAsync(connectionString,
            "SELECT value::text || ':' || payload FROM trigger_integer_rows;"));
    }

    /// <summary>Does not apply built-in owner proofs to domains, recursive owners or opaque dependents.</summary>
    /// <param name="caseId">The independently selectable native dependency boundary.</param>
    [Theory]
    [InlineData("domain")]
    [InlineData("inherited-parent")]
    [InlineData("inherited-child")]
    [InlineData("expression-index")]
    [InlineData("partial-index")]
    [InlineData("foreign-key-inbound")]
    public async Task StandaloneWideningRetainsNativeDependencyBoundaries(string caseId)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var before = caseId == "domain" ? "CREATE DOMAIN guarded_integer AS integer; "
            : caseId == "inherited-child" ? "CREATE TABLE inherited_base (value integer NOT NULL); "
                : string.Empty;
        var columns = caseId == "domain" ? "value guarded_integer NOT NULL, payload text NOT NULL"
            : caseId == "inherited-child" ? "payload text NOT NULL"
                : caseId == "foreign-key-inbound" ? "value integer NOT NULL PRIMARY KEY, payload text NOT NULL"
                : "value integer NOT NULL, payload text NOT NULL";
        var inheritance = caseId == "inherited-child" ? " INHERITS (inherited_base)" : string.Empty;
        var dependent = caseId switch
        {
            "inherited-parent" => "CREATE TABLE inherited_leaf () INHERITS (dependency_integer_rows);",
            "expression-index" => "CREATE INDEX ix_dependency ON dependency_integer_rows ((value + 1));",
            "partial-index" => "CREATE INDEX ix_dependency ON dependency_integer_rows (value) WHERE value > 0;",
            "foreign-key-inbound" => "CREATE TABLE inbound_integer_rows (value integer NOT NULL "
                + "REFERENCES dependency_integer_rows(value)); INSERT INTO inbound_integer_rows VALUES (7);",
            _ => string.Empty,
        };

        await ExecuteSqlAsync(connectionString, before + "CREATE TABLE dependency_integer_rows (" + columns
            + ")" + inheritance + "; INSERT INTO dependency_integer_rows(value,payload) VALUES (7, 'retained'); "
            + dependent);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("dependency_integer_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("integer-native-dependency-boundary"));
        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context,
            builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Equal("P1001", failure.SqlState);
        Assert.Equal("7:retained", await ScalarStringAsync(connectionString,
            "SELECT value::text || ':' || payload FROM ONLY dependency_integer_rows;"));
    }

    /// <summary>Widens both smaller signed widths using the real engine while preserving boundary values.</summary>
    /// <param name="targetType">The exact wider built-in target.</param>
    [Theory]
    [InlineData("integer")]
    [InlineData("bigint")]
    public async Task SmallerIntegerWideningPreservesRangeBoundaries(string targetType)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE smaller_integer_rows (value smallint NOT NULL); "
            + "INSERT INTO smaller_integer_rows VALUES (-32768), (32767);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("smaller_integer_rows",
            new ExpectedColumnDefinition("value", targetType == "integer" ? typeof(int) : typeof(long),
                false, targetType),
            new ExpectedColumnDefinition("value", typeof(short), false, "smallint"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("smaller-integer-upgrade"));
        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(report.Assessments).Action);
        Assert.Equal("-32768:32767", await ScalarStringAsync(connectionString,
            "SELECT min(value)::text || ':' || max(value)::text FROM smaller_integer_rows;"));
        Assert.Equal(targetType, await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema = 'public' "
            + "AND table_name = 'smaller_integer_rows' AND column_name = 'value';"));
    }

    /// <summary>A mismatched replacement index remains an independent blocker after the old index disappears.</summary>
    [Fact]
    public async Task ReplacementIndexDriftIsNotErasedByAcceptedWidening()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE replacement_integer_rows (value integer NOT NULL, payload text NOT NULL); "
            + "INSERT INTO replacement_integer_rows VALUES (7, 'retained'); "
            + "CREATE INDEX ix_old_integer ON replacement_integer_rows (value); "
            + "CREATE INDEX ix_new_integer ON replacement_integer_rows (payload);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_old_integer", "replacement_integer_rows");
        builder.AlterColumnIfDifferent("replacement_integer_rows",
            new ExpectedColumnDefinition("value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("value", typeof(int), false, "integer"),
            SafeMigrationPolicy.RepairIfSafe);
        builder.CreateIndexIfNotExists("ix_new_integer", "replacement_integer_rows", ["value"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("integer-replacement-index-drift"));

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM pg_catalog.pg_indexes WHERE schemaname = 'public' "
            + "AND indexname = 'ix_old_integer';"));
        Assert.Equal("7:retained", await ScalarStringAsync(connectionString,
            "SELECT value::text || ':' || payload FROM replacement_integer_rows;"));
    }

    /// <summary>Retains unchanged column evidence across an index-only boundary without mutating preflight.</summary>
    /// <param name="explicitSchema">Whether the owner is explicitly schema-qualified.</param>
    /// <param name="dropIndex">Whether the upgrade removes the existing ordinary index first.</param>
    /// <param name="indexAlreadyRemoved">Whether an interrupted prefix already removed the old index.</param>
    /// <param name="threeDrops">Whether each widened source has its own physical index-drop boundary.</param>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    public async Task IntegerUpgradeAfterIndexRemovalPreservesDataAndReplays(
        bool explicitSchema,
        bool dropIndex,
        bool indexAlreadyRemoved,
        bool threeDrops)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var schema = explicitSchema ? "widening" : null;
        var owner = explicitSchema ? "widening.upgrade_rows" : "upgrade_rows";
        await ExecuteSqlAsync(connectionString,
            (explicitSchema ? "CREATE SCHEMA widening; " : string.Empty)
            + $"CREATE TABLE {owner} (left_value integer NOT NULL, right_value integer NOT NULL, "
            + "position_value integer NOT NULL, payload text NOT NULL); "
            + $"INSERT INTO {owner} VALUES (-2147483648, 2147483647, 42, 'retained'); "
            + (indexAlreadyRemoved ? string.Empty
                : $"CREATE INDEX ix_upgrade_rows ON {owner} (left_value"
                    + (threeDrops ? string.Empty : ", right_value") + "); "
                    + (threeDrops ? $"CREATE INDEX ix_upgrade_rows_right ON {owner} (right_value); "
                        + $"CREATE INDEX ix_upgrade_rows_position ON {owner} (position_value);" : string.Empty)));
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (dropIndex)
        {
            builder.DropIndexIfExists("ix_upgrade_rows", "upgrade_rows", schema);
            if (threeDrops)
            {
                builder.DropIndexIfExists("ix_upgrade_rows_right", "upgrade_rows", schema);
                builder.DropIndexIfExists("ix_upgrade_rows_position", "upgrade_rows", schema);
            }
        }

        string[] names = ["left_value", "right_value", "position_value"];

        foreach (var name in names)
        {
            builder.AlterColumnIfDifferent("upgrade_rows",
                new ExpectedColumnDefinition(name, typeof(long), false, "bigint"),
                new ExpectedColumnDefinition(name, typeof(int), false, "integer"),
                SafeMigrationPolicy.RepairIfSafe, schema);
        }

        if (dropIndex)
        {
            builder.CreateIndexIfNotExists("ix_upgrade_left", "upgrade_rows", ["left_value"], schema: schema);
            builder.CreateIndexIfNotExists("ix_upgrade_right", "upgrade_rows", ["right_value"], schema: schema);
            AddUpgradeChecks(builder, "upgrade_rows", schema);
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("index-drop-widening"));
        var before = await ScalarStringAsync(connectionString,
            $"SELECT left_value::text || ':' || right_value::text || ':' || position_value::text || ':' || payload "
            + $"FROM {owner};");
        var beforeType = await ScalarStringAsync(connectionString,
            "SELECT data_type FROM information_schema.columns WHERE table_schema = '"
            + (schema ?? "public") + "' AND table_name = 'upgrade_rows' AND column_name = 'left_value';");
        var beforeIndex = await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM pg_catalog.pg_indexes WHERE schemaname = '" + (schema ?? "public")
            + "' AND indexname = 'ix_upgrade_rows';");
        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("index-drop-widening-replay"));

        // Assert
        Assert.Equal("integer", beforeType);
        Assert.Equal(indexAlreadyRemoved ? 0 : 1, beforeIndex);
        Assert.Equal("-2147483648:2147483647:42:retained", before);
        Assert.Equal(before, await ScalarStringAsync(connectionString,
            $"SELECT left_value::text || ':' || right_value::text || ':' || position_value::text || ':' || payload "
            + $"FROM {owner};"));
        Assert.Equal(names.Length, await ScalarIntAsync(connectionString,
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = '" + (schema ?? "public")
            + "' AND table_name = 'upgrade_rows' AND data_type = 'bigint' AND is_nullable = 'NO';"));
        if (dropIndex)
        {
            Assert.Equal(0, await ScalarIntAsync(connectionString,
                "SELECT count(*) FROM pg_catalog.pg_indexes WHERE schemaname = '" + (schema ?? "public")
                + "' AND indexname IN ('ix_upgrade_rows', 'ix_upgrade_rows_right', 'ix_upgrade_rows_position');"));
            Assert.Equal(2, await ScalarIntAsync(connectionString,
                "SELECT count(*) FROM pg_catalog.pg_indexes WHERE schemaname = '" + (schema ?? "public")
                + "' AND tablename = 'upgrade_rows' AND indexname IN ('ix_upgrade_left', 'ix_upgrade_right');"));
            Assert.Equal(4, await ScalarIntAsync(connectionString,
                "SELECT count(*) FROM pg_catalog.pg_constraint co "
                + "JOIN pg_catalog.pg_class tbl ON tbl.oid = co.conrelid "
                + "JOIN pg_catalog.pg_namespace ns ON ns.oid = tbl.relnamespace "
                + "WHERE ns.nspname = '" + (schema ?? "public")
                + "' AND tbl.relname = 'upgrade_rows' AND co.contype = 'c';"));
        }

        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
    }

    /// <summary>Adds generated CHECK contracts only after each widened source has its accepted definition.</summary>
    private static void AddUpgradeChecks(MigrationBuilder builder, string table, string? schema)
    {
        string[] expressions =
        [
            "left_value < right_value",
            "position_value >= 0",
            "left_value >= -9223372036854775807",
            "right_value <= CASE WHEN position_value = 9223372036854775807 "
                + "THEN position_value ELSE 9223372036854775807 END",
        ];

        // WHY: The ordinal is part of each independently verified constraint's stable identity.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < expressions.Length; ordinal++)
        {
            builder.AddCheckConstraintIfNotExists(
                "ck_upgrade_" + ordinal.ToString(CultureInfo.InvariantCulture), table, expressions[ordinal], schema);
        }
    }

    /// <summary>Owns only the isolated migration history used by the real upgrade regression.</summary>
    public sealed class IntegerUpgradeDbContext(string connectionString) : DbContext
    {
        /// <inheritdoc />
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseNpgsql(connectionString,
                    options => options.MigrationsHistoryTable("__IntegerUpgradeHistory"))
                .UsePostgreSqlSafeMigrations<IntegerUpgradeDbContext>();
    }

    /// <summary>Declares the intentionally model-free schema contract of these authored fixture migrations.</summary>
    [DbContext(typeof(IntegerUpgradeDbContext))]
    public sealed class IntegerUpgradeSnapshot : ModelSnapshot
    {
        /// <inheritdoc />
        protected override void BuildModel(ModelBuilder modelBuilder)
            => ArgumentNullException.ThrowIfNull(modelBuilder);
    }

    /// <summary>Creates a historical Int32 owner with a removable ordinary index and boundary payload.</summary>
    [DbContext(typeof(IntegerUpgradeDbContext))]
    [Migration(MigrationId)]
    public sealed class IntegerBaseline : Migration
    {
        /// <summary>Identifies the frozen historical fixture migration.</summary>
        public const string MigrationId = "20261023010100_IntegerBaseline";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
            => migrationBuilder.Sql("CREATE TABLE pending_integer_rows (coordinate integer NOT NULL, "
                + "payload text NOT NULL); CREATE INDEX ix_pending_integer ON pending_integer_rows (coordinate); "
                + "INSERT INTO pending_integer_rows VALUES (2147483647, 'retained');");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("pending_integer_rows");
    }

    /// <summary>Executes the owned index-drop then exact integer-widening upgrade through EF.</summary>
    [DbContext(typeof(IntegerUpgradeDbContext))]
    [Migration(MigrationId)]
    public sealed class IntegerUpgrade : Migration
    {
        /// <summary>Identifies the pending widening fixture migration.</summary>
        public const string MigrationId = "20261023010200_IntegerUpgrade";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndexIfExists("ix_pending_integer", "pending_integer_rows");
            migrationBuilder.AlterColumnIfDifferent("pending_integer_rows",
                new ExpectedColumnDefinition("coordinate", typeof(long), false, "bigint"),
                new ExpectedColumnDefinition("coordinate", typeof(int), false, "integer"),
                SafeMigrationPolicy.RepairIfSafe);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => throw new NotSupportedException("This regression verifies only the reviewed widening upgrade.");
    }
}
