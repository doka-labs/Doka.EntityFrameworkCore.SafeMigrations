namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies prepared projection behavior independently of measured allocation performance.</summary>
public sealed class SafeMigrationProjectionAllocationTests
{
    /// <summary>Accepted column streams retain exact evidence across independent invocations.</summary>
    /// <param name="count">The prepared stream size, including the original thousand-column workload.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1_000)]
    public void AcceptedColumnsProjectToMatchingDefinitions(
        int count
    )
    {
        // Arrange
        var workload = new PreparedColumnProjectionWorkload(count);

        // Act
        var matching = workload.ProjectAcceptedColumns();
        var replay = workload.ProjectAcceptedColumns();

        // Assert
        Assert.Equal(count, matching);
        Assert.Equal(count, replay);
    }

    /// <summary>Rejected convergence cannot invent a matching physical column definition.</summary>
    [Fact]
    public void RejectedColumnDoesNotBecomeAProjectedMatch()
    {
        // Arrange
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("items", new ExpectedColumnDefinition("value", typeof(int), true, "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None,
            false,
            "live_different");

        var decision = SafeMigrationDecisionPlanner.Plan(
            SafeMigrationOperationKind.EnsureColumn,
            live.ObservedState,
            operation.Policy);

        var projection = new SafeMigrationPreflightProjection();

        // Act
        projection.Observe(operation, live, live, decision);
        var observed = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Different, observed.ObservedState);
    }
}
