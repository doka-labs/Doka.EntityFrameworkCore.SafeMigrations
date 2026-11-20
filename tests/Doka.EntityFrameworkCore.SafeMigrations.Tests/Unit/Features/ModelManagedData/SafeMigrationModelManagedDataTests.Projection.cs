namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedDataTests
{
    /// <summary>A newly accepted strict table supplies the complete empty-row proof for a following seed.</summary>
    [Fact]
    public void AcceptedTableCreationProjectsFollowingModelManagedEnsureAsMissing()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = Operation(
            new EnsureTableIntent(
                RoleTable(),
                SafeMigrationTableMode.StrictDefinition));

        Accept(projection, table, SafeMigrationObservedState.Missing);

        var ensure = RoleEnsure();

        // Act
        var projected = projection.Project(
            ensure,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        var decision = SafeMigrationDecisionPlanner.Plan(
            ensure.Intent.Kind,
            projected.ObservedState,
            ensure.Policy,
            projected.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
    }

    /// <summary>Ordinary provider table creation supplies the same empty-row seed prerequisite.</summary>
    [Fact]
    public void ProviderTableCreationProjectsFollowingModelManagedEnsureAsMissing()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var createTable = ProviderRoleTable();

        projection.ObserveProviderPostcondition(createTable);

        // Act
        var projected = projection.Project(
            RoleEnsure(),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>A provider rename transports the created owner's empty-row proof to its new identity.</summary>
    [Fact]
    public void ProviderTableRenamePreservesNewTableEmptyRowProof()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        projection.ObserveProviderPostcondition(ProviderRoleTable());
        projection.ObserveProviderPostcondition(
            new RenameTableOperation
            {
                Name = "roles",
                NewName = "application_roles",
            });

        // Act
        var projected = projection.Project(
            RoleEnsure("application_roles"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>An accepted safe rename transports empty-row evidence without consulting unrelated live rows.</summary>
    [Fact]
    public void AcceptedTableRenamePreservesNewTableEmptyRowProof()
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var rename = Operation(new RenameTableIntent("roles", newName: "application_roles"));

        Accept(projection, rename, SafeMigrationObservedState.Matching);

        // Act
        var projected = projection.Project(
            RoleEnsure("application_roles"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>A provider column rename preserves the new table's empty-row proof under new column names.</summary>
    [Fact]
    public void ProviderColumnRenamePreservesNewTableEmptyRowProof()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        projection.ObserveProviderPostcondition(ProviderRoleTable());
        projection.ObserveProviderPostcondition(
            new RenameColumnOperation
            {
                Table = "roles",
                Name = "name",
                NewName = "display_name",
            });

        var ensure = RoleEnsureWithDisplayName();

        // Act
        var projected = projection.Project(
            ensure,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>A safe column rename retains empty-row knowledge for subsequent model-managed data.</summary>
    [Fact]
    public void AcceptedColumnRenamePreservesNewTableEmptyRowProof()
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var rename = Operation(new RenameColumnIntent("name", "roles", "display_name"));

        Accept(projection, rename, SafeMigrationObservedState.Matching);

        var ensure = RoleEnsureWithDisplayName();

        // Act
        var projected = projection.Project(
            ensure,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>Earlier unrelated DML cannot invalidate an empty-row proof from later table creation.</summary>
    [Fact]
    public void ProviderMutationBeforeTableCreationDoesNotPoisonNewTableEmptyRowProof()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "existing_table",
                Columns = ["id"],
                Values = new object[,] { { 1 } },
            });

        projection.ObserveProviderPostcondition(ProviderRoleTable());

        // Act
        var projected = projection.Project(
            RoleEnsure(),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>A dropped owner cannot retain the created table's empty-row seed proof.</summary>
    [Fact]
    public void ProviderTableDropInvalidatesNewTableEmptyRowProof()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        projection.ObserveProviderPostcondition(ProviderRoleTable());
        projection.ObserveProviderPostcondition(new DropTableOperation { Name = "roles" });

        // Act
        var projected = projection.Project(RoleEnsure(), live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_prerequisite_missing", projected.Code);
        Assert.False(projected.PostconditionSatisfied);
    }

    /// <summary>An empty table proves deletion unnecessary but cannot invent an update's missing source row.</summary>
    /// <param name="delete">Whether the independent transition deletes rather than updates.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewlyCreatedTableProjectsModelManagedOperationFamiliesConservatively(
        bool delete
    )
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var update = RoleUpdate();
        var deletion = Operation(
            new DeleteModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                new object?[,] { { 1 } },
                ["id", "name"],
                ["int", "varchar(64)"],
                new object?[,] { { 1, "administrator" } },
                schema: null,
                foreignKeys: null));

        // Act
        var projected = projection.Project(
            delete
                ? deletion
                : update,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(delete
                ? SafeMigrationObservedState.Missing
                : SafeMigrationObservedState.PrerequisiteMissing,
            projected.ObservedState);

        if (delete)
        {
            Assert.True(projected.PostconditionSatisfied);
        }
        else
        {
            Assert.Equal("projected_prerequisite_missing", projected.Code);
        }
    }

    /// <summary>Existing structure alone cannot certify that an authored seed row is absent.</summary>
    [Fact]
    public void ExistingTableNeverInfersMissingModelManagedRowsFromStructureAlone()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = Operation(
            new EnsureTableIntent(
                RoleTable(),
                SafeMigrationTableMode.ConvergenceContainer));

        Accept(projection, table, SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(RoleEnsure(), live);

        // Assert
        Assert.Same(live, projected);
    }

    /// <summary>An incomplete projected table cannot provide the missing columns required by a seed.</summary>
    [Fact]
    public void IncompleteTableProjectionNeverInfersMissingModelManagedRows()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = Operation(
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "roles",
                    [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")]),
                SafeMigrationTableMode.StrictDefinition));

        Accept(projection, table, SafeMigrationObservedState.Missing);

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(RoleEnsure(), live);

        // Assert
        Assert.Same(live, projected);
    }

    /// <summary>Each ordinary provider DML family invalidates an otherwise valid empty-row seed proof.</summary>
    /// <param name="kind">The independently observed ordinary data operation.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProviderDataMutationInvalidatesNewTableEmptyRowProof(
        int kind
    )
    {
        // Arrange
        MigrationOperation[] dataOperations =
        [
            new InsertDataOperation
            {
                Table = "other_table",
                Columns = ["id"],
                Values = new object[,] { { 1 } },
            },
            new UpdateDataOperation
            {
                Table = "other_table",
                KeyColumns = ["id"],
                KeyValues = new object[,] { { 1 } },
                Columns = ["name"],
                Values = new object[,] { { "changed" } },
            },
            new DeleteDataOperation
            {
                Table = "other_table",
                KeyColumns = ["id"],
                KeyValues = new object[,] { { 1 } },
            },
        ];

        var projection = ProjectionWithNewRoleTable();
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        projection.ObserveProviderPostcondition(dataOperations[kind]);
        var projected = projection.Project(RoleEnsure(), live);

        // Assert
        Assert.True(projected.IsModelManagedProjectionUnknown);
        Assert.Equal("projected_model_managed_data_state_unknown", projected.Code);
    }

    /// <summary>Opaque provider SQL revokes the new-table structural proof before later seed decisions.</summary>
    [Fact]
    public void OpaqueProviderOperationInvalidatesNewTableEmptyRowProof()
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });

        // Act
        var projected = projection.Project(RoleEnsure(), live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_structure_state_unknown", projected.Code);
    }

    /// <summary>Accepted seed rows retain both their exact target identity and other missing-key knowledge.</summary>
    [Fact]
    public void AcceptedModelManagedRowsPreserveNewTableKnowledgeForLaterKeys()
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var administrator = RoleEnsure();

        Accept(projection, administrator, SafeMigrationObservedState.PrerequisiteMissing);

        var member = Operation(
            new EnsureModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                ["id", "name"],
                ["int", "varchar(64)"],
                new object?[,] { { 2, "member" } },
                schema: null,
                uniqueKeys: null));

        // Act
        var projectedAdministrator = projection.Project(
            administrator,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        var projectedMember = projection.Project(
            member,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, projectedAdministrator.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, projectedMember.ObservedState);
    }

    /// <summary>An overlapping seed with uncaptured cells remains unknown rather than incorrectly absent.</summary>
    [Fact]
    public void IncompleteProjectedRowNeverBecomesMissingFromNewTableProof()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = Operation(
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "roles",
                    [
                        new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
                        new ExpectedColumnDefinition(
                            "name",
                            typeof(string),
                            isNullable: false,
                            storeType: "varchar(64)"),
                        new ExpectedColumnDefinition(
                            "description",
                            typeof(string),
                            isNullable: true,
                            storeType: "varchar(256)"),
                    ]),
                SafeMigrationTableMode.StrictDefinition));

        var first = RoleEnsure();

        Accept(projection, table, SafeMigrationObservedState.Missing);
        Accept(projection, first, SafeMigrationObservedState.PrerequisiteMissing);

        var overlapping = Operation(
            new EnsureModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                ["id", "description"],
                ["int", "varchar(256)"],
                new object?[,] { { 1, "Built-in role" } },
                schema: null,
                uniqueKeys: null));

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(overlapping, live);

        // Assert
        Assert.True(projected.IsModelManagedProjectionUnknown);
        Assert.Equal("projected_model_managed_data_state_unknown", projected.Code);
    }

    /// <summary>Opaque SQL revokes the accepted child deletion proof needed by a later parent deletion.</summary>
    [Fact]
    public void OpaqueProviderMutationInvalidatesRowsAndDependencyHandoffTogether()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var childDelete = Operation(
            new DeleteModelManagedDataIntent(
                "user_roles",
                ["id"],
                ["int"],
                new object?[,] { { 11 } },
                ["id", "role_id"],
                ["int", "int"],
                new object?[,] { { 11, 1 } },
                schema: null,
                foreignKeys: null));

        var childAnalysis = EvidenceAnalysis(
            SafeMigrationObservedState.TransitionReady,
            [SafeMigrationModelManagedRowState.Source],
            []);

        var childDecision = SafeMigrationDecisionPlanner.Plan(
            childDelete.Intent.Kind,
            childAnalysis.ObservedState,
            childDelete.Policy,
            childAnalysis.RepairCapability);

        projection.Observe(childDelete, childAnalysis, childAnalysis, childDecision);
        projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });

        var parentDelete = Operation(
            new DeleteModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                new object?[,] { { 1 } },
                ["id", "name"],
                ["int", "varchar(64)"],
                new object?[,] { { 1, "administrator" } },
                schema: null,
                foreignKeys:
                [
                    new ExpectedModelManagedDataForeignKeyDefinition(
                        "user_roles",
                        ["role_id"],
                        ["id"]),
                ]));

        // Act
        var projected = projection.Project(
            parentDelete,
            EvidenceAnalysis(
                SafeMigrationObservedState.DataBlocked,
                [SafeMigrationModelManagedRowState.Source],
                [1]));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_structure_state_unknown", projected.Code);
    }

    /// <summary>
    /// Structural identity changes invalidate seed evidence with the strongest available owner proof.
    /// </summary>
    /// <param name="mutation">The accepted structural operation.</param>
    /// <param name="expectedCode">The expected projected diagnostic.</param>
    [Theory]
    [InlineData("column-drop", "projected_prerequisite_missing")]
    [InlineData("column-rename", "projected_structure_state_unknown")]
    [InlineData("table-drop", "projected_prerequisite_missing")]
    [InlineData("table-rename", "projected_structure_state_unknown")]
    public void StructuralIdentityChangesInvalidateModelManagedRowProjection(
        string mutation,
        string expectedCode
    )
    {
        // Arrange
        MigrationOperation operation = mutation switch
        {
            "column-drop" => new DropColumnOperation
            {
                Table = "roles",
                Name = "name",
            },
            "column-rename" => new RenameColumnOperation
            {
                Table = "roles",
                Name = "name",
                NewName = "display_name",
            },
            "table-drop" => new DropTableOperation { Name = "roles" },
            "table-rename" => new RenameTableOperation
            {
                Name = "roles",
                NewName = "renamed_roles",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        var projection = ProjectionWithAcceptedRole();

        projection.ObserveProviderPostcondition(operation);

        // Act
        var projected = projection.Project(RoleUpdate(), Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal(expectedCode, projected.Code);
    }

    /// <summary>Complete accepted candidate keys prove a following unique index on a newly created table.</summary>
    [Fact]
    public void AcceptedModelManagedCandidateKeyAuthorizesFollowingUniqueIndexOnNewTable()
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var administrator = RoleEnsure(
            1,
            "administrator",
            includeNameUniqueKey: true);

        var member = RoleEnsure(
            2,
            "member",
            includeNameUniqueKey: true);

        Accept(projection, administrator, SafeMigrationObservedState.PrerequisiteMissing);
        Accept(projection, member, SafeMigrationObservedState.PrerequisiteMissing);

        var index = Operation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ux_roles_name",
                    "roles",
                    [new ExpectedIndexKeyDefinition(column: "name")],
                    unique: true)));

        // Act
        var projected = projection.Project(
            index,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>A safe owner rename retains the accepted candidate-key proof for its later unique index.</summary>
    [Fact]
    public void AcceptedModelManagedCandidateKeySurvivesNewTableRename()
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var administrator = RoleEnsure(
            1,
            "administrator",
            includeNameUniqueKey: true);

        var member = RoleEnsure(
            2,
            "member",
            includeNameUniqueKey: true);

        Accept(projection, administrator, SafeMigrationObservedState.PrerequisiteMissing);
        Accept(projection, member, SafeMigrationObservedState.PrerequisiteMissing);
        Accept(
            projection,
            Operation(new RenameTableIntent("roles", newName: "application_roles")),
            SafeMigrationObservedState.Matching);

        var index = Operation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ux_application_roles_name",
                    "application_roles",
                    [new ExpectedIndexKeyDefinition(column: "name")],
                    unique: true)));

        // Act
        var projected = projection.Project(
            index,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    /// <summary>A seed batch lacking the declared candidate key revokes later unique-index data evidence.</summary>
    [Fact]
    public void ModelManagedBatchWithoutCandidateKeyInvalidatesFollowingUniqueIndexProof()
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var administrator = RoleEnsure(
            1,
            "administrator",
            includeNameUniqueKey: true);

        var member = RoleEnsure(
            2,
            "member",
            includeNameUniqueKey: false);

        Accept(projection, administrator, SafeMigrationObservedState.PrerequisiteMissing);
        Accept(projection, member, SafeMigrationObservedState.PrerequisiteMissing);

        var index = Operation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ux_roles_name",
                    "roles",
                    [new ExpectedIndexKeyDefinition(column: "name")],
                    unique: true)));

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var projected = projection.Project(index, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    /// <summary>Ordinary candidate-key evidence cannot certify the stronger NULLS NOT DISTINCT contract.</summary>
    [Fact]
    public void ModelManagedCandidateKeyDoesNotAuthorizeNullsNotDistinctIndex()
    {
        // Arrange
        var projection = ProjectionWithNewRoleTable();
        var administrator = RoleEnsure(
            1,
            "administrator",
            includeNameUniqueKey: true);

        Accept(projection, administrator, SafeMigrationObservedState.PrerequisiteMissing);

        var index = Operation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ux_roles_name",
                    "roles",
                    [new ExpectedIndexKeyDefinition(column: "name")],
                    unique: true,
                    nullsDistinct: false)));

        // Act
        var projected = projection.Project(
            index,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_data_state_unknown", projected.Code);
    }

    private static SafeMigrationPreflightProjection ProjectionWithAcceptedRole()
    {
        var projection = new SafeMigrationPreflightProjection();
        var ensure = Operation(
            new EnsureModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                ["id", "name"],
                ["int", "varchar(64)"],
                new object?[,] { { 1, "administrator" } },
                schema: null,
                uniqueKeys: null));

        Accept(projection, ensure, SafeMigrationObservedState.Missing);

        return projection;
    }

    private static SafeMigrationPreflightProjection ProjectionWithNewRoleTable()
    {
        var projection = new SafeMigrationPreflightProjection();
        var table = Operation(
            new EnsureTableIntent(
                RoleTable(),
                SafeMigrationTableMode.StrictDefinition));

        Accept(projection, table, SafeMigrationObservedState.Missing);

        return projection;
    }

    private static ExpectedTableDefinition RoleTable() => new(
        "roles",
        [
            new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
            new ExpectedColumnDefinition("name", typeof(string), isNullable: false, storeType: "varchar(64)"),
        ]);

    private static CreateTableOperation ProviderRoleTable()
    {
        var createTable = new CreateTableOperation { Name = "roles" };

        createTable.Columns.Add(
            new AddColumnOperation
            {
                Name = "id",
                Table = "roles",
                ClrType = typeof(int),
                ColumnType = "int",
            });

        createTable.Columns.Add(
            new AddColumnOperation
            {
                Name = "name",
                Table = "roles",
                ClrType = typeof(string),
                ColumnType = "varchar(64)",
            });

        return createTable;
    }

    private static SafeMigrationOperation RoleEnsure(
        string table = "roles"
    ) => Operation(
        new EnsureModelManagedDataIntent(
            table,
            ["id"],
            ["int"],
            ["id", "name"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, "administrator" } },
            schema: null,
            uniqueKeys: null));

    private static SafeMigrationOperation RoleEnsure(
        int id,
        string name,
        bool includeNameUniqueKey
    ) => Operation(
        new EnsureModelManagedDataIntent(
            "roles",
            ["id"],
            ["int"],
            ["id", "name"],
            ["int", "varchar(64)"],
            new object?[,] { { id, name } },
            schema: null,
            uniqueKeys: includeNameUniqueKey
                ? [new ExpectedModelManagedDataUniqueKeyDefinition(["name"])]
                : null));

    private static SafeMigrationOperation RoleEnsureWithDisplayName() => Operation(
        new EnsureModelManagedDataIntent(
            "roles",
            ["id"],
            ["int"],
            ["id", "display_name"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, "administrator" } },
            schema: null,
            uniqueKeys: null));

    private static SafeMigrationOperation RoleUpdate() => Operation(
        new UpdateModelManagedDataIntent(
            "roles",
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            ["name"],
            ["varchar(64)"],
            new object?[,] { { "administrator" } },
            new object?[,] { { "owner" } },
            schema: null,
            uniqueKeys: null));
}
