namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedDataTests
{
    [Fact]
    public void AcceptedTableCreationProjectsFollowingModelManagedEnsureAsMissing()
    {
        var projection = new SafeMigrationPreflightProjection();
        var table = Operation(
            new EnsureTableIntent(
                RoleTable(),
                SafeMigrationTableMode.StrictDefinition));

        Accept(projection, table, SafeMigrationObservedState.Missing);

        var ensure = RoleEnsure();
        var projected = projection.Project(
            ensure,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        var decision = SafeMigrationDecisionPlanner.Plan(
            ensure.Intent.Kind,
            projected.ObservedState,
            ensure.Policy,
            projected.RepairCapability);

        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
    }

    [Fact]
    public void ProviderTableCreationProjectsFollowingModelManagedEnsureAsMissing()
    {
        var projection = new SafeMigrationPreflightProjection();
        var createTable = ProviderRoleTable();

        projection.ObserveProviderPostcondition(createTable);

        var projected = projection.Project(
            RoleEnsure(),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    [Fact]
    public void ProviderTableRenamePreservesNewTableEmptyRowProof()
    {
        var projection = new SafeMigrationPreflightProjection();

        projection.ObserveProviderPostcondition(ProviderRoleTable());
        projection.ObserveProviderPostcondition(new RenameTableOperation
        {
            Name = "roles",
            NewName = "application_roles",
        });

        var projected = projection.Project(
            RoleEnsure("application_roles"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    [Fact]
    public void AcceptedTableRenamePreservesNewTableEmptyRowProof()
    {
        var projection = ProjectionWithNewRoleTable();
        var rename = Operation(new RenameTableIntent("roles", newName: "application_roles"));

        Accept(projection, rename, SafeMigrationObservedState.Matching);

        var projected = projection.Project(
            RoleEnsure("application_roles"),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    [Fact]
    public void ProviderColumnRenamePreservesNewTableEmptyRowProof()
    {
        var projection = new SafeMigrationPreflightProjection();

        projection.ObserveProviderPostcondition(ProviderRoleTable());
        projection.ObserveProviderPostcondition(new RenameColumnOperation
        {
            Table = "roles",
            Name = "name",
            NewName = "display_name",
        });

        var ensure = RoleEnsureWithDisplayName();
        var projected = projection.Project(
            ensure,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    [Fact]
    public void AcceptedColumnRenamePreservesNewTableEmptyRowProof()
    {
        var projection = ProjectionWithNewRoleTable();
        var rename = Operation(new RenameColumnIntent("name", "roles", "display_name"));

        Accept(projection, rename, SafeMigrationObservedState.Matching);

        var ensure = RoleEnsureWithDisplayName();
        var projected = projection.Project(
            ensure,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    [Fact]
    public void ProviderMutationBeforeTableCreationDoesNotPoisonNewTableEmptyRowProof()
    {
        var projection = new SafeMigrationPreflightProjection();

        projection.ObserveProviderPostcondition(new InsertDataOperation
        {
            Table = "existing_table",
            Columns = ["id"],
            Values = new object[,] { { 1, }, },
        });
        projection.ObserveProviderPostcondition(ProviderRoleTable());

        var projected = projection.Project(
            RoleEnsure(),
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Missing, projected.ObservedState);
        Assert.Equal("projected_missing", projected.Code);
    }

    [Fact]
    public void ProviderTableDropInvalidatesNewTableEmptyRowProof()
    {
        var projection = new SafeMigrationPreflightProjection();
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        projection.ObserveProviderPostcondition(ProviderRoleTable());
        projection.ObserveProviderPostcondition(new DropTableOperation { Name = "roles", });

        var projected = projection.Project(RoleEnsure(), live);

        Assert.Same(live, projected);
    }

    [Fact]
    public void NewlyCreatedTableProjectsModelManagedOperationFamiliesConservatively()
    {
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

        var projectedUpdate = projection.Project(
            update,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        var projectedDelete = projection.Project(
            deletion,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projectedUpdate.ObservedState);
        Assert.Equal("projected_prerequisite_missing", projectedUpdate.Code);
        Assert.Equal(SafeMigrationObservedState.Missing, projectedDelete.ObservedState);
        Assert.True(projectedDelete.PostconditionSatisfied);
    }

    [Fact]
    public void ExistingTableNeverInfersMissingModelManagedRowsFromStructureAlone()
    {
        var projection = new SafeMigrationPreflightProjection();
        var table = Operation(
            new EnsureTableIntent(
                RoleTable(),
                SafeMigrationTableMode.ConvergenceContainer));

        Accept(projection, table, SafeMigrationObservedState.Matching);

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var projected = projection.Project(RoleEnsure(), live);

        Assert.Same(live, projected);
    }

    [Fact]
    public void IncompleteTableProjectionNeverInfersMissingModelManagedRows()
    {
        var projection = new SafeMigrationPreflightProjection();
        var table = Operation(
            new EnsureTableIntent(
                new ExpectedTableDefinition(
                    "roles",
                    [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int")]),
                SafeMigrationTableMode.StrictDefinition));

        Accept(projection, table, SafeMigrationObservedState.Missing);

        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);
        var projected = projection.Project(RoleEnsure(), live);

        Assert.Same(live, projected);
    }

    [Fact]
    public void ProviderDataMutationInvalidatesNewTableEmptyRowProof()
    {
        MigrationOperation[] dataOperations =
        [
            new InsertDataOperation
            {
                Table = "other_table",
                Columns = ["id"],
                Values = new object[,] { { 1, }, },
            },
            new UpdateDataOperation
            {
                Table = "other_table",
                KeyColumns = ["id"],
                KeyValues = new object[,] { { 1, }, },
                Columns = ["name"],
                Values = new object[,] { { "changed", }, },
            },
            new DeleteDataOperation
            {
                Table = "other_table",
                KeyColumns = ["id"],
                KeyValues = new object[,] { { 1, }, },
            },
        ];

        foreach (var dataOperation in dataOperations)
        {
            var projection = ProjectionWithNewRoleTable();
            var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

            projection.ObserveProviderPostcondition(dataOperation);

            var projected = projection.Project(RoleEnsure(), live);

            Assert.Same(live, projected);
        }
    }

    [Fact]
    public void OpaqueProviderOperationInvalidatesNewTableEmptyRowProof()
    {
        var projection = ProjectionWithNewRoleTable();
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });

        var projected = projection.Project(RoleEnsure(), live);

        Assert.Same(live, projected);
    }

    [Fact]
    public void AcceptedModelManagedRowsPreserveNewTableKnowledgeForLaterKeys()
    {
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

        var projectedAdministrator = projection.Project(
            administrator,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        var projectedMember = projection.Project(
            member,
            Live(SafeMigrationObservedState.PrerequisiteMissing));

        Assert.Equal(SafeMigrationObservedState.Matching, projectedAdministrator.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, projectedMember.ObservedState);
    }

    [Fact]
    public void IncompleteProjectedRowNeverBecomesMissingFromNewTableProof()
    {
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
        var projected = projection.Project(overlapping, live);

        Assert.Same(live, projected);
    }

    [Fact]
    public void OpaqueProviderMutationInvalidatesRowsAndDependencyHandoffTogether()
    {
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

        projection.Observe(childDelete, childAnalysis, childDecision);
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

        var projected = projection.Project(
            parentDelete,
            EvidenceAnalysis(
                SafeMigrationObservedState.DataBlocked,
                [SafeMigrationModelManagedRowState.Source],
                [1]));

        Assert.Equal(SafeMigrationObservedState.DataBlocked, projected.ObservedState);
        Assert.Equal("test_live", projected.Code);
    }

    [Fact]
    public void StructuralIdentityChangesInvalidateModelManagedRowProjection()
    {
        var structuralOperations = new MigrationOperation[]
        {
            new DropColumnOperation { Table = "roles", Name = "name", },
            new RenameColumnOperation { Table = "roles", Name = "name", NewName = "display_name", },
            new DropTableOperation { Name = "roles", },
            new RenameTableOperation { Name = "roles", NewName = "renamed_roles", },
        };

        foreach (var structuralOperation in structuralOperations)
        {
            var projection = ProjectionWithAcceptedRole();

            projection.ObserveProviderPostcondition(structuralOperation);

            var projected = projection.Project(RoleUpdate(), Live(SafeMigrationObservedState.PrerequisiteMissing));

            Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
            Assert.Equal("test_live", projected.Code);
        }
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
        var createTable = new CreateTableOperation { Name = "roles", };

        createTable.Columns.Add(new AddColumnOperation
        {
            Name = "id",
            Table = "roles",
            ClrType = typeof(int),
            ColumnType = "int",
        });
        createTable.Columns.Add(new AddColumnOperation
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
