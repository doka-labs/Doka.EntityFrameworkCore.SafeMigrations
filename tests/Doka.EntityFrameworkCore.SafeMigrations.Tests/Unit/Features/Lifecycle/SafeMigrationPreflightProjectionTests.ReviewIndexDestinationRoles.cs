namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>An immutable destination role survives source creation without weakening missing replay.</summary>
    /// <param name="sourceLifetime">The absent, sparse-created, complete-created or dropped source lifetime.</param>
    /// <param name="targetSupported">Whether the provider independently supports the authored destination role.</param>
    [Theory]
    [InlineData("absent", false)]
    [InlineData("absent", true)]
    [InlineData("sparse-created", false)]
    [InlineData("sparse-created", true)]
    [InlineData("complete-created", false)]
    [InlineData("complete-created", true)]
    [InlineData("dropped", false)]
    [InlineData("dropped", true)]
    public void IndexDestinationRoleCannotBeOverriddenBySourceCreation(
        string sourceLifetime,
        bool targetSupported
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        if (sourceLifetime == "complete-created")
        {
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("code")]),
                SafeMigrationTableMode.StrictDefinition));
        }

        if (sourceLifetime is "sparse-created" or "complete-created")
        {
            ObserveAccepted(projection, Index("ix_source"), SafeMigrationObservedState.Missing);
        }
        else if (sourceLifetime == "dropped")
        {
            ObserveAccepted(projection, new DropIndexIntent("ix_source", "items"), SafeMigrationObservedState.Matching);
        }

        var intent = new RenameIndexIntent("ix_source", "items", "ix_target");
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Missing)
            .WithRenameSourceSupported(false)
            .WithRenameTargetSupported(targetSupported);

        var sourceAbsent = sourceLifetime is "absent" or "dropped";

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(intent.Kind, analysis.ObservedState, operation.Policy);

        // Assert
        Assert.Equal(sourceAbsent ? SafeMigrationObservedState.Missing
            : targetSupported ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            analysis.ObservedState);
        Assert.Equal(sourceAbsent ? SafeMigrationAction.NoOp
            : targetSupported ? SafeMigrationAction.Apply : SafeMigrationAction.RejectDifferent, decision.Action);
    }

    /// <summary>An accepted destination tombstone cannot make a reserved role a valid rename target.</summary>
    [Fact]
    public void IndexDestinationDropCannotDischargeUnsupportedAuthoredRole()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(projection, new DropIndexIntent("ix_target", "items"), SafeMigrationObservedState.Matching);
        var operation = new SafeMigrationOperation(new RenameIndexIntent("ix_source", "items", "ix_target"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Different)
            .WithRenameSourceSupported(true)
            .WithRenameTargetSupported(false);

        // Act
        var analysis = projection.Project(operation, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind,
            analysis.ObservedState, operation.Policy).Action);
    }
}
