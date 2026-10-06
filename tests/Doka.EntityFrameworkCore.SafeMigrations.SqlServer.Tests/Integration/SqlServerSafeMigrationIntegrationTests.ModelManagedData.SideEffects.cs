namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Requires runtime validation when explicit SQL triggers invalidate the immutable live row snapshot.
    /// </summary>
    /// <param name="writeKind">The explicit write whose trigger changes the initially matching row.</param>
    /// <remarks>
    /// WHY: A trigger on another table can change a row that matched the immutable preflight snapshot.
    /// </remarks>
    [SqlServerLiveTheory]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task SideEffects_ExplicitSqlTriggerInvalidatesLiveMatchingRow(string writeKind)
    {
        // Arrange
        var connectionString = await CreateSideEffectTriggerFixtureAsync(writeKind);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        // WHY: Managed SQL Server writes reject enabled target triggers. Explicit
        // SQL invokes the trigger while the following safe write retains its guard.
        builder.Sql(SideEffectWriteSql(writeKind));
        AppendSideEffectEnsure(builder, "side_effect_rows");

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var baseline = await runner.AnalyzeAsync(
            context,
            [builder.Operations[1]],
            new SafeMigrationRunOptions("sqlserver-sql-trigger-baseline-" + writeKind));

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-sql-trigger-" + writeKind));

        await ExecuteOperationsAsync(context, [builder.Operations[0]]);

        var actual = await runner.AnalyzeAsync(
            context,
            [builder.Operations[1]],
            new SafeMigrationRunOptions("sqlserver-sql-trigger-actual-" + writeKind));

        var exception = await Record.ExceptionAsync(
            () => ExecuteOperationsAsync(context, [builder.Operations[1]]));

        var changedRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.side_effect_rows WHERE id = 1 AND managed_value = 'trigger-change';");

        var origin = preflight.Assessments[1].DeferredOrigin;

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(baseline.Assessments).ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(baseline.Assessments).Action);
        Assert.False(preflight.Assessments[0].IsSafeOperation);
        Assert.Null(preflight.Assessments[0].Action);
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);
        Assert.Equal("runtime_validation_required", preflight.Assessments[1].Code);
        Assert.Equal("projected_structure_state_unknown", preflight.Assessments[1].AnalysisCode);
        Assert.Null(preflight.Assessments[1].ObservedState);
        Assert.Null(preflight.Assessments[1].PostconditionSatisfied);
        Assert.NotNull(origin);
        Assert.Equal(0, origin.OperationOrdinal);
        Assert.Equal(typeof(SqlOperation).FullName, origin.OperationType);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(actual.Assessments).ObservedState);
        Assert.Equal(51001, Assert.IsType<SqlException>(exception).Number);
        Assert.Equal(1, changedRows);
    }

    /// <summary>Rejects managed writes to enabled-trigger tables before either the write or trigger runs.</summary>
    /// <param name="writeKind">The managed write blocked by the target trigger.</param>
    [SqlServerLiveTheory]
    [InlineData("INSERT")]
    [InlineData("UPDATE")]
    [InlineData("DELETE")]
    public async Task SideEffects_ManagedWriteWithTriggerRemainsUnsupported(string writeKind)
    {
        // Arrange
        var connectionString = await CreateSideEffectTriggerFixtureAsync(writeKind);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectWrite(builder, writeKind);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-trigger-rejected-" + writeKind));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var unchangedRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.side_effect_rows WHERE id = 1 AND managed_value = 'expected';");

        var sourceRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.side_effect_writes WHERE id = 1 AND managed_value = 'source';");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(preflight.Assessments).ObservedState);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(preflight.Assessments).Action);
        Assert.Equal(51002, Assert.IsType<SqlException>(exception).Number);
        Assert.Equal(1, unchangedRows);
        Assert.Equal(writeKind == "INSERT" ? 0 : 1, sourceRows);
    }

    /// <summary>
    /// Treats the executed managed insert as an exact match in a separate replay analysis.
    /// </summary>
    [SqlServerLiveFact]
    public async Task SideEffects_ExecutedManagedInsertReplayProvesSameRow()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.side_effect_rows (id int NOT NULL PRIMARY KEY, "
            + "managed_value nvarchar(64) NOT NULL);");

        await using var context = CreateContext(connectionString);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectEnsure(builder, "side_effect_rows");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-immediate-postcondition"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-executed-replay"));

        var rowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.side_effect_rows WHERE id = 1 AND managed_value = 'expected';");

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
    [SqlServerLiveFact]
    public async Task SideEffects_NewlyCreatedPlainTableRemainsReady()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();

        await using var context = CreateContext(connectionString);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        builder.CreateTableIfNotExists(
            "side_effect_rows",
            table => new
            {
                id = table.Column<int>(type: "int", nullable: false),
                managed_value = table.Column<string>(type: "nvarchar(64)", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_side_effect_rows", row => row.id));

        AppendSideEffectEnsure(builder, "side_effect_rows");

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-fresh-table"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var rowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.side_effect_rows WHERE id = 1 AND managed_value = 'expected';");

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
    [SqlServerLiveFact]
    public async Task SideEffects_TwoChildDeletesThenParentRemainExecutableWithRuntimeValidation()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.side_effect_parents (id int NOT NULL PRIMARY KEY); "
            + "CREATE TABLE dbo.side_effect_children (id int NOT NULL PRIMARY KEY, "
            + "parent_id int NOT NULL, CONSTRAINT fk_side_effect_parent FOREIGN KEY (parent_id) "
            + "REFERENCES dbo.side_effect_parents (id) ON DELETE CASCADE); "
            + "INSERT INTO dbo.side_effect_parents (id) VALUES (1); "
            + "INSERT INTO dbo.side_effect_children (id, parent_id) VALUES (11, 1), (12, 1);");

        await using var context = CreateContext(connectionString);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectChildDelete(builder, 11);
        AppendSideEffectChildDelete(builder, 12);
        AppendSideEffectParentDelete(builder);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-two-child-deletes"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var parents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.side_effect_parents;");

        var children = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.side_effect_children;");

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
    [SqlServerLiveFact]
    public async Task SideEffects_InterveningTriggerRecreationRejectsParentDelete()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.side_effect_parents (id int NOT NULL PRIMARY KEY); "
            + "CREATE TABLE dbo.side_effect_children (id int NOT NULL PRIMARY KEY, "
            + "parent_id int NOT NULL, CONSTRAINT fk_side_effect_parent FOREIGN KEY (parent_id) "
            + "REFERENCES dbo.side_effect_parents (id) ON DELETE CASCADE); "
            + "CREATE TABLE dbo.side_effect_writes (id int NOT NULL PRIMARY KEY, "
            + "managed_value nvarchar(64) NOT NULL); "
            + "INSERT INTO dbo.side_effect_parents (id) VALUES (1); "
            + "INSERT INTO dbo.side_effect_children (id, parent_id) VALUES (11, 1);");

        const string writeKind = "INSERT";

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TRIGGER dbo.side_effect_trigger ON dbo.side_effect_writes AFTER " + writeKind
            + " AS BEGIN SET NOCOUNT ON; INSERT INTO dbo.side_effect_children (id, parent_id) VALUES (99, 1); END;");

        await using var context = CreateContext(connectionString);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectChildDelete(builder, 11);

        // WHY: A managed insert into the triggered table is unsupported. The
        // explicit SQL recreates the child and invalidates earlier delete proofs.
        builder.Sql(SideEffectWriteSql(writeKind));
        AppendSideEffectParentDelete(builder);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-recreated-child"));

        await ExecuteOperationsAsync(context, [builder.Operations[0], builder.Operations[1]]);

        var actual = await runner.AnalyzeAsync(
            context,
            [builder.Operations[2]],
            new SafeMigrationRunOptions("sqlserver-managed-recreated-child-actual"));

        var exception = await Record.ExceptionAsync(
            () => ExecuteOperationsAsync(context, [builder.Operations[2]]));

        var parents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.side_effect_parents;");

        var recreatedChildren = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.side_effect_children WHERE id = 99 AND parent_id = 1;");

        var origin = preflight.Assessments[2].DeferredOrigin;

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[2].Action);
        Assert.Equal("runtime_validation_required", preflight.Assessments[2].Code);
        Assert.Equal("projected_structure_state_unknown", preflight.Assessments[2].AnalysisCode);
        Assert.Null(preflight.Assessments[2].ObservedState);
        Assert.Null(preflight.Assessments[2].PostconditionSatisfied);
        Assert.NotNull(origin);
        Assert.Equal(1, origin.OperationOrdinal);
        Assert.Equal(typeof(SqlOperation).FullName, origin.OperationType);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(actual.Assessments).ObservedState);
        Assert.Equal(51003, Assert.IsType<SqlException>(exception).Number);
        Assert.Equal(1, parents);
        Assert.Equal(1, recreatedChildren);
    }

    /// <summary>Creates an enabled cross-table trigger with a deterministic source row for each DML kind.</summary>
    /// <param name="writeKind">The DML event that changes the separately guarded row.</param>
    /// <returns>The isolated fixture's connection string.</returns>
    private async Task<string> CreateSideEffectTriggerFixtureAsync(string writeKind)
    {
        var connectionString = await CreateDatabaseAsync();

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.side_effect_rows (id int NOT NULL PRIMARY KEY, "
            + "managed_value nvarchar(64) NOT NULL); "
            + "CREATE TABLE dbo.side_effect_writes (id int NOT NULL PRIMARY KEY, "
            + "managed_value nvarchar(64) NOT NULL); "
            + "INSERT INTO dbo.side_effect_rows (id, managed_value) VALUES (1, 'expected');"
            + (writeKind == "INSERT"
                ? string.Empty
                : "INSERT INTO dbo.side_effect_writes (id, managed_value) VALUES (1, 'source');"));

        // WHY: SQL Server requires CREATE TRIGGER to start its own batch.
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TRIGGER dbo.side_effect_trigger ON dbo.side_effect_writes AFTER " + writeKind
            + " AS BEGIN SET NOCOUNT ON; "
            + "UPDATE dbo.side_effect_rows SET managed_value = 'trigger-change' WHERE id = 1; END;");

        return connectionString;
    }

    /// <summary>Invokes the fixture's DML trigger explicitly, outside the managed-write contract.</summary>
    /// <param name="writeKind">The event emitted by the isolated trigger fixture.</param>
    /// <returns>The exact write that changes or removes the deterministic fixture row.</returns>
    private static string SideEffectWriteSql(string writeKind) => writeKind switch
    {
        "INSERT" => "INSERT INTO dbo.side_effect_writes (id, managed_value) VALUES (1, 'target');",
        "UPDATE" => "UPDATE dbo.side_effect_writes SET managed_value = 'target' WHERE id = 1;",
        "DELETE" => "DELETE FROM dbo.side_effect_writes WHERE id = 1;",
        _ => throw new ArgumentOutOfRangeException(nameof(writeKind)),
    };

    private static void AppendSideEffectEnsure(MigrationBuilder builder, string table)
        => builder.EnsureModelManagedDataFromModel(
            table,
            ["id"],
            ["int"],
            ["id", "managed_value"],
            ["int", "nvarchar(64)"],
            new object?[,] { { 1, "expected" } });

    private static void AppendSideEffectWrite(MigrationBuilder builder, string writeKind)
    {
        switch (writeKind)
        {
            case "INSERT":
                builder.EnsureModelManagedDataFromModel(
                    "side_effect_writes",
                    ["id"],
                    ["int"],
                    ["id", "managed_value"],
                    ["int", "nvarchar(64)"],
                    new object?[,] { { 1, "target" } });

                break;
            case "UPDATE":
                builder.UpdateModelManagedDataFromModel(
                    "side_effect_writes",
                    ["id"],
                    ["int"],
                    new object?[,] { { 1 } },
                    ["managed_value"],
                    ["nvarchar(64)"],
                    new object?[,] { { "source" } },
                    new object?[,] { { "target" } });

                break;
            case "DELETE":
                builder.DeleteModelManagedDataFromModel(
                    "side_effect_writes",
                    ["id"],
                    ["int"],
                    new object?[,] { { 1 } },
                    ["id", "managed_value"],
                    ["int", "nvarchar(64)"],
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
            ["int"],
            new object?[,] { { id } },
            ["id", "parent_id"],
            ["int", "int"],
            new object?[,] { { id, 1 } });

    private static void AppendSideEffectParentDelete(MigrationBuilder builder)
        => builder.DeleteModelManagedDataFromModel(
            "side_effect_parents",
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            foreignKeys:
            [
                new ExpectedModelManagedDataForeignKeyDefinition(
                    "side_effect_children",
                    ["parent_id"],
                    ["id"]),
            ]);
}
