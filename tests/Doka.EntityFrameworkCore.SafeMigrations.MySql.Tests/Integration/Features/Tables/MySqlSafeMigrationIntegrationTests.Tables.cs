namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task GeneratedCheckConstraint_IsAcceptedByPreflightRuntimeAndPostflight()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "generated_check_orders",
            table => new
            {
                amount = table.Column<int>(type: "int", nullable: false),
            },
            constraints: table => table.CheckConstraint(
                "ck_generated_check_orders_amount",
                "`amount` >= 0"));
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("generated-check-preflight"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("generated-check-postflight"));

        var exception = await Assert.ThrowsAsync<MySqlException>(() => ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `generated_check_orders` (`amount`) VALUES (-1);"));

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Contains("ck_generated_check_orders_amount", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GranularConvergence_CompletesExistingPartialTableAndIsIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `module_state` (`id` int NOT NULL, PRIMARY KEY (`id`)); "
            + "INSERT INTO `module_state` (`id`) VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "module_state",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
            },
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);
        builder.AddColumnIfNotExists<string>(
            "display_name",
            "module_state",
            type: "varchar(100)",
            maxLength: 100,
            nullable: true);
        builder.CreateIndexIfNotExists("ix_module_state_display_name", "module_state", ["display_name"]);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'module_state' "
                + "AND COLUMN_NAME = 'display_name';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(DISTINCT INDEX_NAME) FROM INFORMATION_SCHEMA.STATISTICS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'module_state' "
                + "AND INDEX_NAME = 'ix_module_state_display_name';"));
    }

    [Fact]
    public async Task ExistenceOnly_AcceptsAnExistingTableContainerWithoutMutatingShapeDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE `existence_shape` (`id` varchar(20) NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "existence_shape",
            table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                Payload = table.Column<string>(type: "varchar(80)", nullable: true),
            },
            policy: SafeMigrationPolicy.ExistenceOnly);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);

        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(
            20,
            await ScalarIntAsync(
                connectionString,
                "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'existence_shape' "
                + "AND COLUMN_NAME = 'id';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS "
                + "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'existence_shape' "
                + "AND COLUMN_NAME = 'Payload';"));
    }

    [Fact]
    public async Task PairwiseLegacyStates_ConvergeOrBlockWithoutPreflightMutation()
    {
        var scenarios = SafeMigrationStateSpaceGenerator.GeneratePairwise(
            [
                new SafeMigrationStateDimension("column", ["missing", "matching", "different"]),
                new SafeMigrationStateDimension("index", ["missing", "matching", "different"]),
                new SafeMigrationStateDimension("data", ["empty", "populated"]),
                new SafeMigrationStateDimension("extras", ["none", "unknown"]),
                new SafeMigrationStateDimension("history", ["none", "legacy"]),
            ],
            seed: 0x5AFE2026);

        foreach (var scenario in scenarios)
        {
            var table = $"state_matrix_{scenario.Index.ToString(CultureInfo.InvariantCulture)}";
            var index = $"ix_{table}_id";
            var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
            var columnSql = scenario.Values["column"] switch
            {
                "missing" => string.Empty,
                "matching" => ", `payload` varchar(40) NULL",
                "different" => ", `payload` varchar(20) NULL",
                _ => throw new InvalidOperationException("Unknown generated column state."),
            };

            await ExecuteSqlAsync(
                connectionString,
                $"CREATE TABLE `{table}` (`id` int NOT NULL, `alternate_id` int NOT NULL{columnSql});");
            if (scenario.Values["index"] != "missing")
            {
                var indexColumn = scenario.Values["index"] == "matching" ? "id" : "alternate_id";
                await ExecuteSqlAsync(connectionString, $"CREATE INDEX `{index}` ON `{table}` (`{indexColumn}`);");
            }

            if (scenario.Values["data"] == "populated")
            {
                var insertColumns = scenario.Values["column"] == "missing"
                    ? "`id`, `alternate_id`"
                    : "`id`, `alternate_id`, `payload`";

                var insertValues = scenario.Values["column"] == "missing" ? "1, 2" : "1, 2, 'legacy'";
                await ExecuteSqlAsync(
                    connectionString,
                    $"INSERT INTO `{table}` ({insertColumns}) VALUES ({insertValues});");
            }

            if (scenario.Values["extras"] == "unknown")
            {
                await ExecuteSqlAsync(connectionString, $"ALTER TABLE `{table}` ADD COLUMN `legacy_extra` int NULL;");
            }

            if (scenario.Values["history"] == "legacy")
            {
                await ExecuteSqlAsync(
                    connectionString,
                    "CREATE TABLE `__LegacyMigrationsHistory` (`MigrationId` varchar(150) NOT NULL); "
                    + "INSERT INTO `__LegacyMigrationsHistory` VALUES ('legacy-unchanged');");
            }

            await using var context = CreateContext(connectionString);
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.CreateTableIfNotExists(
                table,
                columns => new
                {
                    Id = columns.Column<int>(type: "int", nullable: false),
                    AlternateId = columns.Column<int>(name: "alternate_id", type: "int", nullable: false),
                },
                mode: SafeMigrationTableMode.ConvergenceContainer,
                policy: SafeMigrationPolicy.ExistenceOnly);
            builder.AddColumnIfNotExists<string>("payload", table, type: "varchar(40)", maxLength: 40, nullable: true);
            builder.CreateIndexIfNotExists(index, table, ["id"]);

            var beforeColumnLength = await ScalarIntAsync(
                connectionString,
                "SELECT COALESCE(MAX(CHARACTER_MAXIMUM_LENGTH), 0) "
                + "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
                + $"AND TABLE_NAME = '{table}' AND COLUMN_NAME = 'payload';");

            var report = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions($"pairwise-{scenario.Index}"));

            Assert.Equal(
                beforeColumnLength,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COALESCE(MAX(CHARACTER_MAXIMUM_LENGTH), 0) "
                    + "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() "
                    + $"AND TABLE_NAME = '{table}' AND COLUMN_NAME = 'payload';"));

            var shouldBlock = scenario.Values["column"] == "different" || scenario.Values["index"] == "different";

            Assert.Equal(
                shouldBlock ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
                report.Status);
            if (shouldBlock)
            {
                Assert.Contains(
                    report.Assessments,
                    assessment => assessment.ObservedState == SafeMigrationObservedState.Different);
                continue;
            }

            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);

            Assert.Equal(
                40,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS "
                    + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' "
                    + "AND COLUMN_NAME = 'payload';"));
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS "
                    + $"WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' "
                    + $"AND INDEX_NAME = '{index}' AND COLUMN_NAME = 'id';"));
        }
    }

    [Fact]
    public async Task StrictTableDefinition_RejectsUnexpectedColumnsAndConstraints()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "strict_table",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("code", typeof(string), true, "varchar(30)", maxLength: 30),
            ],
            comment: "strict shape",
            primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "strict_table", ["id"]),
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition("uq_strict_code", "strict_table", ["code"]),],
            checkConstraints:
            [
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_strict_id",
                    "strict_table",
                    SqlColumnAndInt("id", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            ]);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(definition, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connectionString, "ALTER TABLE `strict_table` ADD `legacy` int NULL;");

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);

        await ExecuteSqlAsync(
            connectionString,
            "ALTER TABLE `strict_table` DROP COLUMN `legacy`, "
            + "ADD CONSTRAINT `uq_strict_unexpected` UNIQUE (`id`, `code`);");
        report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);
    }

    [Fact]
    public async Task StrictTableDefinition_NormalizesExpectedUniqueIndexWithoutAcceptingUnknownUniqueKeys()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var options = new DbContextOptionsBuilder<StrictUniqueIndexContext>()
            .UseMySql(connectionString, Fixture.ServerVersion)
            .UseMySqlSafeMigrations<StrictUniqueIndexContext>()
            .Options;

        await using var context = new StrictUniqueIndexContext(options);
        var definition = new ExpectedTableDefinition(
            "strict_unique_index",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("email", typeof(string), true, "varchar(200)", maxLength: 200),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "strict_unique_index", ["id"]));

        var expectedIndex = new ExpectedIndexDefinition(
            "ux_strict_unique_index_email",
            "strict_unique_index",
            [new ExpectedIndexKeyDefinition(column: "email")],
            unique: true);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(definition, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(expectedIndex, SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-unique-index-preflight"));

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-unique-index-postflight"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(preflight.Assessments, static assessment =>
            Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));
        Assert.Empty(preflight.UnexpectedObjects);
        Assert.Empty(postflight.UnexpectedObjects);

        await ExecuteSqlAsync(
            connectionString,
            "ALTER TABLE `strict_unique_index` "
            + "ADD CONSTRAINT `uq_strict_unique_index_unknown` UNIQUE (`id`, `email`);");

        var runtimeException = await Assert.ThrowsAsync<MySqlException>(() =>
            ExecuteOperationsAsync(context, builder.Operations));

        var drift = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-unique-index-drift"));

        Assert.Contains("doka_sm_different", runtimeException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SafeMigrationReportStatus.Blocked, drift.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            drift.Assessments[0].ObservedState);
        Assert.Contains(
            drift.UnexpectedObjects,
            static value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.UniqueConstraint,
                Table: "strict_unique_index",
                Name: "uq_strict_unique_index_unknown",
            });
    }

    [Fact]
    public async Task UnexpectedObjectInventory_DoesNotAliasUniqueConstraintToExpectedNonUniqueIndex()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `non_unique_alias` ("
            + "`id` int NOT NULL, `code` varchar(30) NULL, "
            + "CONSTRAINT `ix_non_unique_alias_code` UNIQUE (`code`));");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "non_unique_alias",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("code", typeof(string), true, "varchar(30)", maxLength: 30),
            ]);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            definition,
            SafeMigrationTableMode.ConvergenceContainer,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ix_non_unique_alias_code",
                "non_unique_alias",
                [new ExpectedIndexKeyDefinition(column: "code")]),
            SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("non-unique-alias"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[^1].ObservedState);
        Assert.Contains(
            report.UnexpectedObjects,
            static value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.UniqueConstraint,
                Table: "non_unique_alias",
                Name: "ix_non_unique_alias_code",
            });
    }

    private sealed class StrictUniqueIndexContext(
        DbContextOptions<StrictUniqueIndexContext> options
    ) : DbContext(options)
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<StrictUniqueIndexEntry>(entity =>
            {
                entity.ToTable("strict_unique_index");
                entity.HasKey(entry => entry.Id)
                    .HasName("PRIMARY");
                entity.Property(entry => entry.Id)
                    .HasColumnName("id")
                    .HasColumnType("int")
                    .ValueGeneratedNever();
                entity.Property(entry => entry.Email)
                    .HasColumnName("email")
                    .HasColumnType("varchar(200)")
                    .HasMaxLength(200);
                entity.HasIndex(entry => entry.Email)
                    .IsUnique()
                    .HasDatabaseName("ux_strict_unique_index_email");
            });
        }
    }

    private sealed class StrictUniqueIndexEntry
    {
        public int Id { get; init; }

        public string? Email { get; init; }
    }
}
