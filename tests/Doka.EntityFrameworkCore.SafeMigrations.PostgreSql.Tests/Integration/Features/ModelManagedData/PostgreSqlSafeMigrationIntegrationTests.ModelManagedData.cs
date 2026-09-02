namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests
{
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence)]
    public async Task NewlyCreatedTableAndModelManagedDataConvergeInOneInitialDeployment(
        SafeMigrationScaffoldingMode mode
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);

        if (mode == SafeMigrationScaffoldingMode.Strict)
        {
            _ = builder.CreateTableIfNotExists(
                "model_initial_roles",
                table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                },
                constraints: table => table.PrimaryKey("pk_model_initial_roles", value => value.id));
        }
        else
        {
            _ = builder.ConvergeTableFromModel(
                "model_initial_roles",
                table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                },
                constraints: table => table.PrimaryKey("pk_model_initial_roles", value => value.id));
        }

        _ = builder.EnsureModelManagedDataFromModel(
            "model_initial_roles",
            ["id"],
            ["integer"],
            ["id", "name"],
            ["integer", "character varying(64)"],
            new object?[,] { { 1, "administrator" } });

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions($"model-data-initial-{mode}"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions($"model-data-initial-{mode}-replay"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations);

        var tableAssessment = Assert.Single(
            preflight.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureTable);

        var dataAssessment = Assert.Single(
            preflight.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureModelManagedData);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, tableAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, tableAssessment.Action);
        Assert.Equal(SafeMigrationObservedState.Missing, dataAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, dataAssessment.Action);
        Assert.Equal("projected_missing", dataAssessment.Code);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
        Assert.Equal(1, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_initial_roles "
            + "WHERE id = 1 AND name = 'administrator';"));
    }

    [Fact]
    public async Task ModelManagedDataBlocksUniquePrerequisiteAndUnmodeledDependencyEdges()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE model_edge_roles ("
            + "id integer NOT NULL PRIMARY KEY, code varchar(64) NOT NULL UNIQUE, name varchar(64) NULL);"
            + "CREATE TABLE model_edge_audit (id integer NOT NULL PRIMARY KEY, role_id integer NOT NULL, "
            + "CONSTRAINT fk_model_edge_audit_role FOREIGN KEY (role_id) "
            + "REFERENCES model_edge_roles (id) ON DELETE CASCADE);"
            + "INSERT INTO model_edge_roles (id, code, name) VALUES "
            + "(1, 'administrator', NULL), (2, 'member', 'Member');"
            + "INSERT INTO model_edge_audit (id, role_id) VALUES (11, 1);");

        await using var context = CreateContext(connectionString);
        var runner = context.GetService<ISafeMigrationRunner>();
        var uniqueCollision = new MigrationBuilder(context.Database.ProviderName!);
        _ = uniqueCollision.EnsureModelManagedDataFromModel(
            "model_edge_roles",
            ["id"],
            ["integer"],
            ["id", "code", "name"],
            ["integer", "character varying(64)", "character varying(64)"],
            new object?[,] { { 3, "member", "Other member" } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["code"])]);
        var missingPrerequisite = new MigrationBuilder(context.Database.ProviderName!);
        _ = missingPrerequisite.EnsureModelManagedDataFromModel(
            "model_edge_missing",
            ["id"],
            ["integer"],
            ["id"],
            ["integer"],
            new object?[,] { { 1 } });
        var unmodeledDependency = new MigrationBuilder(context.Database.ProviderName!);
        _ = unmodeledDependency.DeleteModelManagedDataFromModel(
            "model_edge_roles",
            ["id"],
            ["integer"],
            new object?[,] { { 1 } },
            ["id", "code", "name"],
            ["integer", "character varying(64)", "character varying(64)"],
            new object?[,] { { 1, "administrator", null } });

        var uniqueReport = await runner.AnalyzeAsync(
            context,
            uniqueCollision.Operations,
            new SafeMigrationRunOptions("model-data-unique-collision"),
            CancellationToken.None);

        var missingReport = await runner.AnalyzeAsync(
            context,
            missingPrerequisite.Operations,
            new SafeMigrationRunOptions("model-data-missing-prerequisite"),
            CancellationToken.None);

        var dependencyReport = await runner.AnalyzeAsync(
            context,
            unmodeledDependency.Operations,
            new SafeMigrationRunOptions("model-data-unmodeled-dependency"),
            CancellationToken.None);

        _ = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteOperationsAsync(context, unmodeledDependency.Operations));

        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(uniqueReport.Assessments).ObservedState);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing,
            Assert.Single(missingReport.Assessments).ObservedState);
        Assert.Equal(SafeMigrationObservedState.Unsupported,
            Assert.Single(dependencyReport.Assessments).ObservedState);
        Assert.Equal(1, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_edge_roles WHERE id = 1 AND name IS NULL;"));
        Assert.Equal(1, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_edge_audit WHERE role_id = 1;"));
    }

    [Fact]
    public async Task ModelManagedUpdateHandlesNullSourceAndMixedTargetRowsAtomically()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE model_null_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NULL);"
            + "INSERT INTO model_null_roles (id, name) VALUES (1, NULL), (2, 'Owner');");

        await using var context = CreateContext(connectionString);
        var update = new MigrationBuilder(context.Database.ProviderName!);
        _ = update.UpdateModelManagedDataFromModel(
            "model_null_roles",
            ["id"],
            ["integer"],
            new object?[,] { { 1 }, { 2 } },
            ["name"],
            ["character varying(64)"],
            new object?[,] { { null }, { "Member" } },
            new object?[,] { { "Owner" }, { "Owner" } });

        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions("model-data-null-update"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, update.Operations);
        await ExecuteOperationsAsync(context, update.Operations);

        Assert.Equal(SafeMigrationObservedState.TransitionReady,
            Assert.Single(preflight.Assessments).ObservedState);
        Assert.Equal(2, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_null_roles WHERE name = 'Owner';"));
    }

    [Fact]
    public async Task OrderedChildDeleteDischargesOnlyTheExactLiveParentDependency()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE model_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NOT NULL);"
            + "CREATE TABLE model_user_roles (id integer NOT NULL PRIMARY KEY, role_id integer NOT NULL, "
            + "CONSTRAINT fk_model_user_roles_role FOREIGN KEY (role_id) "
            + "REFERENCES model_roles (id) ON DELETE CASCADE);"
            + "INSERT INTO model_roles (id, name) VALUES (1, 'administrator'), (2, 'member');"
            + "INSERT INTO model_user_roles (id, role_id) VALUES (11, 1), (21, 2), (22, 2);");

        await using var context = CreateContext(connectionString);
        var exact = ModelManagedDependencyDeletion(context.Database.ProviderName!, roleId: 1, userRoleId: 11);
        var runner = context.GetService<ISafeMigrationRunner>();
        var exactPreflight = await runner.AnalyzeAsync(
            context,
            exact.Operations,
            new SafeMigrationRunOptions("model-data-exact-dependency"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, exact.Operations);

        var unmatched = ModelManagedDependencyDeletion(context.Database.ProviderName!, roleId: 2, userRoleId: 21);
        var unmatchedPreflight = await runner.AnalyzeAsync(
            context,
            unmatched.Operations,
            new SafeMigrationRunOptions("model-data-unmatched-dependency"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, exactPreflight.Status);
        Assert.All(exactPreflight.Assessments, assessment =>
            Assert.Equal(SafeMigrationObservedState.TransitionReady, assessment.ObservedState));
        Assert.Equal("projected_dependency_handoff", exactPreflight.Assessments[1].Code);
        Assert.Equal(0, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_roles WHERE id = 1;"));
        Assert.Equal(0, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_user_roles WHERE role_id = 1;"));
        Assert.Equal(SafeMigrationReportStatus.Blocked, unmatchedPreflight.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, unmatchedPreflight.Assessments[1].ObservedState);
        Assert.Equal(1, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_roles WHERE id = 2;"));
        Assert.Equal(2, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_user_roles WHERE role_id = 2;"));
    }

    [Fact]
    public async Task ModelManagedDataTransitionsAreIdempotentAndRejectDrift()
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE model_roles ("
            + "id integer NOT NULL PRIMARY KEY, code varchar(64) NOT NULL UNIQUE, name varchar(64) NULL);"
            + "INSERT INTO model_roles (id, code, name) VALUES "
            + "(1, 'administrator', 'Administrator'), (3, 'drift', 'Live value');");

        await using var context = CreateContext(connectionString);
        var ensure = new MigrationBuilder(context.Database.ProviderName!);
        _ = ensure.EnsureModelManagedDataFromModel(
            "model_roles",
            ["id"],
            ["integer"],
            ["id", "code", "name"],
            ["integer", "character varying(64)", "character varying(64)"],
            new object?[,]
            {
                { 1, "administrator", "Administrator" },
                { 2, "member", "Member" },
            },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["code"])]);

        var runner = context.GetService<ISafeMigrationRunner>();
        var ensurePreflight = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions("model-data-ensure"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, ensure.Operations);
        var ensureReplay = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions("model-data-ensure-replay"),
            CancellationToken.None);

        var update = new MigrationBuilder(context.Database.ProviderName!);
        _ = update.UpdateModelManagedDataFromModel(
            "model_roles",
            ["id"],
            ["integer"],
            new object?[,] { { 1 }, { 2 } },
            ["name"],
            ["character varying(64)"],
            new object?[,] { { "Administrator" }, { "Member" } },
            new object?[,] { { "Owner" }, { "Member" } });

        var updatePreflight = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions("model-data-update"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, update.Operations);

        var delete = new MigrationBuilder(context.Database.ProviderName!);
        _ = delete.DeleteModelManagedDataFromModel(
            "model_roles",
            ["id"],
            ["integer"],
            new object?[,] { { 1 }, { 99 } },
            ["id", "code", "name"],
            ["integer", "character varying(64)", "character varying(64)"],
            new object?[,]
            {
                { 1, "administrator", "Owner" },
                { 99, "absent", "Absent" },
            });

        var deletePreflight = await runner.AnalyzeAsync(
            context,
            delete.Operations,
            new SafeMigrationRunOptions("model-data-delete"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, delete.Operations);
        await ExecuteOperationsAsync(context, delete.Operations);

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        _ = drift.EnsureModelManagedDataFromModel(
            "model_roles",
            ["id"],
            ["integer"],
            ["id", "code", "name"],
            ["integer", "character varying(64)", "character varying(64)"],
            new object?[,] { { 3, "drift", "Source-controlled value" } });

        var driftPreflight = await runner.AnalyzeAsync(
            context,
            drift.Operations,
            new SafeMigrationRunOptions("model-data-drift"),
            CancellationToken.None);

        _ = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context, drift.Operations));

        Assert.Equal(SafeMigrationReportStatus.Ready, ensurePreflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(ensurePreflight.Assessments).ObservedState);
        Assert.Equal(SafeMigrationReportStatus.Ready, ensureReplay.Status);
        Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(ensureReplay.Assessments).Action);
        Assert.Equal(SafeMigrationObservedState.TransitionReady,
            Assert.Single(updatePreflight.Assessments).ObservedState);
        Assert.Equal(SafeMigrationObservedState.TransitionReady,
            Assert.Single(deletePreflight.Assessments).ObservedState);
        Assert.Equal(SafeMigrationReportStatus.Blocked, driftPreflight.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(driftPreflight.Assessments).ObservedState);
        Assert.Equal(0, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_roles WHERE id = 1;"));
        Assert.Equal(1, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_roles WHERE id = 2 AND name = 'Member';"));
        Assert.Equal(1, await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM model_roles WHERE id = 3 AND name = 'Live value';"));
    }

    private static MigrationBuilder ModelManagedDependencyDeletion(
        string providerName,
        int roleId,
        int userRoleId
    )
    {
        var builder = new MigrationBuilder(providerName);
        _ = builder.DeleteModelManagedDataFromModel(
            "model_user_roles",
            ["id"],
            ["integer"],
            new object?[,] { { userRoleId } },
            ["id", "role_id"],
            ["integer", "integer"],
            new object?[,] { { userRoleId, roleId } });
        _ = builder.DeleteModelManagedDataFromModel(
            "model_roles",
            ["id"],
            ["integer"],
            new object?[,] { { roleId } },
            ["id", "name"],
            ["integer", "character varying(64)"],
            new object?[,] { { roleId, roleId == 1 ? "administrator" : "member" } },
            foreignKeys:
            [
                new ExpectedModelManagedDataForeignKeyDefinition(
                    "model_user_roles",
                    ["role_id"],
                    ["id"]),
            ]);

        return builder;
    }
}
