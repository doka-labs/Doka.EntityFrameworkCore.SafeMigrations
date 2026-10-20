namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Exercises current generated-source lifetimes rather than stale initial catalog facts.</summary>
public sealed class PostgreSqlGeneratedProjectionIntegrationTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the shared native container with one isolated database per test.</summary>
    /// <param name="fixture">The assembly-owned native fixture.</param>
    public PostgreSqlGeneratedProjectionIntegrationTests(PostgreSqlContainerFixture fixture) : base(fixture)
    {
    }

    /// <summary>Removed inputs cannot borrow old types; unrelated removals preserve current bindings.</summary>
    /// <param name="referenced">Whether the accepted DROP removes the captured expression's source.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedCaseUsesCurrentSourceLifetimeAfterDrop(bool referenced)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE source_lifetime (value integer, unrelated integer);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(referenced ? "value" : "unrelated", "source_lifetime");
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "source_lifetime", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN value > 0 THEN TRUE ELSE FALSE END", stored: true));

        builder.CreateIndexIfNotExists("ix_source_result", "source_lifetime", columns: ["result"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-source-drop-lifetime"), CancellationToken.None);

        if (!referenced)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(referenced ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(referenced ? SafeMigrationAction.RejectPrerequisiteMissing : SafeMigrationAction.Apply,
            report.Assessments[1].Action);

        Assert.Equal(referenced ? 0 : 1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid = 'source_lifetime'::regclass "
            + "AND attname = 'result' AND NOT attisdropped;"));
    }

    /// <summary>A generated source cannot erase occupied native or accepted index namespace identities.</summary>
    /// <param name="symbol">The native or accepted object occupying the requested index name.</param>
    [Theory]
    [InlineData("index")]
    [InlineData("table")]
    [InlineData("accepted_index")]
    [InlineData("accepted_unique")]
    [InlineData("accepted_primary")]
    public async Task GeneratedIndexPreservesOccupiedNamespace(string symbol)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE source_lifetime (value integer NOT NULL, unrelated integer); "
            + (symbol == "index" ? "CREATE INDEX ix_source_result ON source_lifetime(value);"
                : symbol == "table" ? "CREATE TABLE ix_source_result (id integer);" : string.Empty));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (symbol == "accepted_index")
        {
            builder.CreateIndexIfNotExists("ix_source_result", "source_lifetime", columns: ["value"]);
        }
        else if (symbol == "accepted_unique")
        {
            builder.AddUniqueConstraintIfNotExists("ix_source_result", "source_lifetime", columns: ["value"]);
        }
        else if (symbol == "accepted_primary")
        {
            builder.AddPrimaryKeyIfNotExists("ix_source_result", "source_lifetime", columns: ["value"]);
        }

        builder.DropColumnIfExists("unrelated", "source_lifetime");
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "source_lifetime", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN value > 0 THEN TRUE ELSE FALSE END", stored: true));

        builder.CreateIndexIfNotExists("ix_source_result", "source_lifetime", columns: ["result"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-occupied-namespace"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.True(report.Assessments[^1].Action is SafeMigrationAction.RejectDifferent
            or SafeMigrationAction.RejectPrerequisiteMissing);
    }

    /// <summary>An accepted exact index remains idempotent across the narrow generated-index restoration.</summary>
    [Fact]
    public async Task GeneratedIndexRestorationRetainsAcceptedExactIdentity()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE source_lifetime (value integer, unrelated integer);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", "source_lifetime");
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "source_lifetime", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN value > 0 THEN TRUE ELSE FALSE END", stored: true));

        builder.CreateIndexIfNotExists("ix_source_result", "source_lifetime", columns: ["result"]);
        builder.CreateIndexIfNotExists("ix_source_result", "source_lifetime", columns: ["result"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-accepted-identity"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[3].Action);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_class WHERE relname = 'ix_source_result' AND relkind = 'i';"));
    }

    /// <summary>Replacing a generated member requires explicit removal of its accepted dependent index.</summary>
    /// <param name="explicitDrop">Whether the ordered stream explicitly removes its accepted dependent index.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedIndexSourceReplacementRequiresExplicitDrop(bool explicitDrop)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE source_lifetime (value integer, unrelated integer);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AddGenerated();
        builder.CreateIndexIfNotExists("ix_source_result", "source_lifetime", columns: ["result"]);
        if (explicitDrop)
        {
            builder.DropIndexIfExists("ix_source_result", "source_lifetime");
        }

        builder.DropColumnIfExists("result", "source_lifetime");
        AddGenerated();
        builder.CreateIndexIfNotExists("ix_source_result", "source_lifetime", columns: ["result"]);

        // Act
        SafeMigrationRunReport? report = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("generated-index-recreated-source"), CancellationToken.None);

            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        // Assert
        if (explicitDrop)
        {
            Assert.Null(exception);
            Assert.Equal(SafeMigrationAction.Apply, report!.Assessments[1].Action);
            Assert.Equal(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        }
        else
        {
            Assert.Contains("Drop the index explicitly", Assert.IsType<InvalidOperationException>(exception).Message,
                StringComparison.Ordinal);
        }

        Assert.Equal(explicitDrop ? 1 : 0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_class WHERE relname = 'ix_source_result' AND relkind = 'i';"));

        void AddGenerated() => builder.AddColumnIfNotExistsFromModel(operationBuilder =>
            operationBuilder.AddColumn<bool>("result", "source_lifetime", type: "boolean", nullable: true,
                computedColumnSql: "CASE WHEN value > 0 THEN TRUE ELSE FALSE END", stored: true));
    }

    /// <summary>Every included member must survive the accepted prefix independently of the generated key.</summary>
    /// <param name="removed">Whether the INCLUDE target was removed instead of an unrelated sibling.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedIndexRestorationRequiresCurrentIncludedColumns(bool removed)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE source_lifetime (value integer, payload integer, unrelated integer);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(removed ? "payload" : "unrelated", "source_lifetime");
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "source_lifetime", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN value > 0 THEN TRUE ELSE FALSE END", stored: true));

        builder.EnsureIndex(new ExpectedIndexDefinition("ix_source_result", "source_lifetime",
            [new ExpectedIndexKeyDefinition(column: "result")], includedColumns: ["payload"]),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-current-include"), CancellationToken.None);

        if (!removed)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(removed ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(removed ? SafeMigrationAction.RejectPrerequisiteMissing : SafeMigrationAction.Apply,
            report.Assessments[^1].Action);
    }

    /// <summary>Opaque SQL cannot inherit an earlier native absence or source certificate.</summary>
    [Fact]
    public async Task GeneratedIndexRestorationDoesNotCrossOpaqueSql()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE source_lifetime (value integer, unrelated integer);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists("unrelated", "source_lifetime");
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "source_lifetime", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN value > 0 THEN TRUE ELSE FALSE END", stored: true));

        builder.Sql("SELECT 1;");
        builder.CreateIndexIfNotExists("ix_source_result", "source_lifetime", columns: ["result"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-index-opaque-boundary"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
    }

    /// <summary>A recreated owner uses accepted new declarations, never original source-type evidence.</summary>
    /// <param name="compatible">Whether the new lifetime declares the required builtin integer source.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedCaseRebindsOnlyAcceptedRecreatedOwner(bool compatible)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE recreated_source (value uuid);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("recreated_source");
        if (compatible)
        {
            builder.CreateTableIfNotExists("recreated_source", table => new
            {
                value = table.Column<int>("integer", nullable: true),
            });
        }
        else
        {
            builder.CreateTableIfNotExists("recreated_source", table => new
            {
                value = table.Column<Guid>("uuid", nullable: true),
            });
        }

        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "result", "recreated_source", type: "boolean", nullable: true,
            computedColumnSql: "CASE WHEN value > 0 THEN TRUE ELSE FALSE END", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-source-recreated-owner"), CancellationToken.None);

        if (compatible)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(compatible ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(compatible ? SafeMigrationAction.Apply : SafeMigrationAction.RejectUnsupported,
            report.Assessments[^1].Action);
    }

    /// <summary>Final-writer reduction retains full-stream physical aliases after a transformed table probe.</summary>
    [Fact]
    public async Task GeneratedRenamePostflightRetainsFullStreamSchemaIdentity()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA custom; "
            + "CREATE TABLE custom.a (source integer NOT NULL, derived integer GENERATED ALWAYS AS "
            + "(CASE WHEN source > 0 THEN source ELSE 0 END) STORED); CREATE TABLE custom.b (id integer); "
            + "INSERT INTO custom.a (source) VALUES (2);");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET search_path = custom, public;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("a", table => new
        {
            source = table.Column<int>("integer", nullable: false),
            derived = table.Column<int>("integer", nullable: true,
                computedColumnSql: "CASE WHEN source > 0 THEN source ELSE 0 END", stored: true),
        });

        builder.RenameColumnIfExists("source", "a", "renamed");
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
            "derived", "a", type: "integer", nullable: true,
            computedColumnSql: "CASE WHEN renamed > 0 THEN renamed ELSE 0 END", stored: true));

        builder.AddColumnIfNotExists<int>("extra", "b", type: "integer", nullable: true);
        builder.DropColumnIfExists("extra", "b", schema: "custom");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-rename-schema-postflight"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal("2:2", await ScalarStringAsync(connectionString,
            "SELECT renamed::text || ':' || derived::text FROM custom.a;"));

        Assert.Equal(0, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid = 'custom.b'::regclass "
            + "AND attname = 'extra' AND NOT attisdropped;"));
    }

    /// <summary>Native-equivalent CHECK reassertion preserves the complete renamed strict-table lineage.</summary>
    [Fact]
    public async Task CapturedCheckReassertionRetainsRenamedStrictTablePostflight()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE check_lineage (leftbound integer NOT NULL, payload integer NOT NULL DEFAULT 1, "
            + "CONSTRAINT ck_check_lineage CHECK (leftbound >= 0)); "
            + "COMMENT ON COLUMN check_lineage.payload IS 'retained payload'; "
            + "INSERT INTO check_lineage(leftbound) VALUES(2);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("check_lineage", table => new
        {
            leftbound = table.Column<int>("integer", nullable: false),
            payload = table.Column<int>("integer", nullable: false, defaultValue: 1, comment: "retained payload"),
        }, constraints: table => table.CheckConstraint("ck_check_lineage", "leftbound >= 0"));

        builder.RenameColumnIfExists("leftbound", "check_lineage", "score");
        builder.AddCheckConstraintIfNotExists("ck_check_lineage", "check_lineage", "score >= 0");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-check-renamed-strict-preflight"), CancellationToken.None);

        // WHY: Observe both independent contracts even when the preflight
        // regression rejects this valid isolated native stream prematurely.
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-check-renamed-strict-postflight"), CancellationToken.None);

        var violation = await Record.ExceptionAsync(() => context.Database.ExecuteSqlRawAsync(
            "INSERT INTO check_lineage(score) VALUES(-1);"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal("23514", Assert.IsType<PostgresException>(violation).SqlState);
        Assert.Equal("2:1:retained payload", await ScalarStringAsync(connectionString,
            "SELECT score::text || ':' || payload::text || ':' || pg_catalog.col_description("
            + "'check_lineage'::regclass, 2) FROM check_lineage;"));
    }

    /// <summary>Active callbacks prevent preflight from certifying unrelated absence through a known rename.</summary>
    [Fact]
    public async Task CapturedCheckRenameDoesNotCertifyEventTriggerEffects()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE check_callback (source integer NOT NULL, CONSTRAINT ck_check_callback CHECK(source>=0)); "
            + "CREATE FUNCTION check_callback_effect() RETURNS event_trigger LANGUAGE plpgsql "
            + "AS $$ BEGIN PERFORM 1; END; $$; CREATE EVENT TRIGGER check_callback_effect ON ddl_command_end "
            + "EXECUTE FUNCTION check_callback_effect();");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("check_callback", table => new
        {
            source = table.Column<int>("integer", nullable: false),
        }, constraints: table => table.CheckConstraint("ck_check_callback", "source>=0"));

        builder.RenameColumnIfExists("source", "check_callback", "score");
        builder.AddCheckConstraintIfNotExists("ck_check_callback", "check_callback", "score>=0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-check-callback-boundary"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.Equal("projected_event_trigger_state_unknown", report.Assessments[^1].AnalysisCode);
        Assert.Equal(1,
            Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[^1].DeferredOrigin).OperationOrdinal);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='check_callback'::regclass "
            + "AND attname='source' AND NOT attisdropped;"));
    }

}
