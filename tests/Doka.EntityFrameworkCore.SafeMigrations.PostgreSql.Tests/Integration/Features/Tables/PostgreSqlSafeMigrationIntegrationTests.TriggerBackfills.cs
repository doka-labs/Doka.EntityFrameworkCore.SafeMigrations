namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Column backfill SQL can invoke a user trigger and invalidate an unrelated captured index.</summary>
    /// <param name="authoring">Whether EF ALTER, safe ALTER or safe ensure-repair owns the baseline.</param>
    /// <param name="sqlDefault">Whether the default is SQL rather than a CLR literal.</param>
    [Theory]
    [InlineData("typed", false)]
    [InlineData("typed", true)]
    [InlineData("alter", false)]
    [InlineData("alter", true)]
    [InlineData("ensure", false)]
    [InlineData("ensure", true)]
    public async Task EventTriggers_ColumnBackfillDefersStructuralMatch(string authoring, bool sqlDefault)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE event_backfill_source(value integer " + (authoring == "ensure" ? "NOT NULL" : "NULL")
            + "); INSERT INTO event_backfill_source VALUES (" + (authoring == "typed" ? "NULL" : "3") + "); "
            + "CREATE FUNCTION event_backfill_trigger_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN DROP INDEX ix_event_existing; CREATE INDEX ix_event_existing ON event_existing_values(value); "
            + "RETURN NULL; END; $$; CREATE TRIGGER event_backfill_fixture AFTER UPDATE ON event_backfill_source "
            + "FOR EACH STATEMENT EXECUTE FUNCTION event_backfill_trigger_fixture();");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerColumnAlter(builder, authoring, sqlDefault, hasDefault: true);
        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-backfill-trigger-structure"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [builder.Operations[1]]));
        var values = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM event_backfill_source WHERE value=" + (authoring == "typed" ? "0" : "3") + ";");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[1].Action);
        Assert.Equal("projected_dml_trigger_structure_unknown", report.Assessments[1].AnalysisCode);
        var origin = Assert.IsType<SafeMigrationDeferredOrigin>(report.Assessments[1].DeferredOrigin);

        Assert.Equal(0, origin.OperationOrdinal);
        Assert.Equal("P1001", Assert.IsType<PostgresException>(exception).SqlState);
        Assert.Equal(1, values);
        if (authoring != "typed")
        {
            Assert.Equal(SafeMigrationAction.Repair, report.Assessments[0].Action);
        }
    }

    /// <summary>
    /// A column alteration without a rendered default emits no UPDATE and preserves unrelated metadata.
    /// </summary>
    /// <param name="authoring">The column authoring form.</param>
    [Theory]
    [InlineData("typed")]
    [InlineData("alter")]
    [InlineData("ensure")]
    public async Task EventTriggers_ColumnAlterWithoutBackfillRetainsStructuralMatch(string authoring)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE event_backfill_source(value integer " + (authoring == "ensure" ? "NOT NULL" : "NULL")
            + "); INSERT INTO event_backfill_source VALUES (3); "
            + "CREATE FUNCTION event_backfill_trigger_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN DROP INDEX ix_event_existing; RETURN NULL; END; $$; "
            + "CREATE TRIGGER event_backfill_fixture AFTER UPDATE ON event_backfill_source "
            + "FOR EACH STATEMENT EXECUTE FUNCTION event_backfill_trigger_fixture();");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        AppendEventTriggerColumnAlter(builder, authoring, sqlDefault: false, hasDefault: false);
        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-no-backfill-trigger-structure"));
        await ExecuteOperationsAsync(context, builder.Operations);
        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_indexes WHERE indexname='ix_event_existing';");

        // Assert
        Assert.Equal(authoring == "typed" ? SafeMigrationReportStatus.ReadyWithProviderOperations
            : SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Null(report.Assessments[1].DeferredOrigin);
        Assert.Equal(1, indexes);
    }

    /// <summary>Computed replacement returns before Npgsql's UPDATE branch and cannot invoke a DML trigger.</summary>
    [Fact]
    public async Task EventTriggers_ComputedReplacementWithoutBackfillRetainsStructuralMatch()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE event_existing_values(id integer NOT NULL, value integer); "
            + "CREATE INDEX ix_event_existing ON event_existing_values(id); "
            + "CREATE TABLE event_backfill_source(id integer NOT NULL, "
            + "value integer GENERATED ALWAYS AS (id+1) STORED); INSERT INTO event_backfill_source(id) VALUES (1); "
            + "CREATE FUNCTION event_backfill_trigger_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN DROP INDEX ix_event_existing; RETURN NULL; END; $$; "
            + "CREATE TRIGGER event_backfill_fixture AFTER UPDATE ON event_backfill_source "
            + "FOR EACH STATEMENT EXECUTE FUNCTION event_backfill_trigger_fixture();");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(new AlterColumnOperation
        {
            Table = "event_backfill_source",
            Name = "value",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
            DefaultValue = 0,
            OldColumn = new AddColumnOperation
            {
                ClrType = typeof(int),
                ColumnType = "integer",
                IsNullable = true,
                ComputedColumnSql = "id+1",
                IsStored = true,
            },
        });

        builder.CreateIndexIfNotExists("ix_event_existing", "event_existing_values", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("postgresql-computed-replacement-without-backfill"));
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([builder.Operations[0]], context.Model);
        await ExecuteOperationsAsync(context, builder.Operations);
        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_indexes WHERE indexname='ix_event_existing';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        Assert.Null(report.Assessments[1].DeferredOrigin);
        Assert.All(commands, static command => Assert.DoesNotContain("UPDATE ", command.CommandText,
            StringComparison.Ordinal));
        Assert.Equal(1, indexes);
    }

    /// <summary>Builds the pinned Npgsql baseline forms which may emit UPDATE before changing nullability.</summary>
    /// <param name="builder">The ordered fixture stream.</param>
    /// <param name="authoring">The ordinary or safe authoring form.</param>
    /// <param name="sqlDefault">Whether to render a SQL default rather than a literal.</param>
    /// <param name="hasDefault">Whether the alteration has a backfill default at all.</param>
    private static void AppendEventTriggerColumnAlter(
        MigrationBuilder builder,
        string authoring,
        bool sqlDefault,
        bool hasDefault
    )
    {
        if (authoring == "typed")
        {
            builder.Operations.Add(new AlterColumnOperation
            {
                Table = "event_backfill_source",
                Name = "value",
                ClrType = typeof(int),
                ColumnType = "integer",
                IsNullable = false,
                DefaultValue = hasDefault && !sqlDefault ? 0 : null,
                DefaultValueSql = hasDefault && sqlDefault ? "0" : null,
                OldColumn = new AddColumnOperation
                {
                    ClrType = typeof(int),
                    ColumnType = "integer",
                    IsNullable = true,
                },
            });

            return;
        }

        var target = new ExpectedColumnDefinition("value", typeof(int), isNullable: false, "integer",
            comment: "approved", defaultValue: !hasDefault ? SafeMigrationDefaultValue.None
                : sqlDefault ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(0))
                    : SafeMigrationDefaultValue.Literal(0));

        if (authoring == "ensure")
        {
            builder.EnsureColumn("event_backfill_source", target, SafeMigrationPolicy.RepairIfSafe);
        }
        else if (authoring == "alter")
        {
            var old = new ExpectedColumnDefinition("value", typeof(int), isNullable: true, "integer");
            builder.AlterColumnIfDifferent("event_backfill_source", target, old, SafeMigrationPolicy.RepairIfSafe);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(authoring));
        }
    }
}
