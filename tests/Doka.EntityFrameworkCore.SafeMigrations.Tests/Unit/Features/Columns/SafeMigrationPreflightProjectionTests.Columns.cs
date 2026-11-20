namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>
    /// Approves an alteration only against the accepted old shape, not a contradictory historical native
    /// classification.
    /// </summary>
    [Fact]
    public void ProjectionProvesRepairOnlyAgainstTheProjectedOldDefinition()
    {
        // Arrange
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

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.Safe, analysis.RepairCapability);
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
    }

    /// <summary>
    /// Observes a proven repair and replays its complete target as matching with no residual operational impact.
    /// </summary>
    [Fact]
    public void AcceptedEnsureColumnRepairProjectsTheCompleteTargetDefinition()
    {
        // Arrange
        var projection = ProjectionWithExistingVarcharLength(10);
        var target = VarcharColumn(200);
        var intent = new EnsureColumnIntent("items", target);
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var live = RepairableVarcharAnalysis();

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        projection.Observe(operation, analysis, analysis, decision);
        var replay = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, replay.ObservedState);
        Assert.True(replay.PostconditionSatisfied);
        Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, replay.OperationalImpact);
    }

    /// <summary>
    /// Replays an accepted target without reusing the earlier narrowing proof after a later provider data mutation.
    /// </summary>
    [Fact]
    public void ProjectedNarrowingReplayDoesNotRetainTheConsumedLiveDataProof()
    {
        // Arrange
        var projection = ProjectionWithExistingVarcharLength(200);
        var intent = new EnsureColumnIntent("items", VarcharColumn(10));
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var live = RepairableVarcharNarrowingAnalysis();

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        projection.Observe(operation, analysis, analysis, decision);

        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "other_items",
                Columns = ["value"],
                Values = new object?[,] { { "unrelated" } },
            });

        var replay = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, replay.ObservedState);
        Assert.True(replay.PostconditionSatisfied);
        Assert.Equal(SafeMigrationOperationalImpact.NotApplicable, replay.OperationalImpact);
    }

    /// <summary>
    /// Keeps the old accepted shape and rejected replay when strict policy refuses a provider-proven repair.
    /// </summary>
    [Fact]
    public void RejectedEnsureColumnRepairDoesNotChangeTheProjectedDefinition()
    {
        // Arrange
        var projection = ProjectionWithExistingVarcharLength(10);
        var target = VarcharColumn(200);
        var intent = new EnsureColumnIntent("items", target);
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var live = RepairableVarcharAnalysis();

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        projection.Observe(operation, analysis, analysis, decision);
        var replay = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Different, replay.ObservedState);
        Assert.False(replay.PostconditionSatisfied);
    }

    /// <summary>
    /// Does not transfer a live widening approval to a different column shape accepted earlier in the same stream.
    /// </summary>
    [Fact]
    public void ProjectedDifferentColumnDoesNotReuseHistoricalProviderRepairProof()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var initialTable = new ExpectedTableDefinition("items", [VarcharColumn(10)]);
        var intent = new EnsureColumnIntent("items", VarcharColumn(200));
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);

        Apply(projection, new EnsureTableIntent(initialTable, SafeMigrationTableMode.StrictDefinition));

        // Act
        var analysis = projection.Project(operation, RepairableVarcharAnalysis());
        var decision = SafeMigrationDecisionPlanner.Plan(
            intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
        Assert.Equal(SafeMigrationAction.RejectDifferent, decision.Action);
        Assert.Empty(analysis.Differences);
    }

    /// <summary>
    /// Rejects a historical length-narrowing proof after an ordinary INSERT changes the table's row domain.
    /// </summary>
    [Fact]
    public void ProviderDataMutationInvalidatesEarlierNarrowingProof()
    {
        // Arrange
        var projection = ProjectionWithExistingVarcharLength(200);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("items", VarcharColumn(10)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "items",
                Columns = ["value"],
                Values = new object?[,] { { "new value" } },
            });

        var analysis = projection.Project(operation, RepairableVarcharNarrowingAnalysis());

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    /// <summary>
    /// Invalidates both affected and other table narrowing proofs across the conservative managed-write boundary.
    /// </summary>
    [Fact]
    public void ModelManagedMutationInvalidatesEveryTableNarrowingProof()
    {
        // Arrange
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

        // Act
        var itemAnalysis = projection.Project(itemOperation, RepairableVarcharNarrowingAnalysis());
        var auditAnalysis = projection.Project(auditOperation, RepairableVarcharNarrowingAnalysis());

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, itemAnalysis.ObservedState);
        Assert.Equal(SafeMigrationRepairCapability.None, itemAnalysis.RepairCapability);
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

        projection.Observe(operation, analysis, analysis, decision);
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
