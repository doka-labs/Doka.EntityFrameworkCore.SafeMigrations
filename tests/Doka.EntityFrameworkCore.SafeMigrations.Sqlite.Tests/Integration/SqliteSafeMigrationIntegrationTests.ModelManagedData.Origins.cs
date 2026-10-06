namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>A confined later insert cannot replace the actual global uncertainty origin.</summary>
    [Fact]
    public async Task SideEffects_ConfinedInsertPreservesGlobalDeferredOriginAndPostflight()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();

        await ExecuteSqlAsync(connection,
            "CREATE TABLE side_effect_writes (id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL); "
            + "CREATE TABLE side_effect_rows (id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL); "
            + "INSERT INTO side_effect_rows VALUES (1, 'expected');");

        await using var context = CreateContext(connection);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectWrite(builder, "INSERT");
        builder.CreateTableIfNotExists("confined_rows", table => new
        {
            id = table.Column<int>(type: "INTEGER", nullable: false),
            managed_value = table.Column<string>(type: "TEXT", nullable: false),
        }, constraints: table => table.PrimaryKey("pk_confined_rows", row => row.id));
        AppendSideEffectEnsure(builder, "confined_rows");
        AppendSideEffectEnsure(builder, "side_effect_rows");

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("sqlite-managed-global-origin");

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations, options);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);

        var postflight = await runner.VerifyAsync(context, builder.Operations, options);
        var json = SafeMigrationReportJson.SerializeToUtf8Bytes(report);

        using var document = System.Text.Json.JsonDocument.Parse(json);

        var origin = report.Assessments[3].DeferredOrigin;

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[3].Action);
        Assert.NotNull(origin);
        Assert.Equal(0, origin.OperationOrdinal);
        Assert.Equal(typeof(SafeMigrationOperation).FullName, origin.OperationType);
        Assert.Equal(0, document.RootElement.GetProperty("assessments")[3]
            .GetProperty("deferredOrigin").GetProperty("operationOrdinal").GetInt32());
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>Each deferred managed operation propagates uncertainty without claiming a new row state.</summary>
    [Fact]
    public async Task SideEffects_DeferredManagedWritesAdvanceTheOrigin()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();

        await ExecuteSqlAsync(connection,
            "CREATE TABLE side_effect_writes (id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL); "
            + "CREATE TABLE side_effect_rows (id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL); "
            + "CREATE TABLE other_rows (id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL); "
            + "INSERT INTO side_effect_rows VALUES (1, 'expected'); "
            + "INSERT INTO other_rows VALUES (1, 'expected');");

        await using var context = CreateContext(connection);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectWrite(builder, "INSERT");
        AppendSideEffectEnsure(builder, "side_effect_rows");
        AppendSideEffectEnsure(builder, "other_rows");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-managed-deferred-chain"));

        var firstOrigin = report.Assessments[1].DeferredOrigin;

        var secondOrigin = report.Assessments[2].DeferredOrigin;

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[2].Action);
        Assert.NotNull(firstOrigin);
        Assert.Equal(0, firstOrigin.OperationOrdinal);
        Assert.NotNull(secondOrigin);
        Assert.Equal(1, secondOrigin.OperationOrdinal);
        Assert.Null(report.Assessments[1].ObservedState);
        Assert.Null(report.Assessments[2].PostconditionSatisfied);
    }

    /// <summary>
    /// Defers a parent delete after a confined insert adds a previously absent dependent row.
    /// </summary>
    /// <param name="unrelatedWrite">Whether a later confined insert targets an unrelated fresh table.</param>
    /// <remarks>
    /// WHY: Recreating the child table does not preserve the parent's immutable no-dependent-row proof.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SideEffects_ConfinedInsertIntoRecreatedChildInvalidatesParentDependencyProof(bool unrelatedWrite)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();

        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE side_effect_parents (id INTEGER NOT NULL PRIMARY KEY); "
            + "CREATE TABLE side_effect_children (id INTEGER NOT NULL PRIMARY KEY, "
            + "parent_id INTEGER NOT NULL, CONSTRAINT fk_side_effect_parent FOREIGN KEY (parent_id) "
            + "REFERENCES side_effect_parents (id) ON DELETE CASCADE); "
            + "INSERT INTO side_effect_parents (id) VALUES (1);");

        await using var context = CreateContext(connection);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        builder.DropTableIfExists("side_effect_children");
        builder.CreateTableIfNotExists(
            "side_effect_children",
            table => new
            {
                id = table.Column<int>(type: "INTEGER", nullable: false),
                parent_id = table.Column<int>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_side_effect_children", row => row.id);
                table.ForeignKey(
                    "fk_side_effect_parent",
                    row => row.parent_id,
                    principalTable: "side_effect_parents",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        builder.EnsureModelManagedDataFromModel(
            "side_effect_children",
            ["id"],
            ["INTEGER"],
            ["id", "parent_id"],
            ["INTEGER", "INTEGER"],
            new object?[,] { { 11, 1 } });

        if (unrelatedWrite)
        {
            builder.CreateTableIfNotExists(
                "unrelated_confined_rows",
                table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false),
                    managed_value = table.Column<string>(type: "TEXT", nullable: false),
                },
                constraints: table => table.PrimaryKey("pk_unrelated_confined_rows", row => row.id));

            AppendSideEffectEnsure(builder, "unrelated_confined_rows");
        }

        var parentOrdinal = builder.Operations.Count;

        AppendSideEffectParentDelete(builder);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var initial = await runner.AnalyzeAsync(
            context,
            [builder.Operations[parentOrdinal]],
            new SafeMigrationRunOptions("sqlite-managed-parent-without-children"));

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-managed-recreated-child-insert"));

        await ExecuteOperationsAsync(
            context,
            builder.Operations.Take(parentOrdinal).ToArray());

        var actual = await runner.AnalyzeAsync(
            context,
            [builder.Operations[parentOrdinal]],
            new SafeMigrationRunOptions("sqlite-managed-recreated-child-insert-actual"));

        var exception = await Record.ExceptionAsync(
            () => ExecuteOperationsAsync(context, [builder.Operations[parentOrdinal]]));

        var parents = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM side_effect_parents WHERE id = 1;");

        var children = await ScalarIntAsync(
            connection,
            "SELECT COUNT(*) FROM side_effect_children WHERE id = 11 AND parent_id = 1;");

        var origin = preflight.Assessments[parentOrdinal].DeferredOrigin;

        // Assert
        Assert.Equal(SafeMigrationObservedState.TransitionReady, Assert.Single(initial.Assessments).ObservedState);
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[parentOrdinal].Action);
        Assert.Equal("runtime_validation_required", preflight.Assessments[parentOrdinal].Code);
        Assert.Equal("projected_model_managed_data_state_unknown", preflight.Assessments[parentOrdinal].AnalysisCode);
        Assert.NotNull(origin);
        Assert.Equal(2, origin.OperationOrdinal);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(actual.Assessments).ObservedState);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(1, parents);
        Assert.Equal(1, children);
    }

    /// <summary>
    /// Attributes fresh-table uncertainty to its later rename instead of an earlier unrelated global write.
    /// </summary>
    /// <remarks>
    /// WHY: The newly created table did not exist when the earlier unconfined write invalidated live rows.
    /// </remarks>
    [Fact]
    public async Task SideEffects_FreshTableRenameSupersedesEarlierGlobalDeferredOrigin()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();

        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE side_effect_writes (id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL);");

        await using var context = CreateContext(connection);

        var builder = new MigrationBuilder(context.Database.ProviderName!);

        AppendSideEffectWrite(builder, "INSERT");
        builder.CreateTableIfNotExists(
            "origin_fresh_rows",
            table => new
            {
                id = table.Column<int>(type: "INTEGER", nullable: false),
                managed_value = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_origin_fresh_rows", row => row.id));

        AppendSideEffectEnsure(builder, "origin_fresh_rows");
        builder.RenameColumnIfExists("managed_value", "origin_fresh_rows", "renamed_value");
        builder.EnsureModelManagedDataFromModel(
            "origin_fresh_rows",
            ["id"],
            ["INTEGER"],
            ["id", "renamed_value"],
            ["INTEGER", "TEXT"],
            new object?[,] { { 2, "expected" } });

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-managed-fresh-table-rename-origin"));

        var origin = preflight.Assessments[4].DeferredOrigin;

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[4].Action);
        Assert.Equal("runtime_validation_required", preflight.Assessments[4].Code);
        Assert.NotNull(origin);
        Assert.Equal(3, origin.OperationOrdinal);
    }
}
