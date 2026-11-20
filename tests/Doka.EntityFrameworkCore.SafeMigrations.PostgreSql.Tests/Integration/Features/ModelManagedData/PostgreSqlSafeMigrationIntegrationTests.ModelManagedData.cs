namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ModelManagedData PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlModelManagedDataIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlModelManagedDataIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Newly Created Table And Model Managed Data Converge In One Initial Deployment.</summary>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence)]
    public async Task NewlyCreatedTableAndModelManagedDataConvergeInOneInitialDeployment(
        SafeMigrationScaffoldingMode mode
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
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

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions($"model-data-initial-{mode}"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        var replay = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions($"model-data-initial-{mode}-replay"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // Assert
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
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_initial_roles "
                + "WHERE id = 1 AND name = 'administrator';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Model Managed Data Blocks Unique Prerequisite And Unmodeled Dependency Edges.</summary>
    /// <param name="caseIndex">The unique, missing-prerequisite, or unmodeled-dependency boundary.</param>
    /// <param name="expectedState">The precise native classification for the selected boundary.</param>
    [Theory]
    [InlineData(0, SafeMigrationObservedState.DataBlocked)]
    [InlineData(1, SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(2, SafeMigrationObservedState.Unsupported)]
    public async Task ModelManagedDataBlocksUniquePrerequisiteAndUnmodeledDependencyEdges(
        int caseIndex,
        SafeMigrationObservedState expectedState
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE model_edge_roles (id integer NOT NULL PRIMARY KEY, code varchar(64) NOT NULL UNIQUE,
            name varchar(64) NULL);CREATE TABLE model_edge_audit (id integer NOT NULL PRIMARY KEY,
            role_id integer NOT NULL, CONSTRAINT fk_model_edge_audit_role FOREIGN KEY (role_id) REFERENCES
            model_edge_roles (id) ON DELETE CASCADE);INSERT INTO model_edge_roles (id,
            code, name) VALUES (1, 'administrator', NULL), (2, 'member', 'Member');INSERT INTO model_edge_audit (id,
            role_id) VALUES (11, 1);
            """,
            cancellationToken: CancellationToken.None);

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

        var operations = caseIndex switch
        {
            0 => uniqueCollision.Operations,
            1 => missingPrerequisite.Operations,
            2 => unmodeledDependency.Operations,
            _ => throw new ArgumentOutOfRangeException(nameof(caseIndex)),
        };

        // Act
        var report = await runner.AnalyzeAsync(
            context,
            operations,
            new SafeMigrationRunOptions($"model-data-edge-{caseIndex}"),
            CancellationToken.None);

        var failure = caseIndex == 2
            ? await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, operations, CancellationToken.None))
            : null;

        // Assert
        Assert.Equal(expectedState,
            Assert.Single(report.Assessments)
                .ObservedState);

        if (caseIndex == 2)
        {
            Assert.IsType<PostgresException>(failure);
        }

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_edge_roles WHERE id = 1 AND name IS NULL;",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_edge_audit WHERE role_id = 1;",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Model Managed Update Handles Null Source And Mixed Target Rows Atomically.</summary>
    [Fact]
    public async Task ModelManagedUpdateHandlesNullSourceAndMixedTargetRowsAtomically()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE model_null_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NULL);INSERT
            INTO model_null_roles (id,
            name) VALUES (1, NULL), (2, 'Owner');
            """,
            cancellationToken: CancellationToken.None);

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

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions("model-data-null-update"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, update.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, update.Operations, CancellationToken.None);

        // Assert
        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(preflight.Assessments)
                .ObservedState);

        Assert.Equal(
            2,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_null_roles WHERE name = 'Owner';",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Ordered child deletion defers parent proof and preserves unmatched dependency rows.</summary>
    /// <param name="hasUnmatchedChild">Whether another dependent row must prevent executing the parent delete.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedChildDeleteDefersStaleParentDependencyEvidence(
        bool hasUnmatchedChild
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE model_roles (id integer NOT NULL PRIMARY KEY, name varchar(64) NOT NULL);CREATE
            TABLE model_user_roles (id integer NOT NULL PRIMARY KEY,
            role_id integer NOT NULL, CONSTRAINT fk_model_user_roles_role FOREIGN KEY (role_id) REFERENCES
            model_roles (id) ON DELETE CASCADE);INSERT INTO model_roles (id,
            name) VALUES (1, 'administrator'), (2, 'member');INSERT INTO model_user_roles (id,
            role_id) VALUES (11, 1), (21, 2), (22, 2);
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var roleId = hasUnmatchedChild
            ? 2
            : 1;

        var childId = hasUnmatchedChild
            ? 21
            : 11;

        var builder = ModelManagedDependencyDeletion(context.Database.ProviderName!, roleId, childId);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("model-data-ordered-dependency"),
            CancellationToken.None);

        if (!hasUnmatchedChild)
        {
            await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        }

        // Assert
        Assert.Equal(2, preflight.Assessments.Count);
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, preflight.Status);
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, preflight.Assessments[1].Action);

        if (!hasUnmatchedChild)
        {
            Assert.Equal(SafeMigrationObservedState.TransitionReady, preflight.Assessments[0].ObservedState);
            Assert.Equal("projected_model_managed_data_state_unknown", preflight.Assessments[1].AnalysisCode);
        }

        Assert.Equal(hasUnmatchedChild
                ? 1
                : 0,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM model_roles WHERE id = {roleId};",
                cancellationToken: CancellationToken.None));

        Assert.Equal(hasUnmatchedChild
                ? 2
                : 0,
            await ScalarIntAsync(
                connectionString,
                $"SELECT COUNT(*) FROM model_user_roles WHERE role_id = {roleId};",
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Model Managed Data Transitions Are Idempotent And Reject Drift.</summary>
    [Fact]
    public async Task ModelManagedDataTransitionsAreIdempotentAndRejectDrift()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            """
            CREATE TABLE model_roles (id integer NOT NULL PRIMARY KEY, code varchar(64)
            NOT NULL UNIQUE,
            name varchar(64) NULL);INSERT INTO model_roles (id, code, name) VALUES (1,
            'administrator', 'Administrator'), (3, 'drift', 'Live value');
            """,
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var ensure = new MigrationBuilder(context.Database.ProviderName!);
        _ = ensure.EnsureModelManagedDataFromModel(
            "model_roles",
            ["id"],
            ["integer"],
            ["id", "code", "name"],
            ["integer", "character varying(64)", "character varying(64)"],
            new object?[,] { { 1, "administrator", "Administrator" }, { 2, "member", "Member" } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["code"])]);

        var runner = context.GetService<ISafeMigrationRunner>();
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

        var delete = new MigrationBuilder(context.Database.ProviderName!);
        _ = delete.DeleteModelManagedDataFromModel(
            "model_roles",
            ["id"],
            ["integer"],
            new object?[,] { { 1 }, { 99 } },
            ["id", "code", "name"],
            ["integer", "character varying(64)", "character varying(64)"],
            new object?[,] { { 1, "administrator", "Owner" }, { 99, "absent", "Absent" } });

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        _ = drift.EnsureModelManagedDataFromModel(
            "model_roles",
            ["id"],
            ["integer"],
            ["id", "code", "name"],
            ["integer", "character varying(64)", "character varying(64)"],
            new object?[,] { { 3, "drift", "Source-controlled value" } });

        // Act
        var ensurePreflight = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions("model-data-ensure"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, ensure.Operations, CancellationToken.None);
        var ensureReplay = await runner.AnalyzeAsync(
            context,
            ensure.Operations,
            new SafeMigrationRunOptions("model-data-ensure-replay"),
            CancellationToken.None);

        var updatePreflight = await runner.AnalyzeAsync(
            context,
            update.Operations,
            new SafeMigrationRunOptions("model-data-update"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, update.Operations, CancellationToken.None);

        var deletePreflight = await runner.AnalyzeAsync(
            context,
            delete.Operations,
            new SafeMigrationRunOptions("model-data-delete"),
            CancellationToken.None);

        await ExecuteOperationsAsync(context, delete.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, delete.Operations, CancellationToken.None);

        var driftPreflight = await runner.AnalyzeAsync(
            context,
            drift.Operations,
            new SafeMigrationRunOptions("model-data-drift"),
            CancellationToken.None);

        var driftFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(
            context,
            drift.Operations,
            CancellationToken.None));

        // Assert
        Assert.IsType<PostgresException>(driftFailure);
        Assert.Equal(SafeMigrationReportStatus.Ready, ensurePreflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing,
            Assert.Single(ensurePreflight.Assessments)
                .ObservedState);

        Assert.Equal(SafeMigrationReportStatus.Ready, ensureReplay.Status);
        Assert.Equal(SafeMigrationAction.NoOp,
            Assert.Single(ensureReplay.Assessments)
                .Action);

        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(updatePreflight.Assessments)
                .ObservedState);

        Assert.Equal(
            SafeMigrationObservedState.TransitionReady,
            Assert.Single(deletePreflight.Assessments)
                .ObservedState);

        var driftAssessment = Assert.Single(driftPreflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, driftPreflight.Status);
        Assert.Equal(SafeMigrationObservedState.Different, driftAssessment.ObservedState);
        Assert.Equal("model_managed_row_content", Assert.Single(driftAssessment.Differences).Facet);

        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_roles WHERE id = 1;",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_roles WHERE id = 2 AND name = 'Member';",
                cancellationToken: CancellationToken.None));

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*) FROM model_roles WHERE id = 3 AND name = 'Live value';",
                cancellationToken: CancellationToken.None));
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
            new object?[,]
            {
                {
                    roleId,
                    roleId == 1
                        ? "administrator"
                        : "member",
                },
            },
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
