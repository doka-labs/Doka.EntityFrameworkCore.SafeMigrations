namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>DML trigger functions can execute DDL and invalidate an unrelated captured index match.</summary>
    /// <param name="typedWrite">Whether the source write is an ordinary EF operation.</param>
    /// <param name="writeKind">The DML operation which invokes the fixture trigger.</param>
    [Theory]
    [InlineData(false, "INSERT")]
    [InlineData(true, "INSERT")]
    [InlineData(false, "UPDATE")]
    [InlineData(true, "UPDATE")]
    [InlineData(false, "DELETE")]
    [InlineData(true, "DELETE")]
    public async Task EventTriggers_DmlTriggerDdlDefersStructuralMatch(bool typedWrite, string writeKind)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE side_effect_writes(id integer NOT NULL PRIMARY KEY, "
            + "managed_value character varying(64) NOT NULL); "
            + (writeKind == "INSERT" ? string.Empty : "INSERT INTO side_effect_writes VALUES (1,'source'); ")
            + "CREATE FUNCTION event_dml_trigger_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN DROP INDEX ix_event_existing; CREATE INDEX ix_event_existing ON event_existing_values(value); "
            + "RETURN NULL; END; $$; CREATE TRIGGER event_dml_fixture AFTER " + writeKind + " ON side_effect_writes "
            + "FOR EACH ROW EXECUTE FUNCTION event_dml_trigger_fixture();");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (typedWrite)
        {
            AppendEventTriggerTypedWrite(builder, writeKind);
        }
        else
        {
            AppendSideEffectWrite(builder, writeKind);
        }

        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-dml-trigger-structure"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [builder.Operations[1]]));
        var changed = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM pg_catalog.pg_indexes "
            + "WHERE indexname='ix_event_existing' AND indexdef LIKE '%(value)%';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[1].Action);
        Assert.Equal("projected_dml_trigger_structure_unknown", report.Assessments[1].AnalysisCode);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[1].DeferredOrigin);

        Assert.Equal(0, origin.OperationOrdinal);
        Assert.Equal("P1001", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(1, changed);
    }

    /// <summary>A parent DELETE can invoke DDL in a dependent user trigger through an internal FK cascade.</summary>
    [Fact]
    public async Task EventTriggers_CascadeDmlDefersUnrelatedStructuralMatch()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE event_dml_parents(id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE event_dml_children(id integer NOT NULL PRIMARY KEY, parent_id integer NOT NULL "
            + "REFERENCES event_dml_parents(id) ON DELETE CASCADE); "
            + "INSERT INTO event_dml_parents VALUES (1); INSERT INTO event_dml_children VALUES (2,1); "
            + "CREATE FUNCTION event_dml_trigger_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN DROP INDEX ix_event_existing; CREATE INDEX ix_event_existing ON event_existing_values(value); "
            + "RETURN NULL; END; $$; CREATE TRIGGER event_dml_fixture AFTER DELETE ON event_dml_children "
            + "FOR EACH ROW EXECUTE FUNCTION event_dml_trigger_fixture();");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(new DeleteDataOperation
        {
            Table = "event_dml_parents",
            KeyColumns = ["id"],
            KeyColumnTypes = ["integer"],
            KeyValues = new object[,] { { 1 } },
        });

        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-cascade-dml-trigger-structure"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [builder.Operations[1]]));
        var remaining = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM event_dml_children;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[1].Action);
        Assert.Equal("projected_dml_trigger_structure_unknown", report.Assessments[1].AnalysisCode);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[1].DeferredOrigin);

        Assert.Equal(0, origin.OperationOrdinal);
        Assert.Equal("P1001", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(0, remaining);
    }

    /// <summary>
    /// Inactive user triggers and internal FK triggers alone preserve captured nonunique index metadata.
    /// </summary>
    /// <param name="activation">The optional user trigger's origin-session activation clause.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("DISABLE")]
    [InlineData("ENABLE REPLICA")]
    public async Task EventTriggers_InactiveDmlRetainsStructuralMatch(string? activation)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL PRIMARY KEY, value integer); "
            + "INSERT INTO event_existing_values VALUES (1,0); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE side_effect_writes(id integer NOT NULL PRIMARY KEY "
            + "REFERENCES event_existing_values(id), managed_value character varying(64) NOT NULL);");
        if (activation is not null)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE FUNCTION event_dml_trigger_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ "
                + "BEGIN DROP INDEX ix_event_existing; RETURN NULL; END; $$; "
                + "CREATE TRIGGER event_dml_fixture AFTER INSERT ON side_effect_writes "
                + "FOR EACH ROW EXECUTE FUNCTION event_dml_trigger_fixture(); "
                + "ALTER TABLE side_effect_writes " + activation + " TRIGGER event_dml_fixture;");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerTypedWrite(builder, "INSERT");
        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-inactive-dml-trigger-structure"));
        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM side_effect_writes;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Null(report.Assessments[1].DeferredOrigin);
        Assert.Equal(1, rows);
    }

    /// <summary>A matching managed row emits no DML and therefore does not invoke an active user trigger.</summary>
    [Fact]
    public async Task EventTriggers_MatchingManagedWriteRetainsStructuralMatch()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE side_effect_writes(id integer NOT NULL PRIMARY KEY, "
            + "managed_value character varying(64) NOT NULL); INSERT INTO side_effect_writes VALUES (1,'target'); "
            + "CREATE FUNCTION event_dml_trigger_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN DROP INDEX ix_event_existing; RETURN NULL; END; $$; "
            + "CREATE TRIGGER event_dml_fixture AFTER INSERT ON side_effect_writes "
            + "FOR EACH ROW EXECUTE FUNCTION event_dml_trigger_fixture();");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendSideEffectWrite(builder, "INSERT");
        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-noop-dml-trigger-structure"));
        await ExecuteOperationsAsync(context, builder.Operations);
        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_indexes WHERE indexname='ix_event_existing';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.All(report.Assessments, static assessment => Assert.Null(assessment.DeferredOrigin));
        Assert.Equal(1, indexes);
    }

    /// <summary>User DML triggers follow session replication activation independently of DDL event triggers.</summary>
    /// <param name="activation">The user trigger activation clause.</param>
    /// <param name="replicationRole">The session role for analysis and execution.</param>
    /// <param name="fires">Whether PostgreSQL invokes the trigger for the source INSERT.</param>
    [Theory]
    [InlineData("ENABLE", "local", true)]
    [InlineData("ENABLE", "replica", false)]
    [InlineData("ENABLE REPLICA", "replica", true)]
    [InlineData("ENABLE ALWAYS", "replica", true)]
    [InlineData("ENABLE ALWAYS", "origin", true)]
    public async Task EventTriggers_DmlActivationFollowsSessionRole(
        string activation,
        string replicationRole,
        bool fires
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE side_effect_writes(id integer NOT NULL PRIMARY KEY, "
            + "managed_value character varying(64) NOT NULL); "
            + "CREATE FUNCTION event_dml_trigger_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN DROP INDEX ix_event_existing; CREATE INDEX ix_event_existing ON event_existing_values(value); "
            + "RETURN NULL; END; $$; CREATE TRIGGER event_dml_fixture AFTER INSERT ON side_effect_writes "
            + "FOR EACH ROW EXECUTE FUNCTION event_dml_trigger_fixture(); ALTER TABLE side_effect_writes "
            + activation + " TRIGGER event_dml_fixture;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlAsync(
            $"SELECT pg_catalog.set_config('session_replication_role', {replicationRole}, false);");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerTypedWrite(builder, "INSERT");
        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-dml-trigger-session-role"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [builder.Operations[1]]));

        // Assert
        Assert.Equal(fires ? SafeMigrationReportStatus.RuntimeValidationRequired
            : SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Equal(fires ? SafeMigrationAction.ValidateAtRuntime : SafeMigrationAction.NoOp,
            report.Assessments[1].Action);
        if (fires)
        {
            Assert.Equal("P1001", Assert.IsType<PostgresException>(exception).SqlState);
        }
        else
        {
            Assert.Null(exception);
            Assert.Null(report.Assessments[1].DeferredOrigin);
        }
    }

    /// <summary>Revoked user-trigger catalog access cannot certify structural safety after typed DML.</summary>
    [Fact]
    public async Task EventTriggers_DmlVisibilityUnknownRequiresRuntimeValidation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var role = "dml_reader_" + Guid.NewGuid().ToString("N");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE side_effect_writes(id integer NOT NULL PRIMARY KEY, "
            + "managed_value character varying(64) NOT NULL); CREATE ROLE " + role
            + " LOGIN PASSWORD 'dml-reader-fixture'; GRANT USAGE ON SCHEMA public TO " + role + "; "
            + "GRANT SELECT, INSERT ON side_effect_writes TO " + role + "; "
            + "GRANT SELECT ON event_existing_values TO " + role + "; "
            + "REVOKE SELECT ON pg_catalog.pg_trigger FROM PUBLIC;");
        var readerConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = role,
            Password = "dml-reader-fixture",
        }.ConnectionString;

        await using var context = CreateContext(readerConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerTypedWrite(builder, "INSERT");
        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-dml-trigger-visibility"));
        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM side_effect_writes;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[1].Action);
        Assert.Equal("projected_dml_trigger_visibility_unknown", report.Assessments[1].AnalysisCode);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[1].DeferredOrigin);

        Assert.Equal(0, origin.OperationOrdinal);
        Assert.Equal(1, rows);
    }

    /// <summary>Zero-row ordinary writes emit no SQL and therefore cannot invoke an active user trigger.</summary>
    /// <param name="writeKind">The source EF data-operation form.</param>
    [Theory]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task EventTriggers_EmptyTypedDmlRetainsStructuralMatch(string writeKind)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE side_effect_writes(id integer NOT NULL PRIMARY KEY, "
            + "managed_value character varying(64) NOT NULL); "
            + "CREATE FUNCTION event_dml_trigger_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN DROP INDEX ix_event_existing; RETURN NULL; END; $$; "
            + "CREATE TRIGGER event_dml_fixture AFTER INSERT OR UPDATE OR DELETE ON side_effect_writes "
            + "FOR EACH STATEMENT EXECUTE FUNCTION event_dml_trigger_fixture();");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerTypedWrite(builder, writeKind, empty: true);
        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-empty-dml-trigger-structure"));
        var sourceCommands = context.GetService<IMigrationsSqlGenerator>()
            .Generate([builder.Operations[0]], context.Model);

        await ExecuteOperationsAsync(context, builder.Operations);
        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_indexes WHERE indexname='ix_event_existing';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Null(report.Assessments[1].DeferredOrigin);
        Assert.Empty(sourceCommands);
        Assert.Equal(1, indexes);
    }

    /// <summary>Disabling a user trigger between analyses clears the scoped analyzer's prior DML origin.</summary>
    [Fact]
    public async Task EventTriggers_DisablingDmlBetweenAnalysesClearsCapturedOrigin()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE side_effect_writes(id integer NOT NULL PRIMARY KEY, "
            + "managed_value character varying(64) NOT NULL); "
            + "CREATE FUNCTION event_dml_trigger_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN DROP INDEX ix_event_existing; RETURN NULL; END; $$; "
            + "CREATE TRIGGER event_dml_fixture AFTER INSERT ON side_effect_writes "
            + "FOR EACH ROW EXECUTE FUNCTION event_dml_trigger_fixture();");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerTypedWrite(builder, "INSERT");
        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var enabled = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-dml-trigger-enabled"));
        await ExecuteSqlAsync(connectionString, "ALTER TABLE side_effect_writes DISABLE TRIGGER event_dml_fixture;");
        var disabled = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-dml-trigger-disabled"));
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, enabled.Status);
        Assert.NotNull(enabled.Assessments[1].DeferredOrigin);
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, disabled.Status);
        Assert.Equal(SafeMigrationAction.NoOp, disabled.Assessments[1].Action);
        Assert.Null(disabled.Assessments[1].DeferredOrigin);
    }

    /// <summary>Creates ordinary typed DML without requiring model metadata for a disposable fixture table.</summary>
    /// <param name="builder">The ordered operation stream.</param>
    /// <param name="writeKind">The source DML form.</param>
    /// <param name="empty">Whether the provider's certified zero-row baseline is requested.</param>
    private static void AppendEventTriggerTypedWrite(MigrationBuilder builder, string writeKind, bool empty = false)
    {
        switch (writeKind)
        {
            case "INSERT":
                builder.Operations.Add(new InsertDataOperation
                {
                    Table = "side_effect_writes",
                    Columns = ["id", "managed_value"],
                    ColumnTypes = ["integer", "character varying(64)"],
                    Values = empty ? new object[0, 2] : new object[,] { { 1, "target" } },
                });

                break;
            case "UPDATE":
                builder.Operations.Add(new UpdateDataOperation
                {
                    Table = "side_effect_writes",
                    KeyColumns = ["id"],
                    KeyColumnTypes = ["integer"],
                    KeyValues = empty ? new object[0, 1] : new object[,] { { 1 } },
                    Columns = ["managed_value"],
                    ColumnTypes = ["character varying(64)"],
                    Values = empty ? new object[0, 1] : new object[,] { { "target" } },
                });

                break;
            case "DELETE":
                builder.Operations.Add(new DeleteDataOperation
                {
                    Table = "side_effect_writes",
                    KeyColumns = ["id"],
                    KeyColumnTypes = ["integer"],
                    KeyValues = empty ? new object[0, 1] : new object[,] { { 1 } },
                });

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(writeKind));
        }
    }

}
