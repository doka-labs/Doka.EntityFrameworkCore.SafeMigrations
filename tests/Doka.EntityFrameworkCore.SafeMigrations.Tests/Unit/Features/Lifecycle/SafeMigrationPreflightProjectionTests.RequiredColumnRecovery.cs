namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Completing a staged required column cannot invent an empty-table certificate.</summary>
    /// <param name="resumesStaging">Whether the applicable add completes an owned existing staging column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredColumnRecoveryDoesNotProveTableEmpty(
        bool resumesStaging
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveColumnsOnExistingTable(projection, "items", Column("id"));
        ObserveAccepted(projection, new DropPrimaryKeyIntent("pk_items", "items"),
            SafeMigrationObservedState.Matching);

        var addition = new SafeMigrationOperation(
            new EnsureColumnIntent("items", Column("replacement_id")), SafeMigrationPolicy.ThrowIfDifferent);

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, "required-add")
        {
            RequiredColumnAddResumesOwnedStaging = resumesStaging,
        };

        var decision = SafeMigrationDecisionPlanner.Plan(addition.Intent.Kind, live.ObservedState,
            addition.Policy, live.RepairCapability);

        // Act
        projection.Observe(addition, live, live, decision);
        var analysis = projection.Project(ConstraintReplacementPrimaryKeyOperation(),
            Live(SafeMigrationObservedState.Different));

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        Assert.Equal(resumesStaging ? SafeMigrationObservedState.PrerequisiteMissing
            : SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>
    /// Diagnostic enrichment keeps the owned-staging certificate and cannot turn it into fresh absence.
    /// </summary>
    [Fact]
    public void RequiredColumnRecoveryCertificateSurvivesDiagnosticEnrichment()
    {
        // Arrange
        var source = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, postconditionSatisfied: false, "column_atomic_add_resume")
        {
            RequiredColumnAddResumesOwnedStaging = true,
        };

        // Act
        var enriched = source.WithDifferences([], requiresLiveDataProof: false);

        // Assert
        Assert.NotSame(source, enriched);
        Assert.True(enriched.RequiredColumnAddResumesOwnedStaging);
        Assert.Equal(SafeMigrationObservedState.Missing, enriched.ObservedState);
        Assert.False(enriched.PostconditionSatisfied);
    }
}
