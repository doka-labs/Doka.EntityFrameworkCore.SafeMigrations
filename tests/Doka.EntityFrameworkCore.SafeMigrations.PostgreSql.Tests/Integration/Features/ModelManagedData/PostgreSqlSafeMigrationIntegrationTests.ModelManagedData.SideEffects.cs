namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>
    /// Requires runtime validation when a managed write can invalidate the immutable live row snapshot.
    /// </summary>
    /// <param name="writeKind">The managed write whose trigger changes the initially matching row.</param>
    /// <remarks>
    /// WHY: A trigger on another table can change a row that matched the immutable preflight snapshot.
    /// </remarks>
    [Theory]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task SideEffects_ManagedWriteTriggerInvalidatesLiveMatchingRow(string writeKind)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE side_effect_rows (id integer NOT NULL PRIMARY KEY, "
            + "managed_value character varying(64) NOT NULL); "
            + "CREATE TABLE side_effect_writes (id integer NOT NULL PRIMARY KEY, "
            + "managed_value character varying(64) NOT NULL); "
            + "INSERT INTO side_effect_rows (id, managed_value) VALUES (1, 'expected');"
            + (writeKind == "INSERT"
                ? string.Empty
                : "INSERT INTO side_effect_writes (id, managed_value) VALUES (1, 'source');"));

        await ExecuteSqlAsync(
            connectionString,
            "CREATE FUNCTION side_effect_trigger_function() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN UPDATE side_effect_rows SET managed_value = 'trigger-change' WHERE id = 1; RETURN NULL; END; $$; "
            + "CREATE TRIGGER side_effect_trigger AFTER " + writeKind
            + " ON side_effect_writes FOR EACH ROW EXECUTE FUNCTION side_effect_trigger_function();");

        await using var context = CreateContext(connectionString);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectWrite(builder, writeKind);
        AppendSideEffectEnsure(builder, "side_effect_rows");

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("postgresql-managed-trigger-" + writeKind));

        await ExecuteOperationsAsync(context, [builder.Operations[0]]);

        var actual = await runner.AnalyzeAsync(
            context,
            [builder.Operations[1]],
            new SafeMigrationRunOptions("postgresql-managed-trigger-actual-" + writeKind));

        var exception = await Record.ExceptionAsync(
            () => ExecuteOperationsAsync(context, [builder.Operations[1]]));

        var changedRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM side_effect_rows WHERE id = 1 AND managed_value = 'trigger-change';");

        var origin = preflight.Assessments[1].DeferredOrigin;

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.Equal("runtime_validation_required", preflight.Assessments[1].Code);
        Assert.Equal("projected_model_managed_data_state_unknown", preflight.Assessments[1].AnalysisCode);
        Assert.NotNull(origin);
        Assert.Equal(0, origin.OperationOrdinal);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(actual.Assessments).ObservedState);
        Assert.IsType<PostgresException>(exception);
        Assert.Equal(1, changedRows);
    }

    /// <summary>
    /// Treats the executed managed insert as an exact match in a separate replay analysis.
    /// </summary>
    [Fact]
    public async Task SideEffects_ExecutedManagedInsertReplayProvesSameRow()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE side_effect_rows (id integer NOT NULL PRIMARY KEY, "
            + "managed_value character varying(64) NOT NULL);");

        await using var context = CreateContext(connectionString);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectEnsure(builder, "side_effect_rows");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("postgresql-managed-immediate-postcondition"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("postgresql-managed-executed-replay"));

        var rowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM side_effect_rows WHERE id = 1 AND managed_value = 'expected';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay.Assessments).Action);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(replay.Assessments).ObservedState);
        Assert.Equal(1, rowCount);
    }

    /// <summary>
    /// Keeps a plain managed insert into a newly created table statically ready.
    /// </summary>
    [Fact]
    public async Task SideEffects_NewlyCreatedPlainTableRemainsReady()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await using var context = CreateContext(connectionString);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        builder.CreateTableIfNotExists(
            "side_effect_rows",
            table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                managed_value = table.Column<string>(type: "character varying(64)", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_side_effect_rows", row => row.id));

        AppendSideEffectEnsure(builder, "side_effect_rows");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("postgresql-managed-fresh-table"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var rowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM side_effect_rows WHERE id = 1 AND managed_value = 'expected';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.All(preflight.Assessments, assessment => Assert.Null(assessment.DeferredOrigin));
        Assert.Equal(1, rowCount);
    }

    /// <summary>
    /// Executes two child deletes and the parent delete through fresh runtime dependency checks.
    /// </summary>
    [Fact]
    public async Task SideEffects_TwoChildDeletesThenParentRemainExecutableWithRuntimeValidation()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE side_effect_parents (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE side_effect_children (id integer NOT NULL PRIMARY KEY, "
            + "parent_id integer NOT NULL, CONSTRAINT fk_side_effect_parent FOREIGN KEY (parent_id) "
            + "REFERENCES side_effect_parents (id) ON DELETE CASCADE); "
            + "INSERT INTO side_effect_parents (id) VALUES (1); "
            + "INSERT INTO side_effect_children (id, parent_id) VALUES (11, 1), (12, 1);");

        await using var context = CreateContext(connectionString);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectChildDelete(builder, 11);
        AppendSideEffectChildDelete(builder, 12);
        AppendSideEffectParentDelete(builder);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("postgresql-managed-two-child-deletes"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var parents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM side_effect_parents;");

        var children = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM side_effect_children;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[2].Action);
        Assert.NotNull(preflight.Assessments[1].DeferredOrigin);
        Assert.NotNull(preflight.Assessments[2].DeferredOrigin);
        Assert.Equal(0, parents);
        Assert.Equal(0, children);
    }

    /// <summary>
    /// Rejects a parent delete when an intervening trigger recreates a dependent row.
    /// </summary>
    /// <remarks>
    /// WHY: A stale child-delete proof must not authorize cascading deletion of the recreated row.
    /// </remarks>
    [Fact]
    public async Task SideEffects_InterveningTriggerRecreationRejectsParentDelete()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE side_effect_parents (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE side_effect_children (id integer NOT NULL PRIMARY KEY, "
            + "parent_id integer NOT NULL, CONSTRAINT fk_side_effect_parent FOREIGN KEY (parent_id) "
            + "REFERENCES side_effect_parents (id) ON DELETE CASCADE); "
            + "CREATE TABLE side_effect_writes (id integer NOT NULL PRIMARY KEY, "
            + "managed_value character varying(64) NOT NULL); "
            + "INSERT INTO side_effect_parents (id) VALUES (1); "
            + "INSERT INTO side_effect_children (id, parent_id) VALUES (11, 1);");

        const string writeKind = "INSERT";

        await ExecuteSqlAsync(
            connectionString,
            "CREATE FUNCTION side_effect_trigger_function() RETURNS trigger LANGUAGE plpgsql AS $$ "
            + "BEGIN INSERT INTO side_effect_children (id, parent_id) VALUES (99, 1); RETURN NULL; END; $$; "
            + "CREATE TRIGGER side_effect_trigger AFTER " + writeKind
            + " ON side_effect_writes FOR EACH ROW EXECUTE FUNCTION side_effect_trigger_function();");

        await using var context = CreateContext(connectionString);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectChildDelete(builder, 11);
        AppendSideEffectWrite(builder, writeKind);
        AppendSideEffectParentDelete(builder);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("postgresql-managed-recreated-child"));

        await ExecuteOperationsAsync(context, [builder.Operations[0], builder.Operations[1]]);

        var actual = await runner.AnalyzeAsync(
            context,
            [builder.Operations[2]],
            new SafeMigrationRunOptions("postgresql-managed-recreated-child-actual"));

        var exception = await Record.ExceptionAsync(
            () => ExecuteOperationsAsync(context, [builder.Operations[2]]));

        var parents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM side_effect_parents;");

        var recreatedChildren = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM side_effect_children WHERE id = 99 AND parent_id = 1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[2].Action);
        Assert.Equal("runtime_validation_required", preflight.Assessments[2].Code);
        // WHY: User-trigger presence also invalidates structure after the first DELETE.
        // The intervening deferred managed write has no certified provider postcondition.
        Assert.Equal("projected_provider_postcondition_unknown", preflight.Assessments[2].AnalysisCode);
        Assert.NotNull(preflight.Assessments[2].DeferredOrigin);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(actual.Assessments).ObservedState);
        Assert.IsType<PostgresException>(exception);
        Assert.Equal(1, parents);
        Assert.Equal(1, recreatedChildren);
    }

    private static void AppendSideEffectEnsure(MigrationBuilder builder, string table)
        => builder.EnsureModelManagedDataFromModel(
            table,
            ["id"],
            ["integer"],
            ["id", "managed_value"],
            ["integer", "character varying(64)"],
            new object?[,] { { 1, "expected" } });

    private static void AppendSideEffectWrite(MigrationBuilder builder, string writeKind)
    {
        switch (writeKind)
        {
            case "INSERT":
                builder.EnsureModelManagedDataFromModel(
                    "side_effect_writes",
                    ["id"],
                    ["integer"],
                    ["id", "managed_value"],
                    ["integer", "character varying(64)"],
                    new object?[,] { { 1, "target" } });

                break;
            case "UPDATE":
                builder.UpdateModelManagedDataFromModel(
                    "side_effect_writes",
                    ["id"],
                    ["integer"],
                    new object?[,] { { 1 } },
                    ["managed_value"],
                    ["character varying(64)"],
                    new object?[,] { { "source" } },
                    new object?[,] { { "target" } });

                break;
            case "DELETE":
                builder.DeleteModelManagedDataFromModel(
                    "side_effect_writes",
                    ["id"],
                    ["integer"],
                    new object?[,] { { 1 } },
                    ["id", "managed_value"],
                    ["integer", "character varying(64)"],
                    new object?[,] { { 1, "source" } });

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(writeKind));
        }
    }

    private static void AppendSideEffectChildDelete(MigrationBuilder builder, int id)
        => builder.DeleteModelManagedDataFromModel(
            "side_effect_children",
            ["id"],
            ["integer"],
            new object?[,] { { id } },
            ["id", "parent_id"],
            ["integer", "integer"],
            new object?[,] { { id, 1 } });

    private static void AppendSideEffectParentDelete(MigrationBuilder builder)
        => builder.DeleteModelManagedDataFromModel(
            "side_effect_parents",
            ["id"],
            ["integer"],
            new object?[,] { { 1 } },
            ["id"],
            ["integer"],
            new object?[,] { { 1 } },
            foreignKeys:
            [
                new ExpectedModelManagedDataForeignKeyDefinition(
                    "side_effect_children",
                    ["parent_id"],
                    ["id"]),
            ]);
}
