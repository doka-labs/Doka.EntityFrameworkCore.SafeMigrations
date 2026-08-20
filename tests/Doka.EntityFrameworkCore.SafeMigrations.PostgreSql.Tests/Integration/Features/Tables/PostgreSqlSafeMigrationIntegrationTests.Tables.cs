namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Fact]
    public async Task GranularConvergence_CompletesExistingPartialTableAndIsIdempotent()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE module_state (id integer NOT NULL PRIMARY KEY); "
            + "INSERT INTO module_state (id) VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "module_state",
            table => new { Id = table.Column<int>(type: "integer", nullable: false) },
            policy: SafeMigrationPolicy.ExistenceOnly,
            mode: SafeMigrationTableMode.ConvergenceContainer);
        builder.AddColumnIfNotExists<string>(
            "display_name",
            "module_state",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);
        builder.CreateIndexIfNotExists("ix_module_state_display_name", "module_state", ["display_name"]);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'module_state' "
                + "AND column_name = 'display_name';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM pg_catalog.pg_class c "
                + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
                + "WHERE n.nspname = current_schema() "
                + "AND c.relname = 'ix_module_state_display_name' AND c.relkind = 'i';"));
    }

    [Fact]
    public async Task ExistenceOnly_AcceptsAnExistingTableContainerWithoutMutatingShapeDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE existence_shape (id character varying(20) NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "existence_shape",
            table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                Payload = table.Column<string>(type: "character varying(80)", nullable: true),
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
                "SELECT character_maximum_length FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'existence_shape' "
                + "AND column_name = 'id';"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM information_schema.columns "
                + "WHERE table_schema = current_schema() AND table_name = 'existence_shape' "
                + "AND column_name = 'Payload';"));
    }

    [Fact]
    public async Task PairwiseLegacyStates_ConvergeOrBlockWithoutPreflightMutation()
    {
        var scenarios = SafeMigrationStateSpaceGenerator.GeneratePairwise(
            [
                new(
                    "column",
                    [
                        "missing",
                        "matching",
                        "different"
                    ]),
                new(
                    "index",
                    [
                        "missing",
                        "matching",
                        "different"
                    ]),
                new(
                    "data",
                    [
                        "empty",
                        "populated"
                    ]),
                new(
                    "extras",
                    [
                        "none",
                        "unknown"
                    ]),
                new(
                    "history",
                    [
                        "none",
                        "legacy"
                    ]),
            ],
            seed: 0x5AFE2026);

        foreach (var scenario in scenarios)
        {
            var table = $"state_matrix_{scenario.Index.ToString(CultureInfo.InvariantCulture)}";
            var index = $"ix_{table}_id";
            var connectionString = await Fixture.CreateDatabaseAsync();
            var columnSql = scenario.Values["column"] switch
            {
                "missing" => string.Empty,
                "matching" => ", payload character varying(40) NULL",
                "different" => ", payload character varying(20) NULL",
                _ => throw new InvalidOperationException("Unknown generated column state."),
            };

            await ExecuteSqlAsync(
                connectionString,
                $"CREATE TABLE {table} (id integer NOT NULL, alternate_id integer NOT NULL{columnSql});");
            if (scenario.Values["index"] != "missing")
            {
                var indexColumn = scenario.Values["index"] == "matching" ? "id" : "alternate_id";
                await ExecuteSqlAsync(connectionString, $"CREATE INDEX {index} ON {table} ({indexColumn});");
            }

            if (scenario.Values["data"] == "populated")
            {
                var insertColumns = scenario.Values["column"] == "missing"
                    ? "id, alternate_id"
                    : "id, alternate_id, payload";

                var insertValues = scenario.Values["column"] == "missing" ? "1, 2" : "1, 2, 'legacy'";
                await ExecuteSqlAsync(
                    connectionString,
                    $"INSERT INTO {table} ({insertColumns}) VALUES ({insertValues});");
            }

            if (scenario.Values["extras"] == "unknown")
            {
                await ExecuteSqlAsync(connectionString, $"ALTER TABLE {table} ADD COLUMN legacy_extra integer NULL;");
            }

            if (scenario.Values["history"] == "legacy")
            {
                await ExecuteSqlAsync(
                    connectionString,
                    "CREATE TABLE \"__LegacyMigrationsHistory\" (\"MigrationId\" text NOT NULL); "
                    + "INSERT INTO \"__LegacyMigrationsHistory\" VALUES ('legacy-unchanged');");
            }

            await using var context = CreateContext(connectionString);
            var builder = new MigrationBuilder(context.Database.ProviderName!);
            builder.CreateTableIfNotExists(
                table,
                columns => new
                {
                    Id = columns.Column<int>(type: "integer", nullable: false),
                    AlternateId = columns.Column<int>(name: "alternate_id", type: "integer", nullable: false),
                },
                mode: SafeMigrationTableMode.ConvergenceContainer,
                policy: SafeMigrationPolicy.ExistenceOnly);
            builder.AddColumnIfNotExists<string>(
                "payload",
                table,
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
            builder.CreateIndexIfNotExists(index, table, ["id"]);

            var beforeColumnLength = await ScalarIntAsync(
                connectionString,
                "SELECT COALESCE(MAX(character_maximum_length), 0) "
                + "FROM information_schema.columns WHERE table_schema = current_schema() "
                + $"AND table_name = '{table}' AND column_name = 'payload';");

            var report = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions($"pairwise-{scenario.Index}"));

            Assert.Equal(
                beforeColumnLength,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COALESCE(MAX(character_maximum_length), 0) "
                    + "FROM information_schema.columns WHERE table_schema = current_schema() "
                    + $"AND table_name = '{table}' AND column_name = 'payload';"));

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
                    "SELECT character_maximum_length FROM information_schema.columns "
                    + $"WHERE table_schema = current_schema() AND table_name = '{table}' "
                    + "AND column_name = 'payload';"));
            Assert.Equal(
                1,
                await ScalarIntAsync(
                    connectionString,
                    "SELECT COUNT(*) FROM pg_catalog.pg_index i "
                    + "JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid "
                    + "JOIN pg_catalog.pg_attribute a ON a.attrelid = i.indrelid "
                    + "AND a.attnum = ANY(i.indkey) "
                    + $"WHERE c.relname = '{index}' AND a.attname = 'id';"));
        }
    }

    [Fact]
    public async Task StrictTableDefinition_RejectsUnexpectedColumnsAndConstraints()
    {
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "strict_table",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("code", typeof(string), true, "character varying(30)", maxLength: 30),
            ],
            comment: "strict shape",
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_strict_table", "strict_table", ["id"]),
            uniqueConstraints:
            [
                new ExpectedUniqueConstraintDefinition("uq_strict_code", "strict_table", ["code"]),
            ],
            checkConstraints:
            [
                new ExpectedCheckConstraintDefinition("ck_strict_id", "strict_table", "id >= 0"),
            ]);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(definition, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connectionString, "ALTER TABLE strict_table ADD legacy integer NULL;");

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
            "ALTER TABLE strict_table DROP COLUMN legacy, " + "ADD CONSTRAINT uq_strict_unexpected UNIQUE (id, code);");
        report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(context, builder.Operations, new SafeMigrationRunOptions("test-instance"));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(
            SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);
    }
}
