namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Only current, provider-qualified nullable transitions receive a physical-layout certificate.</summary>
    /// <param name="boundary">The independent source, lifetime or provider boundary exercised by this case.</param>
    [Theory]
    [InlineData("relax")]
    [InlineData("tighten")]
    [InlineData("no-source")]
    [InlineData("provider-declines")]
    [InlineData("data")]
    [InlineData("opaque")]
    [InlineData("column-drop")]
    [InlineData("table-recreate")]
    [InlineData("incoming-fk")]
    [InlineData("mutating-repair")]
    [InlineData("data-blocked")]
    public void EnsureAfterForeignKeyDropsCertifiesOnlyCurrentIntrinsicLayout(string boundary)
    {
        // Arrange
        var provider = new EnsureNullableLayoutProbe { CertifiesLayout = boundary != "provider-declines" };
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = new ExpectedColumnDefinition("reference", typeof(int), boundary == "tighten", "int");
        var target = new ExpectedColumnDefinition("reference", typeof(int), boundary != "tighten", "int");
        if (boundary != "no-source")
        {
            ObserveAccepted(projection, new EnsureColumnIntent("items", source), SafeMigrationObservedState.Matching);
        }

        ObserveAccepted(projection, new DropForeignKeyIntent("fk_old", "items"), SafeMigrationObservedState.Matching);
        switch (boundary)
        {
            case "data":
                projection.ObserveProviderPostcondition(new InsertDataOperation
                {
                    Table = "other",
                    Columns = ["id"],
                    Values = new object?[,] { { 1 } },
                });
                break;
            case "opaque":
                projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1" });
                break;
            case "column-drop":
                ObserveAccepted(projection, new DropColumnIntent("reference", "items"),
                    SafeMigrationObservedState.Matching);
                break;
            case "table-recreate":
                ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
                Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [source]),
                    SafeMigrationTableMode.StrictDefinition));
                break;
            case "incoming-fk":
                Apply(projection, new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                    "fk_incoming", "other", ["reference"], "items", ["reference"])));
                break;
        }

        var live = new SafeMigrationProviderAnalysis(
            boundary == "data-blocked" ? SafeMigrationObservedState.DataBlocked : SafeMigrationObservedState.Different,
            boundary == "data-blocked" ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            false, "captured_nullable_transition")
        {
            RepairMutatesData = boundary == "mutating-repair",
        };

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var result = projection.Project(operation, live);

        // Assert
        Assert.Equal(boundary is "relax" or "tighten", result.RepairPreservesPhysicalKeys);
        Assert.Equal(boundary is "relax" or "tighten", result.RepairPreservesIntrinsicColumnLayout);
        Assert.False(live.RepairPreservesPhysicalKeys);
        Assert.False(live.RepairPreservesIntrinsicColumnLayout);
        Assert.False(result.RepairPreservesLiveValueDomain);
        Assert.Equal(boundary is "relax" or "tighten" or "provider-declines", provider.LayoutQueries == 1);
    }

    /// <summary>Observation consumes the projected certificate, retaining keys and later sibling proof only when certified.</summary>
    /// <param name="certified">Whether the exact native layout-preservation callback accepts the source.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureNullableRepairObservationRetainsOnlyCertifiedKeysAndSiblingProof(bool certified)
    {
        // Arrange
        var provider = new EnsureNullableLayoutProbe { CertifiesLayout = certified };
        var projection = new SafeMigrationPreflightProjection(projectedColumnAnalyzer: provider);
        var source = new ExpectedColumnDefinition("reference", typeof(int), false, "int");
        var target = new ExpectedColumnDefinition("reference", typeof(int), true, "int");
        var siblingSource = new ExpectedColumnDefinition("sibling", typeof(int), false, "int");
        var siblingTarget = new ExpectedColumnDefinition("sibling", typeof(int), true, "int");
        var index = new EnsureIndexIntent(new ExpectedIndexDefinition("ix_reference", "items",
            [new ExpectedIndexKeyDefinition("reference")]));

        ObserveAccepted(projection, new EnsureColumnIntent("items", source), SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new EnsureColumnIntent("items", siblingSource), SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, index, SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new DropForeignKeyIntent("fk_old", "items"), SafeMigrationObservedState.Matching);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.RepairIfSafe);
        var sibling = new SafeMigrationOperation(new EnsureColumnIntent("items", siblingTarget),
            SafeMigrationPolicy.RepairIfSafe);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "captured_nullable_transition");
        var repair = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, repair.ObservedState,
            operation.Policy, repair.RepairCapability);
        var indexOperation = new SafeMigrationOperation(index, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        projection.Observe(operation, live, repair, decision);
        var keyResult = projection.Project(indexOperation, Live(SafeMigrationObservedState.PrerequisiteMissing));
        var siblingResult = projection.Project(sibling, live);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.False(live.RepairPreservesPhysicalKeys);
        Assert.Equal(certified, keyResult.ObservedState == SafeMigrationObservedState.Matching);
        Assert.Equal(certified, siblingResult.RepairPreservesIntrinsicColumnLayout);
        Assert.Equal(certified ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            siblingResult.RepairCapability);
    }

    /// <summary>Distinct projected Missing receipts cannot erase original owned-staging provenance.</summary>
    /// <param name="resumesStaging">Whether the actual native add completes an interrupted owned stage.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureObservationKeepsOriginalStagingReceiptSeparateFromProjectedMissing(bool resumesStaging)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("id")),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);
        var addition = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("replacement_id")),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "captured_required_add")
        {
            RequiredColumnAddResumesOwnedStaging = resumesStaging,
        };

        var projected = Live(SafeMigrationObservedState.Missing);
        var decision = SafeMigrationDecisionPlanner.Plan(addition.Intent.Kind, projected.ObservedState,
            addition.Policy, projected.RepairCapability);

        // Act
        projection.Observe(addition, live, projected, decision);
        var keyResult = projection.Project(ConstraintReplacementPrimaryKeyOperation(),
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.NotSame(live, projected);
        Assert.False(projected.RequiredColumnAddResumesOwnedStaging);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        Assert.Equal(resumesStaging ? SafeMigrationObservedState.PrerequisiteMissing
            : SafeMigrationObservedState.Missing, keyResult.ObservedState);
    }

    /// <summary>Models an opt-in source-bound native proof without granting rows or generic widening.</summary>
    private sealed class EnsureNullableLayoutProbe : ISafeMigrationProjectedColumnAnalyzer
    {
        /// <summary>Gets whether the provider independently certifies this exact nullable-only layout.</summary>
        public bool CertifiesLayout { get; init; }

        /// <summary>Gets the number of source-qualified certificate requests.</summary>
        public int LayoutQueries { get; private set; }

        /// <inheritdoc />
        public bool CanReuseLiveColumnProofAfterForeignKeyDrops(
            EnsureColumnIntent intent,
            SafeMigrationProviderAnalysis liveAnalysis
        ) => true;

        /// <inheritdoc />
        public bool PreservesSiblingColumnProofsAfterForeignKeyDrops(
            EnsureColumnIntent intent,
            ExpectedColumnDefinition source,
            SafeMigrationProviderAnalysis liveAnalysis
        )
        {
            LayoutQueries++;

            return CertifiesLayout && source.StoreType == intent.Definition.StoreType
                && source.IsNullable != intent.Definition.IsNullable;
        }

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedAlterColumn(
            AlterColumnIntent intent,
            ExpectedColumnDefinition source,
            SafeMigrationProjectedAlterColumnContext context,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => projectedAnalysis;
    }
}
