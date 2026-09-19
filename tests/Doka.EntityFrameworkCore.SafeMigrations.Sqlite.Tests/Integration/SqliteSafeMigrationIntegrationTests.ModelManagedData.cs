namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    [Fact]
    public async Task CompositeKeysUpdatesDeletesAndNullUniqueValuesConvergeInOrder()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE composite_managed_rows ("
            + "tenant_id INTEGER NOT NULL, id INTEGER NOT NULL, code TEXT NULL, managed_value TEXT NOT NULL, "
            + "PRIMARY KEY (tenant_id, id), UNIQUE (code)); "
            + "INSERT INTO composite_managed_rows (tenant_id, id, code, managed_value) VALUES "
            + "(1, 1, 'updated', 'source'), (2, 1, 'removed', 'source');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "composite_managed_rows",
            ["tenant_id", "id"],
            ["INTEGER", "INTEGER"],
            ["tenant_id", "id", "code", "managed_value"],
            ["INTEGER", "INTEGER", "TEXT", "TEXT"],
            new object?[,] { { 1, 2, null, "target" } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["code"])]);
        builder.UpdateModelManagedDataFromModel(
            "composite_managed_rows",
            ["tenant_id", "id"],
            ["INTEGER", "INTEGER"],
            new object?[,] { { 1, 1 } },
            ["code", "managed_value"],
            ["TEXT", "TEXT"],
            new object?[,] { { "updated", "source" } },
            new object?[,] { { "updated", "target" } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["code"])]);
        builder.DeleteModelManagedDataFromModel(
            "composite_managed_rows",
            ["tenant_id", "id"],
            ["INTEGER", "INTEGER"],
            new object?[,] { { 2, 1 } },
            ["tenant_id", "id", "code", "managed_value"],
            ["INTEGER", "INTEGER", "TEXT", "TEXT"],
            new object?[,] { { 2, 1, "removed", "source" } });
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-composite-model-data"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-composite-model-data-replay"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(
            2,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM composite_managed_rows WHERE managed_value = 'target';"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM composite_managed_rows WHERE code IS NULL;"));
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM composite_managed_rows WHERE tenant_id = 2 AND id = 1;"));
    }

    [Fact]
    public async Task ModelManagedUpdateWithUnexpectedSourceValue_IsRejectedWithoutOverwrite()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE managed_drift (id INTEGER NOT NULL PRIMARY KEY, managed_value TEXT NOT NULL); "
            + "INSERT INTO managed_drift (id, managed_value) VALUES (1, 'operator-change');");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.UpdateModelManagedDataFromModel(
            "managed_drift",
            ["id"],
            ["INTEGER"],
            new object?[,] { { 1 } },
            ["managed_value"],
            ["TEXT"],
            new object?[,] { { "source" } },
            new object?[,] { { "target" } });

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-model-data-drift"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM managed_drift WHERE id = 1 AND managed_value = 'operator-change';"));
    }

    [Fact]
    public async Task InitialTableAndModelManagedData_ApplyReplayAndVerify()
    {
        await using var connection = await OpenConnectionAsync();
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "managed_roles",
            table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                Code = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_managed_roles", value => value.Id);
                table.UniqueConstraint("uq_managed_roles_code", value => value.Code);
            });
        builder.EnsureModelManagedDataFromModel(
            "managed_roles",
            ["Id"],
            ["INTEGER"],
            ["Id", "Code"],
            ["INTEGER", "TEXT"],
            new object?[,] { { 1, "administrator" } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-managed-initial"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-managed-replay"));

        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-managed-postflight"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connection,
                "SELECT COUNT(*) FROM managed_roles WHERE Id = 1 AND Code = 'administrator';"));
    }

    [Fact]
    public async Task ModelManagedData_UniqueCollisionAndUnmodeledDependencyFailClosed()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE managed_roles ("
            + "Id INTEGER NOT NULL PRIMARY KEY, Code TEXT NOT NULL, CONSTRAINT uq_managed_roles_code UNIQUE (Code)); "
            + "CREATE TABLE managed_assignments ("
            + "Id INTEGER NOT NULL PRIMARY KEY, RoleId INTEGER NOT NULL, "
            + "CONSTRAINT fk_managed_assignments_role FOREIGN KEY (RoleId) REFERENCES managed_roles (Id)); "
            + "INSERT INTO managed_roles (Id, Code) VALUES (1, 'administrator'), (2, 'member'); "
            + "INSERT INTO managed_assignments (Id, RoleId) VALUES (11, 1);");
        await using var context = CreateContext(connection);
        var collision = new MigrationBuilder(context.Database.ProviderName!);
        collision.EnsureModelManagedDataFromModel(
            "managed_roles",
            ["Id"],
            ["INTEGER"],
            ["Id", "Code"],
            ["INTEGER", "TEXT"],
            new object?[,] { { 3, "member" } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);
        var unmodeledDelete = new MigrationBuilder(context.Database.ProviderName!);
        unmodeledDelete.DeleteModelManagedDataFromModel(
            "managed_roles",
            ["Id"],
            ["INTEGER"],
            new object?[,] { { 1 } },
            ["Id", "Code"],
            ["INTEGER", "TEXT"],
            new object?[,] { { 1, "administrator" } });
        var runner = context.GetService<ISafeMigrationRunner>();

        var collisionReport = await runner.AnalyzeAsync(
            context,
            collision.Operations,
            new SafeMigrationRunOptions("sqlite-managed-collision"));

        var dependencyReport = await runner.AnalyzeAsync(
            context,
            unmodeledDelete.Operations,
            new SafeMigrationRunOptions("sqlite-managed-unmodeled-dependency"));

        var collisionAssessment = Assert.Single(collisionReport.Assessments);
        var dependencyAssessment = Assert.Single(dependencyReport.Assessments);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, collisionAssessment.ObservedState);
        Assert.Equal("model_managed_unique_collision", collisionAssessment.AnalysisCode);
        Assert.Equal(SafeMigrationObservedState.Unsupported, dependencyAssessment.ObservedState);
        Assert.Equal("model_managed_unmodeled_dependency", dependencyAssessment.AnalysisCode);
        Assert.Equal(2, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM managed_roles;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM managed_assignments;"));
    }

    [Fact]
    public async Task OrderedModelManagedDependencyDeletion_IsProjectedAndAppliedWithoutCascade()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE managed_roles (Id INTEGER NOT NULL PRIMARY KEY, Code TEXT NOT NULL); "
            + "CREATE TABLE managed_assignments ("
            + "Id INTEGER NOT NULL PRIMARY KEY, RoleId INTEGER NOT NULL, "
            + "CONSTRAINT fk_managed_assignments_role FOREIGN KEY (RoleId) "
            + "REFERENCES managed_roles (Id) ON DELETE CASCADE); "
            + "INSERT INTO managed_roles (Id, Code) VALUES (1, 'administrator'); "
            + "INSERT INTO managed_assignments (Id, RoleId) VALUES (11, 1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DeleteModelManagedDataFromModel(
            "managed_assignments",
            ["Id"],
            ["INTEGER"],
            new object?[,] { { 11 } },
            ["Id", "RoleId"],
            ["INTEGER", "INTEGER"],
            new object?[,] { { 11, 1 } });
        builder.DeleteModelManagedDataFromModel(
            "managed_roles",
            ["Id"],
            ["INTEGER"],
            new object?[,] { { 1 } },
            ["Id", "Code"],
            ["INTEGER", "TEXT"],
            new object?[,] { { 1, "administrator" } },
            foreignKeys:
            [
                new ExpectedModelManagedDataForeignKeyDefinition(
                    "managed_assignments",
                    ["RoleId"],
                    ["Id"]),
            ]);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-managed-dependency-handoff"));

        await ExecuteOperationsAsync(context, builder.Operations);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlite-managed-dependency-replay"));

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.All(preflight.Assessments, assessment =>
            Assert.Equal(SafeMigrationObservedState.TransitionReady, assessment.ObservedState));
        Assert.Equal("projected_dependency_handoff", preflight.Assessments[1].Code);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM managed_roles;"));
        Assert.Equal(0, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM managed_assignments;"));
    }

    [Fact]
    public async Task ModelManagedDependencyWithoutPriorDelete_IsDataBlocked()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE managed_roles (Id INTEGER NOT NULL PRIMARY KEY, Code TEXT NOT NULL); "
            + "CREATE TABLE managed_assignments ("
            + "Id INTEGER NOT NULL PRIMARY KEY, RoleId INTEGER NOT NULL, "
            + "FOREIGN KEY (RoleId) REFERENCES managed_roles (Id) ON DELETE CASCADE); "
            + "INSERT INTO managed_roles (Id, Code) VALUES (1, 'administrator'); "
            + "INSERT INTO managed_assignments (Id, RoleId) VALUES (11, 1);");
        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DeleteModelManagedDataFromModel(
            "managed_roles",
            ["Id"],
            ["INTEGER"],
            new object?[,] { { 1 } },
            ["Id", "Code"],
            ["INTEGER", "TEXT"],
            new object?[,] { { 1, "administrator" } },
            foreignKeys:
            [
                new ExpectedModelManagedDataForeignKeyDefinition(
                    "managed_assignments",
                    ["RoleId"],
                    ["Id"]),
            ]);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlite-managed-dependency-blocked"));

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, assessment.ObservedState);
        Assert.Equal("model_managed_dependency", assessment.AnalysisCode);
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM managed_roles;"));
        Assert.Equal(1, await ScalarIntAsync(connection, "SELECT COUNT(*) FROM managed_assignments;"));
    }
}
