namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing, SafeMigrationAction.Apply)]
    [InlineData(SafeMigrationObservedState.Unsupported, SafeMigrationAction.RejectUnsupported)]
    [InlineData(SafeMigrationObservedState.DataBlocked, SafeMigrationAction.RejectDataBlocked)]
    [InlineData(SafeMigrationObservedState.Different, SafeMigrationAction.RejectDifferent)]
    [InlineData(SafeMigrationObservedState.PrerequisiteMissing, SafeMigrationAction.RejectPrerequisiteMissing)]
    public void StandalonePrimaryKeyReplacementUsesExactProviderProofAndDecision(
        SafeMigrationObservedState providerState,
        SafeMigrationAction expectedAction
    )
    {
        // Arrange
        var providerResult = new SafeMigrationProviderAnalysis(
            providerState,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "provider_primary_key_replacement_proof");

        var validator = new PrimaryKeyReplacementProbe(providerResult);
        var projection = ProjectionAfterStandalonePrimaryKeyDrop(validator);
        var operation = PrimaryKeyReplacementOperation();
        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Same(providerResult, analysis);
        Assert.Equal(expectedAction, decision.Action);
        Assert.Equal(1, validator.ValidationCount);
        Assert.Same(operation.Intent, validator.LastIntent);
        Assert.Same(projection, validator.LastColumnSource);
        Assert.Same(live, validator.LastLiveAnalysis);

        var projected = Assert.IsType<SafeMigrationProviderAnalysis>(validator.LastProjectedAnalysis);

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
        Assert.Equal("projected_primary_key_replacement_unproven", projected.Code);
        Assert.Equal(SafeMigrationRepairCapability.None, projected.RepairCapability);
        Assert.False(projected.PostconditionSatisfied);
        Assert.False(projected.IsOpaqueProjectionUnknown);
        Assert.True(((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "items",
            schema: null,
            out var table));

        Assert.True(table.PrimaryKeyWasDropped);
        Assert.False(table.IsNewlyCreated);
    }

    [Fact]
    public void StandalonePrimaryKeyReplacementWithoutProviderProofRemainsStructureUnknown()
    {
        // Arrange
        var projection = ProjectionAfterStandalonePrimaryKeyDrop();
        var operation = PrimaryKeyReplacementOperation();
        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
        Assert.True(analysis.IsOpaqueProjectionUnknown);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, decision.Action);
    }

    [Fact]
    public void RejectedPrimaryKeyDropDoesNotAuthorizeProviderReplacementProof()
    {
        // Arrange
        var validator = new PrimaryKeyReplacementProbe(Live(SafeMigrationObservedState.Missing));
        var projection = new SafeMigrationPreflightProjection(projectedKeyAnalyzer: validator);
        var drop = new SafeMigrationOperation(
            new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var rejected = Live(SafeMigrationObservedState.Different);
        var dropDecision = SafeMigrationDecisionPlanner.Plan(
            drop.Intent.Kind,
            rejected.ObservedState,
            drop.Policy,
            rejected.RepairCapability);

        projection.Observe(drop, rejected, rejected, dropDecision);

        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = projection.Project(PrimaryKeyReplacementOperation(), live);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationAction.RejectDifferent, dropDecision.Action);
        Assert.Equal(0, validator.ValidationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpaqueOperationAfterPrimaryKeyDropCannotConsultReplacementProof(bool rawSql)
    {
        // Arrange
        var validator = new PrimaryKeyReplacementProbe(Live(SafeMigrationObservedState.Missing));
        var projection = ProjectionAfterStandalonePrimaryKeyDrop(validator);

        projection.ObserveProviderPostcondition(rawSql
            ? new SqlOperation { Sql = "SELECT 1;", }
            : new UnknownPrimaryKeyReplacementOperation());

        // Act
        var analysis = projection.Project(
            PrimaryKeyReplacementOperation(),
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_structure_state_unknown", analysis.Code);
        Assert.True(analysis.IsOpaqueProjectionUnknown);
        Assert.Equal(0, validator.ValidationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvariantPrimaryKeyRejectionPrecedesReplacementProof(bool opaqueSql)
    {
        // Arrange
        var validator = new PrimaryKeyReplacementProbe(Live(SafeMigrationObservedState.Missing));
        var projection = ProjectionAfterStandalonePrimaryKeyDrop(validator);
        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "primary_key_invariant_unsupported")
        {
            IsInvariantUnsupported = true,
        };

        if (opaqueSql)
        {
            projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;", });
        }

        // Act
        var analysis = projection.Project(PrimaryKeyReplacementOperation(), live);

        // Assert
        Assert.Same(live, analysis);
        Assert.True(analysis.IsInvariantUnsupported);
        Assert.Equal(0, validator.ValidationCount);
    }

    [Fact]
    public void DataMutationAfterPrimaryKeyDropReplacesMarkerWithDataUncertainty()
    {
        // Arrange
        var validator = new PrimaryKeyReplacementProbe(Live(SafeMigrationObservedState.Missing));
        var projection = ProjectionAfterStandalonePrimaryKeyDrop(validator);
        var operation = PrimaryKeyReplacementOperation();

        projection.ObserveProviderPostcondition(new InsertDataOperation
        {
            Table = "items",
            Columns = ["id", "external_id"],
            Values = new object[,] { { 1, 2 } },
        });

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Different));
        var decision = SafeMigrationDecisionPlanner.Plan(
            operation.Intent.Kind,
            analysis.ObservedState,
            operation.Policy,
            analysis.RepairCapability);

        // Assert
        Assert.Equal(1, validator.ValidationCount);
        Assert.Same(validator.LastProjectedAnalysis, analysis);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, decision.Action);
        Assert.True(((ISafeMigrationProjectedTableSource)projection).TryGetProjectedTableState(
            "items",
            schema: null,
            out var table));

        Assert.True(table.PrimaryKeyWasDropped);
        Assert.True(table.HasDataMutation);
    }

    private static SafeMigrationPreflightProjection ProjectionAfterStandalonePrimaryKeyDrop(
        ISafeMigrationProjectedKeyAnalyzer? validator = null
    )
    {
        var projection = new SafeMigrationPreflightProjection(projectedKeyAnalyzer: validator);

        ObserveColumnsOnExistingTable(projection, "items", Column("id"), Column("external_id"));
        ObserveAccepted(
            projection,
            new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);

        return projection;
    }

    private static SafeMigrationOperation PrimaryKeyReplacementOperation() => new(
        new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_items_replacement", "items", ["external_id"])),
        SafeMigrationPolicy.ThrowIfDifferent);

    private sealed class UnknownPrimaryKeyReplacementOperation : MigrationOperation
    {
    }

    private sealed class PrimaryKeyReplacementProbe(
        SafeMigrationProviderAnalysis replacement
    ) : ISafeMigrationProjectedKeyAnalyzer
    {
        public bool SharesUniqueConstraintAndIndexIdentity => false;

        public int ValidationCount { get; private set; }

        public EnsurePrimaryKeyIntent? LastIntent { get; private set; }

        public ISafeMigrationProjectedColumnSource? LastColumnSource { get; private set; }

        public SafeMigrationProviderAnalysis? LastLiveAnalysis { get; private set; }

        public SafeMigrationProviderAnalysis? LastProjectedAnalysis { get; private set; }

        public SafeMigrationProviderAnalysis ValidateProjectedPrimaryKey(
            EnsurePrimaryKeyIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        )
        {
            ValidationCount++;
            LastIntent = intent;
            LastColumnSource = columns;
            LastLiveAnalysis = liveAnalysis;
            LastProjectedAnalysis = projectedAnalysis;

            return projectedAnalysis.Code == "projected_primary_key_replacement_unproven"
                ? replacement
                : projectedAnalysis;
        }

        public SafeMigrationProviderAnalysis ValidateProjectedIndex(
            EnsureIndexIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => projectedAnalysis;

        public SafeMigrationProviderAnalysis ValidateProjectedUniqueConstraint(
            EnsureUniqueConstraintIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => projectedAnalysis;
    }
}
