namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the Tables PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlTablesIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlTablesIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Generated Check Constraint Is Accepted By Preflight Runtime And Postflight.</summary>
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
                amount = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table => table.CheckConstraint(
                "ck_generated_check_orders_amount",
                "\"amount\" >= 0"));

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

        var failure = await Record.ExceptionAsync(() => ExecuteSqlAsync(
            connectionString,
            "INSERT INTO generated_check_orders (amount) VALUES (-1);",
            cancellationToken: CancellationToken.None));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        var exception = Assert.IsType<PostgresException>(failure);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.True(Assert.Single(postflight.Assessments)
            .PostconditionSatisfied);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    /// <summary>Granular Convergence Completes Existing Partial Table And Is Idempotent.</summary>
    [Fact]
    public async Task GranularConvergence_CompletesExistingPartialTableAndIsIdempotent()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE module_state (id integer NOT NULL PRIMARY KEY); "
            + "INSERT INTO module_state (id) VALUES (1);",
            cancellationToken: CancellationToken.None);

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

        // Act
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Assert
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'module_state'
                AND column_name = 'display_name';
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = current_schema()
                AND c.relname = 'ix_module_state_display_name'
                AND c.relkind = 'i';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Existence Only Accepts An Existing Table Container Without Mutating Shape Drift.</summary>
    [Fact]
    public async Task ExistenceOnly_AcceptsAnExistingTableContainerWithoutMutatingShapeDrift()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE existence_shape (id character varying(20) NOT NULL);",
            cancellationToken: CancellationToken.None);

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

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("test-instance"),
                CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);

        Assert.Equal(
            20,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT character_maximum_length
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'existence_shape'
                AND column_name = 'id';
                """,
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = 'existence_shape'
                AND column_name = 'Payload';
                """,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Pairwise Legacy States Converge Or Block Without Preflight Mutation.</summary>
    /// <param name="scenarioIndex">The stable original pairwise row identity.</param>
    /// <param name="columnState">The independent existing column state.</param>
    /// <param name="indexState">The independent existing index state.</param>
    /// <param name="dataState">Whether fixture rows already exist.</param>
    /// <param name="extraState">Whether an unknown column exists.</param>
    /// <param name="historyState">Whether unrelated legacy history exists.</param>
    [Theory]
    [MemberData(nameof(PairwiseLegacyCases))]
    public async Task PairwiseLegacyStates_ConvergeOrBlockWithoutPreflightMutation(
        int scenarioIndex,
        string columnState,
        string indexState,
        string dataState,
        string extraState,
        string historyState
    )
    {
        // Arrange
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["column"] = columnState,
            ["index"] = indexState,
            ["data"] = dataState,
            ["extras"] = extraState,
            ["history"] = historyState,
        };

        var table = $"state_matrix_{scenarioIndex.ToString(CultureInfo.InvariantCulture)}";
        var index = $"ix_{table}_id";
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var columnSql = values["column"] switch
        {
            "missing" => string.Empty,
            "matching" => ", payload character varying(40) NULL",
            "different" => ", payload character varying(20) NULL",
            _ => throw new InvalidOperationException("Unknown generated column state."),
        };

        await ExecuteSqlAsync(
            connectionString,
            $"CREATE TABLE {table} (id integer NOT NULL, alternate_id integer NOT NULL{columnSql});",
            cancellationToken: CancellationToken.None);

        if (values["index"] != "missing")
        {
            var indexColumn = values["index"] == "matching"
                ? "id"
                : "alternate_id";

            await ExecuteSqlAsync(
                connectionString,
                $"CREATE INDEX {index} ON {table} ({indexColumn});",
                cancellationToken: CancellationToken.None);
        }

        if (values["data"] == "populated")
        {
            var insertColumns = values["column"] == "missing"
                ? "id, alternate_id"
                : "id, alternate_id, payload";

            var insertValues = values["column"] == "missing"
                ? "1, 2"
                : "1, 2, 'legacy'";

            await ExecuteSqlAsync(
                connectionString,
                $"INSERT INTO {table} ({insertColumns}) VALUES ({insertValues});",
                cancellationToken: CancellationToken.None);
        }

        if (values["extras"] == "unknown")
        {
            await ExecuteSqlAsync(
                connectionString,
                $"ALTER TABLE {table} ADD COLUMN legacy_extra integer NULL;",
                cancellationToken: CancellationToken.None);
        }

        if (values["history"] == "legacy")
        {
            await ExecuteSqlAsync(
                connectionString,
                """
                CREATE TABLE "__LegacyMigrationsHistory" ("MigrationId" text NOT NULL);
                INSERT INTO "__LegacyMigrationsHistory" VALUES ('legacy-unchanged');
                """,
                cancellationToken: CancellationToken.None);
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
            $"""
            SELECT COALESCE(MAX(character_maximum_length), 0)
            FROM information_schema.columns
            WHERE table_schema = current_schema()
            AND table_name = '{table}'
            AND column_name = 'payload';
            """,
            cancellationToken: CancellationToken.None);

        var shouldBlock = values["column"] == "different" || values["index"] == "different";
        int? finalColumnLength = null;
        int? finalIndexCount = null;

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions($"pairwise-{scenarioIndex}"),
                CancellationToken.None);

        var afterPreflightColumnLength = await ScalarIntAsync(
            connectionString,
            $"""
            SELECT COALESCE(MAX(character_maximum_length), 0)
            FROM information_schema.columns
            WHERE table_schema = current_schema()
            AND table_name = '{table}'
            AND column_name = 'payload';
            """,
            cancellationToken: CancellationToken.None);

        if (!shouldBlock)
        {
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

            finalColumnLength = await ScalarIntAsync(
                connectionString,
                $"""
                SELECT character_maximum_length
                FROM information_schema.columns
                WHERE table_schema = current_schema()
                AND table_name = '{table}'
                AND column_name = 'payload';
                """,
                cancellationToken: CancellationToken.None);

            finalIndexCount = await ScalarIntAsync(
                connectionString,
                $"""
                SELECT COUNT(*) FROM pg_catalog.pg_index i
                JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid
                JOIN pg_catalog.pg_attribute a ON a.attrelid = i.indrelid
                AND a.attnum = ANY(i.indkey) WHERE c.relname = '{index}'
                AND a.attname = 'id';
                """,
                cancellationToken: CancellationToken.None);
        }

        // Assert
        Assert.Equal(beforeColumnLength, afterPreflightColumnLength);
        Assert.Equal(3, report.Assessments.Count);
        Assert.Equal([0, 1, 2], report.Assessments.Select(static assessment => assessment.Ordinal));
        Assert.Equal(
            shouldBlock
                ? SafeMigrationReportStatus.Blocked
                : SafeMigrationReportStatus.Ready,
            report.Status);

        if (shouldBlock)
        {
            Assert.Contains(report.Assessments,
                static assessment => assessment.ObservedState == SafeMigrationObservedState.Different);
        }
        else
        {
            Assert.Equal(40, finalColumnLength);
            Assert.Equal(1, finalIndexCount);
        }
    }

    /// <summary>
    /// Discovers each unchanged deterministic pairwise state as an independent primitive-valued case.
    /// </summary>
    /// <returns>The original scenario identity and its five state dimensions.</returns>
    public static IEnumerable<object[]> PairwiseLegacyCases() => SafeMigrationStateSpaceGenerator
        .GeneratePairwise(
            [
                new SafeMigrationStateDimension("column", ["missing", "matching", "different"]),
                new SafeMigrationStateDimension("index", ["missing", "matching", "different"]),
                new SafeMigrationStateDimension("data", ["empty", "populated"]),
                new SafeMigrationStateDimension("extras", ["none", "unknown"]),
                new SafeMigrationStateDimension("history", ["none", "legacy"]),
            ],
            seed: 0x5AFE2026)
        .Select(static scenario => new object[]
        {
            scenario.Index,
            scenario.Values["column"],
            scenario.Values["index"],
            scenario.Values["data"],
            scenario.Values["extras"],
            scenario.Values["history"],
        });

    /// <summary>Strict Table Definition Rejects Unexpected Columns And Constraints.</summary>
    /// <param name="extraKind">The independent unexpected column or constraint variant.</param>
    [Theory]
    [InlineData("column")]
    [InlineData("unique")]
    public async Task StrictTableDefinition_RejectsUnexpectedColumnsAndConstraints(
        string extraKind
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition(
            "strict_table",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("code", typeof(string), true, "character varying(30)", maxLength: 30),
            ],
            comment: "strict shape",
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_strict_table", "strict_table", ["id"]),
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

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            extraKind == "column"
                ? "ALTER TABLE strict_table ADD legacy integer NULL;"
                : "ALTER TABLE strict_table ADD CONSTRAINT uq_strict_unexpected UNIQUE (id, code);",
            cancellationToken: CancellationToken.None);

        // Act
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

    /// <summary>
    /// Strict Table Definition Allows Pending Standalone Foreign Key And Rejects Unexpected Transition Shape.
    /// </summary>
    [Fact]
    public async Task StrictTableDefinition_AllowsPendingStandaloneForeignKeyAndRejectsUnexpectedTransitionShape()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE transition_parents (id integer NOT NULL, CONSTRAINT pk_transition_parents PRIMARY
            KEY (id)); CREATE TABLE transition_children (id integer NOT NULL,
            parent_id integer NULL, CONSTRAINT pk_transition_children PRIMARY KEY (id));
            """,
            cancellationToken: CancellationToken.None);

        var options = new DbContextOptionsBuilder<StandaloneForeignKeyContext>()
            .UseNpgsql(connectionString)
            .UsePostgreSqlSafeMigrations()
            .Options;

        await using var context = new StandaloneForeignKeyContext(options);
        var definition = new ExpectedTableDefinition(
            "transition_children",
            [
                new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                new ExpectedColumnDefinition("parent_id", typeof(int), true, "integer"),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition(
                "pk_transition_children",
                "transition_children",
                ["id"]));

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
            ALTER TABLE transition_children DROP CONSTRAINT fk_transition_children_parent; CREATE TABLE
            transition_other_parents (id integer NOT NULL,
            CONSTRAINT pk_transition_other_parents PRIMARY KEY (id)); ALTER TABLE transition_children ADD
            CONSTRAINT fk_transition_children_unexpected FOREIGN KEY (parent_id) REFERENCES
            transition_other_parents (id);
            """,
            cancellationToken: CancellationToken.None);

        var drift = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("standalone-fk-drift"),
            CancellationToken.None);

        // Assert
        Assert.Equal(2, pending.Assessments.Count);
        Assert.Equal(SafeMigrationReportStatus.Ready, pending.Status);
        Assert.Equal(SafeMigrationObservedState.Matching, pending.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, pending.Assessments[1].ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, pending.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, pending.Assessments[1].Action);

        Assert.Equal(2, replay.Assessments.Count);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(
            replay.Assessments,
            static assessment =>
            {
                Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState);
                Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
            });

        Assert.Equal(2, drift.Assessments.Count);
        Assert.Equal(SafeMigrationReportStatus.Blocked, drift.Status);
        Assert.Equal(SafeMigrationObservedState.Different, drift.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, drift.Assessments[1].ObservedState);
    }
}
