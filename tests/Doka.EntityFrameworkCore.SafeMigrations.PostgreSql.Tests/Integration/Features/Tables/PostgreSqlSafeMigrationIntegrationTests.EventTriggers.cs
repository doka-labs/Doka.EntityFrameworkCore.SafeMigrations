namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Event-trigger writes invalidate the empty-table certificate before a later CHECK.</summary>
    /// <param name="typedCreate">Whether creation is an ordinary EF operation rather than a safe operation.</param>
    /// <param name="enabledMode">The origin-session activation mode.</param>
    [Theory]
    [InlineData(false, "ENABLE")]
    [InlineData(true, "ENABLE")]
    [InlineData(false, "ENABLE ALWAYS")]
    public async Task EventTriggers_CreateThenCheckDefersTriggerRows(bool typedCreate, string enabledMode)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await InstallEventTriggerAsync(connectionString, "INSERT INTO event_created_values VALUES (-1);", enabledMode);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerCreate(builder, typedCreate);
        AppendEventTriggerCheck(builder);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-rows"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [builder.Operations[1]]));
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM event_created_values WHERE id=-1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        var assessment = report.Assessments[1];
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, assessment.Action);
        Assert.Equal("projected_event_trigger_state_unknown", assessment.AnalysisCode);
        Assert.Equal(0, Assert.IsType<SafeMigrationDeferredOrigin>(assessment.DeferredOrigin).OperationOrdinal);
        Assert.Equal("P1003", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(1, rows);
    }

    /// <summary>Disabled and inactive replica triggers do not erase an ordinary creation proof.</summary>
    /// <param name="enabledMode">The event trigger activation mode for an origin session.</param>
    [Theory]
    [InlineData("DISABLE")]
    [InlineData("ENABLE REPLICA")]
    public async Task EventTriggers_InactiveTriggerRetainsReady(string enabledMode)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await InstallEventTriggerAsync(connectionString, "INSERT INTO event_created_values VALUES (-1);", enabledMode);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerCreate(builder, typedCreate: false);
        AppendEventTriggerCheck(builder);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-inactive"));
        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM event_created_values;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.Null(assessment.DeferredOrigin));
        Assert.Equal(0, rows);
    }

    /// <summary>Trigger DDL invalidates an index match even when the preceding table itself remains valid.</summary>
    [Fact]
    public async Task EventTriggers_CreateThenEnsureIndexDefersStructuralMatch()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id);");
        await InstallEventTriggerAsync(connectionString, "DROP INDEX ix_event_existing; "
            + "CREATE INDEX ix_event_existing ON event_existing_values(value);", "ENABLE");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerCreate(builder, typedCreate: false);
        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-structure"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [builder.Operations[1]]));
        var changed = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM pg_catalog.pg_indexes "
            + "WHERE indexname='ix_event_existing' AND indexdef LIKE '%(value)%';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[1].Action);
        Assert.Equal("projected_event_trigger_state_unknown", report.Assessments[1].AnalysisCode);
        Assert.Equal("P1001", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(1, changed);
    }

    /// <summary>A matching safe CREATE emits no DDL and therefore preserves the later CHECK proof.</summary>
    [Fact]
    public async Task EventTriggers_MatchingCreateRetainsReady()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE event_created_values(id integer NOT NULL);");
        await InstallEventTriggerAsync(connectionString, "INSERT INTO event_created_values VALUES (-1);", "ENABLE");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerCreate(builder, typedCreate: false);
        AppendEventTriggerCheck(builder);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-noop"));
        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM event_created_values;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.All(report.Assessments, static assessment => Assert.Null(assessment.DeferredOrigin));
        Assert.Equal(0, rows);
    }

    /// <summary>Unsupported operation contracts stay blocked even after trigger-risk creation.</summary>
    [Fact]
    public async Task EventTriggers_UnsupportedExpressionRemainsBlocked()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await InstallEventTriggerAsync(connectionString, "INSERT INTO event_created_values VALUES (-1);", "ENABLE");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerCreate(builder, typedCreate: false);
        builder.AddCheckConstraintIfNotExists("ck_event_values", "event_created_values", "id > 0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-unsupported"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Null(report.Assessments[1].DeferredOrigin);
    }

    /// <summary>Provenance uses the complete stream ordinal, including preceding ordinary EF data operations.</summary>
    [Fact]
    public async Task EventTriggers_OriginCountsOrdinaryPrefix()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE event_prefix(id integer NOT NULL);");
        await InstallEventTriggerAsync(connectionString, "INSERT INTO event_created_values VALUES (-1);", "ENABLE");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.InsertData("event_prefix", "id", 1);
        AppendEventTriggerCreate(builder, typedCreate: false);
        AppendEventTriggerCheck(builder);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-ordinal"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[2].Action);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[2].DeferredOrigin);

        Assert.Equal(1, origin.OperationOrdinal);
    }

    /// <summary>Activation follows replication-role rules rather than the trigger's enabled flag alone.</summary>
    /// <param name="enabledMode">The trigger activation clause.</param>
    /// <param name="replicationRole">The session role used by both analysis and execution.</param>
    /// <param name="fires">Whether PostgreSQL actually invokes the fixture function.</param>
    [Theory]
    [InlineData("ENABLE", "local", true)]
    [InlineData("ENABLE", "replica", false)]
    [InlineData("ENABLE REPLICA", "replica", true)]
    [InlineData("ENABLE ALWAYS", "replica", true)]
    public async Task EventTriggers_ActivationRespectsReplicationRole(
        string enabledMode,
        string replicationRole,
        bool fires
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await InstallEventTriggerAsync(connectionString, "INSERT INTO event_created_values VALUES (-1);", enabledMode);
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlAsync(
            $"SELECT pg_catalog.set_config('session_replication_role', {replicationRole}, false);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerCreate(builder, typedCreate: false);
        AppendEventTriggerCheck(builder);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-replication"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM event_created_values;");

        // Assert
        Assert.Equal(fires ? SafeMigrationReportStatus.RuntimeValidationRequired : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(fires ? SafeMigrationAction.ValidateAtRuntime : SafeMigrationAction.Apply,
            report.Assessments[1].Action);
        Assert.Equal(fires ? 1 : 0, rows);
    }

    /// <summary>Session-wide disabling certifies absence; versions without that option retain trigger risk.</summary>
    [Fact]
    public async Task EventTriggers_SessionDisableOrMissingOptionUsesActualServerSetting()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await InstallEventTriggerAsync(connectionString, "INSERT INTO event_created_values VALUES (-1);", "ENABLE");
        var hasOption = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_settings WHERE name='event_triggers';") != 0;

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        if (hasOption)
        {
            await context.Database.ExecuteSqlRawAsync("SET event_triggers=off;");
        }

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerCreate(builder, typedCreate: false);
        AppendEventTriggerCheck(builder);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-session-disable"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM event_created_values;");

        // Assert
        Assert.Equal(hasOption ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.RuntimeValidationRequired,
            report.Status);
        Assert.Equal(hasOption ? SafeMigrationAction.Apply : SafeMigrationAction.ValidateAtRuntime,
            report.Assessments[1].Action);
        Assert.Equal(hasOption ? 0 : 1, rows);
    }

    /// <summary>Revoked catalog visibility defers unknown effects rather than proving no trigger exists.</summary>
    [Fact]
    public async Task EventTriggers_RevokedCatalogVisibilityRequiresRuntimeValidation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var role = "event_reader_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(connectionString, "CREATE ROLE " + role + " LOGIN PASSWORD 'event-reader-fixture'; "
            + "GRANT USAGE, CREATE ON SCHEMA public TO " + role + "; "
            + "REVOKE SELECT ON pg_catalog.pg_event_trigger FROM PUBLIC;");
        var readerConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = role,
            Password = "event-reader-fixture",
        }.ConnectionString;

        await using var context = CreateContext(readerConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerCreate(builder, typedCreate: false);
        AppendEventTriggerCheck(builder);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-visibility"));
        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM event_created_values;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[1].Action);
        Assert.Equal("projected_event_trigger_visibility_unknown", report.Assessments[1].AnalysisCode);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[1].DeferredOrigin);

        Assert.Equal(0, origin.OperationOrdinal);
        Assert.Equal(0, rows);
    }

    /// <summary>
    /// Trigger evidence and the preceding DDL origin belong to one analysis, not the scoped analyzer lifetime.
    /// </summary>
    [Fact]
    public async Task EventTriggers_DisablingBetweenAnalysesClearsCapturedOrigin()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await InstallEventTriggerAsync(connectionString, "INSERT INTO event_created_values VALUES (-1);", "ENABLE");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerCreate(builder, typedCreate: false);
        AppendEventTriggerCheck(builder);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var enabled = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-invocation-enabled"));
        await ExecuteSqlAsync(connectionString, "ALTER EVENT TRIGGER event_fixture DISABLE;");
        var disabled = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-invocation-disabled"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, enabled.Status);
        Assert.NotNull(enabled.Assessments[1].DeferredOrigin);
        Assert.Equal(SafeMigrationReportStatus.Ready, disabled.Status);
        Assert.All(disabled.Assessments, static assessment => Assert.Null(assessment.DeferredOrigin));
    }


    /// <summary>
    /// Initial live row violations stay blocked until an actual preceding operation invalidates evidence.
    /// </summary>
    [Fact]
    public async Task EventTriggers_InitialLiveViolationRemainsBlocked()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_created_values(id integer NOT NULL); INSERT INTO event_created_values VALUES(-1);");
        await InstallEventTriggerAsync(connectionString, "INSERT INTO event_created_values VALUES (-1);", "ENABLE");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerCheck(builder);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-event-trigger-initial-live-violation"));
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, Assert.Single(report.Assessments).Action);
        Assert.Null(report.Assessments[0].DeferredOrigin);
        Assert.Equal("P1003", Assert.IsType<PostgresException>(exception).SqlState);
    }

    /// <summary>The sql_drop family can recreate a dropped owner before the next safe operation.</summary>
    [Fact]
    public async Task EventTriggers_DropRecreatesOwnerAndDefersStaleTombstone()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_drop_owner(id integer NOT NULL); "
            + "CREATE FUNCTION event_drop_trigger_fixture() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN "
            + "IF EXISTS (SELECT 1 FROM pg_catalog.pg_event_trigger_dropped_objects() "
            + "WHERE object_type='table' AND schema_name='public' AND object_name='event_drop_owner') THEN "
            + "CREATE TABLE event_drop_owner(id integer NOT NULL, recreated_value integer NULL); "
            + "END IF; END; $$; CREATE EVENT TRIGGER event_drop_fixture ON sql_drop "
            + "EXECUTE FUNCTION event_drop_trigger_fixture();");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTable("event_drop_owner");
        builder.AddColumnIfNotExists<int>("recreated_value", "event_drop_owner", type: "integer", nullable: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-sql-drop-recreated-owner"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var actual = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [builder.Operations[1]],
            new SafeMigrationRunOptions("postgresql-sql-drop-actual-owner"));
        await ExecuteOperationsAsync(context, [builder.Operations[1]]);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[1].Action);
        Assert.Equal("projected_event_trigger_state_unknown", report.Assessments[1].AnalysisCode);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[1].DeferredOrigin);

        Assert.Equal(0, origin.OperationOrdinal);
        Assert.Equal(typeof(DropTableOperation).FullName, origin.OperationType);
        Assert.Equal(SafeMigrationReportStatus.Ready, actual.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(actual.Assessments).Action);
    }

    /// <summary>Installs an isolated CREATE TABLE event trigger with caller-selected effects and activation.</summary>
    /// <param name="connectionString">The database owned by this test.</param>
    /// <param name="effect">The fixed fixture SQL run after creation.</param>
    /// <param name="enabledMode">The PostgreSQL activation clause.</param>
    private static Task InstallEventTriggerAsync(string connectionString, string effect, string enabledMode)
        => ExecuteSqlAsync(connectionString, "CREATE FUNCTION event_trigger_fixture() RETURNS event_trigger "
            + "LANGUAGE plpgsql AS $$ BEGIN " + effect + " END; $$; "
            + "CREATE EVENT TRIGGER event_fixture ON ddl_command_end WHEN TAG IN ('CREATE TABLE') "
            + "EXECUTE FUNCTION event_trigger_fixture(); ALTER EVENT TRIGGER event_fixture " + enabledMode + ";");

    /// <summary>Appends the structured CHECK whose row proof must survive only certified earlier effects.</summary>
    /// <param name="builder">The ordered fixture operation stream.</param>
    private static void AppendEventTriggerCheck(MigrationBuilder builder)
        => builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_event_values",
            "event_created_values", SqlColumnAndInt("id", SafeMigrationSqlBinaryOperator.GreaterThan, 0)),
            SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Appends the same table contract through either supported authoring path.</summary>
    /// <param name="builder">The ordered fixture operation stream.</param>
    /// <param name="typedCreate">Whether EF owns the unwrapped creation operation.</param>
    private static void AppendEventTriggerCreate(MigrationBuilder builder, bool typedCreate)
    {
        if (typedCreate)
        {
            builder.CreateTable("event_created_values", table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
            });
        }
        else
        {
            builder.CreateTableIfNotExists("event_created_values", table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
            });
        }
    }
}
