namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the Tables migration contracts in an independent class collection.</summary>
public sealed class MySqlTablesIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlTablesIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>
    /// Accepts a generated check contract in analysis and execution and verifies its native enforcement.
    /// </summary>
    [Fact]
    public async Task GeneratedCheckConstraint_IsAcceptedByPreflightRuntimeAndPostflight()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("generated-check-preflight"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("generated-check-postflight"),
            CancellationToken.None);

        var capturedException = await Record.ExceptionAsync(() => ExecuteSqlAsync(
            connectionString,
            "INSERT INTO `generated_check_orders` (`amount`) VALUES (-1);",
            cancellationToken: CancellationToken.None));

        // Assert
        var exception = Assert.IsType<MySqlException>(capturedException);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments)
            .PostconditionSatisfied);

        Assert.Contains("ck_generated_check_orders_amount", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Completes a partial legacy table without losing its already-installed columns or replay safety.
    /// </summary>
    [Fact]
    public async Task GranularConvergence_CompletesExistingPartialTableAndIsIdempotent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `module_state` (`id` int NOT NULL, PRIMARY KEY (`id`)); "
            + "INSERT INTO `module_state` (`id`) VALUES (1);",
            cancellationToken: CancellationToken.None);

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

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Assert
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'module_state'
                AND COLUMN_NAME = 'display_name';
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(DISTINCT INDEX_NAME)
                FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = 'module_state'
                AND INDEX_NAME = 'ix_module_state_display_name';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>
    /// Distinguishes an existing container from strict shape equality without rewriting its drifted columns.
    /// </summary>
    [Fact]
    public async Task ExistenceOnly_AcceptsAnExistingTableContainerWithoutMutatingShapeDrift()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `existence_shape` (`id` varchar(20) NOT NULL);",
            cancellationToken: CancellationToken.None);

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

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var preservedIdLength = await ScalarIntAsync(
            connectionString,
            """
            SELECT CHARACTER_MAXIMUM_LENGTH
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'existence_shape'
            AND COLUMN_NAME = 'id';
            """,
            cancellationToken: CancellationToken.None);

        var unexpectedPayloadColumns = await ScalarIntAsync(
            connectionString,
            """
            SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = 'existence_shape'
            AND COLUMN_NAME = 'Payload';
            """,
            cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);

        Assert.Equal(20, preservedIdLength);

        Assert.Equal(0, unexpectedPayloadColumns);
    }

    /// <summary>Exposes every fixed-seed legacy-state vector as an independently discoverable test case.</summary>
    /// <returns>The serializable index of each unchanged pairwise-generated input vector.</returns>
    public static TheoryData<int> PairwiseLegacyStateCases()
    {
        var cases = new TheoryData<int>();
        var scenarios = CreatePairwiseLegacyScenarios();
        foreach (var scenario in scenarios)
        {
            cases.Add(scenario.Index);
        }

        return cases;
    }

    private static IReadOnlyList<SafeMigrationStateScenario> CreatePairwiseLegacyScenarios()
        => SafeMigrationStateSpaceGenerator.GeneratePairwise(
            [
                new SafeMigrationStateDimension("column", ["missing", "matching", "different"]),
                new SafeMigrationStateDimension("index", ["missing", "matching", "different"]),
                new SafeMigrationStateDimension("data", ["empty", "populated"]),
                new SafeMigrationStateDimension("extras", ["none", "unknown"]),
                new SafeMigrationStateDimension("history", ["none", "legacy"]),
            ],
            seed: 0x5AFE2026);

    /// <summary>Either converges or safely blocks each partial legacy shape without preflight mutation.</summary>
    /// <param name="scenarioIndex">The reproducible pairwise input vector selected by the fixed seed.</param>
    [Theory]
    [MemberData(nameof(PairwiseLegacyStateCases))]
    public async Task PairwiseLegacyStates_ConvergeOrBlockWithoutPreflightMutation(
        int scenarioIndex
    )
    {
        // Arrange
        var scenario = CreatePairwiseLegacyScenarios()[scenarioIndex];
        var table = $"state_matrix_{scenario.Index.ToString(CultureInfo.InvariantCulture)}";
        var index = $"ix_{table}_id";
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var columnSql = scenario.Values["column"] switch
        {
            "missing" => string.Empty,
            "matching" => ", `payload` varchar(40) NULL",
            "different" => ", `payload` varchar(20) NULL",
            _ => throw new InvalidOperationException("Unknown generated column state."),
        };

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE `{table}` (`id` int NOT NULL, `alternate_id` int NOT NULL{columnSql});",
            cancellationToken: CancellationToken.None);

        if (scenario.Values["index"] != "missing")
        {
            var indexColumn = scenario.Values["index"] == "matching"
                ? "id"
                : "alternate_id";

            await ExecuteSqlAsync(
                connectionString,
                $"CREATE INDEX `{index}` ON `{table}` (`{indexColumn}`);",
                cancellationToken: CancellationToken.None);
        }

        if (scenario.Values["data"] == "populated")
        {
            var insertColumns = scenario.Values["column"] == "missing"
                ? "`id`, `alternate_id`"
                : "`id`, `alternate_id`, `payload`";

            var insertValues = scenario.Values["column"] == "missing"
                ? "1, 2"
                : "1, 2, 'legacy'";

            await ExecuteSqlAsync(
                connectionString,
                $"INSERT INTO `{table}` ({insertColumns}) VALUES ({insertValues});",
                cancellationToken: CancellationToken.None);
        }

        if (scenario.Values["extras"] == "unknown")
        {
            await ExecuteSqlAsync(
                connectionString,
                $"ALTER TABLE `{table}` ADD COLUMN `legacy_extra` int NULL;",
                cancellationToken: CancellationToken.None);
        }

        if (scenario.Values["history"] == "legacy")
        {
            await ExecuteSqlAsync(
                connectionString,
                """
                CREATE TABLE `__LegacyMigrationsHistory` (`MigrationId` varchar(150) NOT
                NULL); INSERT INTO `__LegacyMigrationsHistory` VALUES ('legacy-unchanged');
                """,
                cancellationToken: CancellationToken.None);
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

        var shouldBlock = scenario.Values["column"] == "different" || scenario.Values["index"] == "different";

        var beforeColumnLength = await ScalarIntAsync(
            connectionString,
            $"""
            SELECT COALESCE(MAX(CHARACTER_MAXIMUM_LENGTH), 0)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = '{table}'
            AND COLUMN_NAME = 'payload';
            """,
            cancellationToken: CancellationToken.None);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions($"pairwise-{scenario.Index}"),
                CancellationToken.None);

        var afterPreflightColumnLength = await ScalarIntAsync(
            connectionString,
            $"""
            SELECT COALESCE(MAX(CHARACTER_MAXIMUM_LENGTH), 0)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            AND TABLE_NAME = '{table}'
            AND COLUMN_NAME = 'payload';
            """,
            cancellationToken: CancellationToken.None);

        if (!shouldBlock)
        {
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        }

        // Assert
        Assert.Equal(beforeColumnLength, afterPreflightColumnLength);
        Assert.Equal(
            shouldBlock
                ? SafeMigrationReportStatus.Blocked
                : SafeMigrationReportStatus.Ready,
            report.Status);

        if (shouldBlock)
        {
            Assert.Contains(
                report.Assessments,
                assessment => assessment.ObservedState == SafeMigrationObservedState.Different);

            return;
        }

        Assert.Equal(
            40,
            await ScalarIntAsync(
                connectionString,
                $"""
                SELECT CHARACTER_MAXIMUM_LENGTH
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = '{table}'
                AND COLUMN_NAME = 'payload';
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                $"""
                SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
                WHERE TABLE_SCHEMA = DATABASE()
                AND TABLE_NAME = '{table}'
                AND INDEX_NAME = '{index}'
                AND COLUMN_NAME = 'id';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Rejects unexpected columns and constraints when strict table equality is required.</summary>
    /// <param name="unexpectedUniqueKey">Whether drift adds an independent unique key rather than a column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictTableDefinition_RejectsUnexpectedColumnsAndConstraints(
        bool unexpectedUniqueKey
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "strict_table",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("code", typeof(string), true, "varchar(30)", maxLength: 30),
            ],
            comment: "strict shape",
            primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "strict_table", ["id"]),
            uniqueConstraints: [new ExpectedUniqueConstraintDefinition("uq_strict_code", "strict_table", ["code"])],
            checkConstraints:
            [
                ExpectedCheckConstraintDefinition.FromExpression(
                    "ck_strict_id",
                    "strict_table",
                    SqlColumnAndInt("id", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)),
            ]);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(definition, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            unexpectedUniqueKey
                ? "ALTER TABLE `strict_table` ADD CONSTRAINT `uq_strict_unexpected` UNIQUE (`id`, `code`);"
                : "ALTER TABLE `strict_table` ADD `legacy` int NULL;",
            cancellationToken: CancellationToken.None);

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
            SafeMigrationObservedState.Different,
            Assert.Single(report.Assessments)
                .ObservedState);
    }

    /// <summary>Recognizes an expected unique index without accepting an unrelated unknown unique key.</summary>
    [Fact]
    public async Task StrictTableDefinition_NormalizesExpectedUniqueIndexWithoutAcceptingUnknownUniqueKeys()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var options = new DbContextOptionsBuilder<StrictUniqueIndexContext>()
            .UseMySql(connectionString, Fixture.ServerVersion)
            .UseMySqlSafeMigrations()
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
            [
                new ExpectedIndexKeyDefinition(
                    column: "email",
                    sortOrder: SafeMigrationIndexSortOrder.Descending,
                    prefixLength: 100),
            ],
            unique: true);

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(definition, SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureIndex(expectedIndex, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-unique-index-preflight"),
            CancellationToken.None);

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-unique-index-postflight"),
            CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "ALTER TABLE `strict_unique_index` RENAME INDEX "
            + "`ux_strict_unique_index_email` TO `ux_strict_unique_index_legacy`;",
            cancellationToken: CancellationToken.None);

        var alias = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-unique-index-alias"),
            CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "ALTER TABLE `strict_unique_index` "
            + "ADD CONSTRAINT `uq_strict_unique_index_unknown` UNIQUE (`id`, `email`);",
            cancellationToken: CancellationToken.None);

        var runtimeException = await AssertOwnedGuardFailureAsync(
            operationToken =>
                ExecuteOperationsAsync(context, builder.Operations, operationToken),
            cancellationToken: CancellationToken.None);

        var drift = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-unique-index-drift"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(
            preflight.Assessments,
            static assessment =>
                Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));

        Assert.Empty(preflight.UnexpectedObjects);
        Assert.Empty(postflight.UnexpectedObjects);

        Assert.Equal(SafeMigrationReportStatus.Ready, alias.Status);
        Assert.All(
            alias.Assessments,
            static assessment =>
                Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));

        Assert.Empty(alias.UnexpectedObjects);

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

    /// <summary>Does not substitute an unknown unique shape for a missing expected unique index.</summary>
    [Fact]
    public async Task StrictTableDefinition_DoesNotTradeMissingExpectedUniqueIndexForUnknownShape()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `strict_unique_substitution` (`id` int NOT NULL, `email` varchar(200)
            NULL,
            PRIMARY KEY (`id`), UNIQUE KEY `ux_unknown_shape` (`id`, `email`));
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "strict_unique_substitution",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("email", typeof(string), true, "varchar(200)", maxLength: 200),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "strict_unique_substitution", ["id"]));

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            definition,
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.EnsureIndex(
            new ExpectedIndexDefinition(
                "ux_expected_email",
                "strict_unique_substitution",
                [new ExpectedIndexKeyDefinition(column: "email")],
                unique: true),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("strict-unique-substitution"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, report.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, report.Assessments[1].ObservedState);
        Assert.Contains(
            report.UnexpectedObjects,
            static value => value is
            {
                ObjectKind: SafeMigrationDatabaseObjectKind.UniqueConstraint,
                Table: "strict_unique_substitution",
                Name: "ux_unknown_shape",
            });
    }

    /// <summary>
    /// Allows an explicitly pending standalone foreign key while rejecting unrelated transition-shape drift.
    /// </summary>
    [Fact]
    public async Task StrictTableDefinition_AllowsPendingStandaloneForeignKeyAndRejectsUnexpectedTransitionShape()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `transition_parents` (`id` int NOT NULL, PRIMARY KEY (`id`));
            CREATE TABLE `transition_children` (`id` int NOT NULL,
            `parent_id` int NULL, PRIMARY KEY (`id`));
            """,
            cancellationToken: CancellationToken.None);

        var options = new DbContextOptionsBuilder<StandaloneForeignKeyContext>()
            .UseMySql(connectionString, Fixture.ServerVersion)
            .UseMySqlSafeMigrations()
            .Options;

        await using var context = new StandaloneForeignKeyContext(options);
        var definition = new ExpectedTableDefinition(
            "transition_children",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "int"),
                new ExpectedColumnDefinition("parent_id", typeof(int), true, "int"),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", "transition_children", ["id"]));

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            definition,
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddForeignKeyIfNotExists(
            "fk_transition_children_parent",
            "transition_children",
            ["parent_id"],
            "transition_parents",
            ["id"],
            onDelete: ReferentialAction.SetNull);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var pending = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("standalone-fk-pending"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("standalone-fk-replay"),
            CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            """
            ALTER TABLE `transition_children` DROP FOREIGN KEY `fk_transition_children_parent`;
            CREATE TABLE `transition_other_parents` (`id` int NOT NULL,
            PRIMARY KEY (`id`)); ALTER TABLE `transition_children` ADD CONSTRAINT `fk_transition_children_unexpected`
            FOREIGN KEY (`parent_id`) REFERENCES `transition_other_parents` (`id`);
            """,
            cancellationToken: CancellationToken.None);

        var drift = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("standalone-fk-drift"),
            CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, pending.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, pending.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, pending.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, pending.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, pending.Assessments[1].Action);

        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(
            replay.Assessments,
            static assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
            });

        Assert.Equal(SafeMigrationReportStatus.Blocked, drift.Status);
        Assert.Equal(SafeMigrationObservedState.Different, drift.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, drift.Assessments[1].ObservedState);
    }

    /// <summary>Does not hide a native unique constraint behind an expected nonunique index.</summary>
    [Fact]
    public async Task UnexpectedObjectInventory_DoesNotAliasUniqueConstraintToExpectedNonUniqueIndex()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE `non_unique_alias` (`id` int NOT NULL, `code` varchar(30) NULL,
            CONSTRAINT `ix_non_unique_alias_code` UNIQUE (`code`));
            """,
            cancellationToken: CancellationToken.None);

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

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("non-unique-alias"),
                CancellationToken.None);

        // Assert
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
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Defines an independently owned unique index for strict table-contract projection.</summary>
    protected sealed class StrictUniqueIndexContext : DbContext
    {
        /// <summary>Initializes the explicit dependencies of this isolated test helper.</summary>
        /// <param name="options">The isolated model and provider options.</param>
        public StrictUniqueIndexContext(
            DbContextOptions<StrictUniqueIndexContext> options
        ) : base(options) { }

        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<StrictUniqueIndexEntry>(entity =>
            {
                entity.ToTable("strict_unique_index");
                entity
                    .HasKey(entry => entry.Id)
                    .HasName("PRIMARY");

                entity
                    .Property(entry => entry.Id)
                    .HasColumnName("id")
                    .HasColumnType("int")
                    .ValueGeneratedNever();

                entity
                    .Property(entry => entry.Email)
                    .HasColumnName("email")
                    .HasColumnType("varchar(200)")
                    .HasMaxLength(200);

                entity
                    .HasIndex(entry => entry.Email)
                    .IsUnique()
                    .IsDescending()
                    .HasPrefixLength(100)
                    .HasDatabaseName("ux_strict_unique_index_email");
            });
        }
    }

    private sealed class StrictUniqueIndexEntry
    {
        public int Id { get; init; }

        public string? Email { get; init; }
    }

    /// <summary>Defines an independently owned foreign key for pending table-contract projection.</summary>
    protected sealed class StandaloneForeignKeyContext : DbContext
    {
        /// <summary>Initializes the explicit dependencies of this isolated test helper.</summary>
        /// <param name="options">The isolated model and provider options.</param>
        public StandaloneForeignKeyContext(
            DbContextOptions<StandaloneForeignKeyContext> options
        ) : base(options) { }

        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<TransitionParent>(entity =>
            {
                entity.ToTable("transition_parents");
                entity
                    .HasKey(value => value.Id)
                    .HasName("PRIMARY");

                entity
                    .Property(value => value.Id)
                    .HasColumnName("id")
                    .HasColumnType("int")
                    .ValueGeneratedNever();
            });

            modelBuilder.Entity<TransitionChild>(entity =>
            {
                entity.ToTable("transition_children");
                entity
                    .HasKey(value => value.Id)
                    .HasName("PRIMARY");

                entity
                    .Property(value => value.Id)
                    .HasColumnName("id")
                    .HasColumnType("int")
                    .ValueGeneratedNever();

                entity
                    .Property(value => value.ParentId)
                    .HasColumnName("parent_id")
                    .HasColumnType("int");

                entity
                    .HasOne<TransitionParent>()
                    .WithMany()
                    .HasForeignKey(value => value.ParentId)
                    .OnDelete(DeleteBehavior.SetNull)
                    .HasConstraintName("fk_transition_children_parent");
            });
        }
    }

    private sealed class TransitionParent
    {
        public int Id { get; init; }
    }

    private sealed class TransitionChild
    {
        public int Id { get; init; }

        public int? ParentId { get; init; }
    }
}
