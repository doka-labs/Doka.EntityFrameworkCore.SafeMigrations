namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Only an untouched, provider-certified first repair can start intrinsic source preservation.</summary>
    /// <param name="boundary">The opt-out or earlier effect that must prevent a new source certificate.</param>
    /// <param name="ensure">Whether the accepted repair has no authored source definition.</param>
    [Theory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("opt-out", false)]
    [InlineData("opt-out", true)]
    [InlineData("unbound-proof", false)]
    [InlineData("unbound-proof", true)]
    [InlineData("data", false)]
    [InlineData("data", true)]
    [InlineData("opaque", false)]
    [InlineData("opaque", true)]
    [InlineData("layout", false)]
    [InlineData("layout", true)]
    [InlineData("recreated", false)]
    [InlineData("recreated", true)]
    public void FirstColumnRepairCannotReviveInvalidatedIntrinsicSources(string boundary, bool ensure)
    {
        // Arrange
        var provider = new KeyColumnSourceProbe();
        var projection = new SafeMigrationPreflightProjection(
            projectedColumnAnalyzer: boundary == "opt-out" ? null : provider);

        var source = KeyColumnSource("reference");
        KeyColumnObserveSources(projection, source, KeyColumnSource("question"));
        var operation = KeyColumnOperation(source, KeyColumnSource("reference", nullable: true), ensure);
        var captured = provider.Capture(operation.Intent, source);
        var live = boundary == "unbound-proof" ? KeyColumnLiveProof() : captured;
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, live.ObservedState,
            operation.Policy, live.RepairCapability);

        switch (boundary)
        {
            case "data":
                projection.ObserveProviderPostcondition(new InsertDataOperation
                {
                    Table = "other",
                    Columns = ["value"],
                    Values = new object?[,] { { 1 } },
                });
                break;
            case "opaque":
                projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1" });
                break;
            case "layout":
                projection.ObserveProviderPostcondition(new AlterTableOperation { Name = "items" });
                break;
            case "recreated":
                ObserveAccepted(projection, new DropTableIntent("items"), SafeMigrationObservedState.Matching);
                Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [source]),
                    SafeMigrationTableMode.StrictDefinition));
                break;
        }

        // Act
        // WHY: Observation consumes an accepted decision, but retaining source facts
        // still needs the independent provider certificate and an unbroken lifetime.
        projection.Observe(operation, live, live, decision);
        var columns = (ISafeMigrationProjectedColumnSource)projection;
        var changedPreserved = columns.PreservesLiveColumnValues("items", null, "reference");
        var siblingPreserved = columns.PreservesLiveColumnValues("items", null, "question");

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(boundary == "none", changedPreserved);
        Assert.Equal(boundary == "none", siblingPreserved);
    }
}
