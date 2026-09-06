namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    [Fact]
    public void ProjectionProvesRepairOnlyAgainstTheProjectedOldDefinition()
    {
        var projection = new SafeMigrationPreflightProjection();
        var oldColumn = new ExpectedColumnDefinition(
            "value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40);

        var target = new ExpectedColumnDefinition(
            "value",
            typeof(string),
            isNullable: true,
            storeType: "varchar(40)",
            maxLength: 40,
            comment: "canonical");

        Apply(
            projection,
            new EnsureTableIntent(
                new ExpectedTableDefinition("items", [oldColumn]),
                SafeMigrationTableMode.StrictDefinition));

        var intent = new AlterColumnIntent("items", target, oldColumn);
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.Safe, analysis.RepairCapability);
        Assert.Equal(
            SafeMigrationAction.Repair,
            SafeMigrationDecisionPlanner.Plan(
                    intent.Kind,
                    analysis.ObservedState,
                    operation.Policy,
                    analysis.RepairCapability)
                .Action);
    }

    [Fact]
    public void AcceptedEnsureColumnRepairProjectsTheCompleteTargetDefinition()
    {
        var projection = ProjectionWithExistingVarcharLength(10);
        var target = VarcharColumn(200);
        var intent = new EnsureColumnIntent("items", target);
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var live = RepairableVarcharAnalysis();

        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        projection.Observe(operation, analysis, decision);
        var replay = projection.Project(operation, live);

        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, replay.ObservedState);
        Assert.True(replay.PostconditionSatisfied);
        Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, replay.OperationalImpact);
    }

    [Fact]
    public void ProjectedNarrowingReplayDoesNotRetainTheConsumedLiveDataProof()
    {
        var projection = ProjectionWithExistingVarcharLength(200);
        var intent = new EnsureColumnIntent("items", VarcharColumn(10));
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var live = RepairableVarcharNarrowingAnalysis();

        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        projection.Observe(operation, analysis, decision);

        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "other_items",
                Columns = ["value"],
                Values = new object?[,] { { "unrelated" } },
            });

        var replay = projection.Project(operation, live);

        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, replay.ObservedState);
        Assert.True(replay.PostconditionSatisfied);
        Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, replay.OperationalImpact);
    }

    [Fact]
    public void RejectedEnsureColumnRepairDoesNotChangeTheProjectedDefinition()
    {
        var projection = ProjectionWithExistingVarcharLength(10);
        var target = VarcharColumn(200);
        var intent = new EnsureColumnIntent("items", target);
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var live = RepairableVarcharAnalysis();

        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        projection.Observe(operation, analysis, decision);
        var replay = projection.Project(operation, live);

        Assert.Equal(SafeMigrationAction.RejectDifferent, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Different, replay.ObservedState);
        Assert.False(replay.PostconditionSatisfied);
    }

    [Fact]
    public void ProjectedDifferentColumnDoesNotReuseHistoricalProviderRepairProof()
    {
        var projection = new SafeMigrationPreflightProjection();
        var initialTable = new ExpectedTableDefinition("items", [VarcharColumn(10)]);
        var intent = new EnsureColumnIntent("items", VarcharColumn(200));
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);

        Apply(projection, new EnsureTableIntent(initialTable, SafeMigrationTableMode.StrictDefinition));

        var analysis = projection.Project(operation, RepairableVarcharAnalysis());
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
        Assert.Equal(SafeMigrationAction.RejectDifferent, decision.Action);
        Assert.Empty(analysis.Differences);
    }

    [Fact]
    public void ProviderDataMutationInvalidatesEarlierNarrowingProof()
    {
        var projection = ProjectionWithExistingVarcharLength(200);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("items", VarcharColumn(10)),
            SafeMigrationPolicy.RepairIfSafe);

        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "items",
                Columns = ["value"],
                Values = new object?[,] { { "new value" } },
            });

        var analysis = projection.Project(operation, RepairableVarcharNarrowingAnalysis());

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    [Fact]
    public void ModelManagedMutationInvalidatesOnlyItsTableNarrowingProof()
    {
        var projection = ProjectionWithExistingVarcharLength(200);
        var auditTable = new ExpectedTableDefinition("audit", [VarcharColumn(200)]);
        var auditData = new EnsureModelManagedDataIntent(
            "audit",
            ["value"],
            ["varchar(200)"],
            ["value"],
            ["varchar(200)"],
            new object?[,] { { "created" } },
            schema: null,
            uniqueKeys: null);

        ObserveExistingConvergenceTable(projection, auditTable);
        Apply(projection, auditData);

        var itemOperation = new SafeMigrationOperation(
            new EnsureColumnIntent("items", VarcharColumn(10)),
            SafeMigrationPolicy.RepairIfSafe);

        var auditOperation = new SafeMigrationOperation(
            new EnsureColumnIntent("audit", VarcharColumn(10)),
            SafeMigrationPolicy.RepairIfSafe);

        var itemAnalysis = projection.Project(itemOperation, RepairableVarcharNarrowingAnalysis());
        var auditAnalysis = projection.Project(auditOperation, RepairableVarcharNarrowingAnalysis());

        Assert.Equal(SafeMigrationObservedState.Different, itemAnalysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.Safe, itemAnalysis.RepairCapability);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, auditAnalysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", auditAnalysis.Code);
    }

    private static SafeMigrationPreflightProjection ProjectionWithExistingVarcharLength(
        int length
    )
    {
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition("items", [VarcharColumn(length)]);

        ObserveExistingConvergenceTable(projection, table);

        return projection;
    }

    private static void ObserveExistingConvergenceTable(
        SafeMigrationPreflightProjection projection,
        ExpectedTableDefinition table
    )
    {
        var intent = new EnsureTableIntent(table, SafeMigrationTableMode.ConvergenceContainer);
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching));
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);

        projection.Observe(operation, analysis, decision);
    }

    private static ExpectedColumnDefinition VarcharColumn(
        int length
    ) => new(
        "value",
        typeof(string),
        isNullable: true,
        storeType: $"varchar({length})",
        maxLength: length);

    private static SafeMigrationProviderAnalysis RepairableVarcharAnalysis() => new(
        SafeMigrationObservedState.Different,
        SafeMigrationRepairCapability.Safe,
        postconditionSatisfied: false,
        "varchar_widening",
        SafeMigrationOperationalImpact.TableRewritePossible,
        [new SafeMigrationFacetDifference("column_max_length", "200", "10")]);

    private static SafeMigrationProviderAnalysis RepairableVarcharNarrowingAnalysis() => new(
        SafeMigrationObservedState.Different,
        SafeMigrationRepairCapability.Safe,
        postconditionSatisfied: false,
        "varchar_length_transition",
        SafeMigrationOperationalImpact.TableRewritePossible,
        [new SafeMigrationFacetDifference("column_max_length", "10", "200")])
    {
        RequiresLiveDataProof = true,
    };
}
