namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>A standalone physical index ensure is visible to a later drop without a complete table image.</summary>
    [Fact]
    public void StandaloneIndexEnsureProjectsDropAsPresent()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(projection, Index("ix_items"), SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(projection, new DropIndexIntent("ix_items", "items"),
            SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
    }

    /// <summary>Repeated drops consume the physical tombstone rather than stale immutable presence.</summary>
    [Fact]
    public void StandaloneIndexDropProjectsRepeatedDropAsMissing()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(projection, new DropIndexIntent("ix_items", "items"), SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(projection, new DropIndexIntent("ix_items", "items"),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>A logical semantic alias never becomes another physical index drop target.</summary>
    /// <param name="name">The physical or logical name being removed.</param>
    /// <param name="expectedState">The expected exact physical presence.</param>
    [Theory]
    [InlineData("ix_physical", SafeMigrationObservedState.Matching)]
    [InlineData("ix_alias", SafeMigrationObservedState.Missing)]
    public void StandaloneIndexDropDistinguishesPhysicalNamesFromSemanticAliases(
        string name,
        SafeMigrationObservedState expectedState
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(projection, Index("ix_alias"), SafeMigrationObservedState.Matching,
            matchedObjectName: "ix_physical");

        // Act
        var analysis = Project(projection, new DropIndexIntent(name, "items"), SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(expectedState, analysis.ObservedState);
    }

    /// <summary>Index rename retains target conflicts and refuses to invent missing destination facts.</summary>
    /// <param name="liveState">The immutable pre-batch source classification.</param>
    /// <param name="targetExists">The separately captured target-presence evidence, when available.</param>
    /// <param name="expectedState">The expected ordered rename classification.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Different, null, SafeMigrationObservedState.Different)]
    [InlineData(SafeMigrationObservedState.Matching, null, SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationObservedState.Missing, null, SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(SafeMigrationObservedState.Missing, false, SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationObservedState.Missing, true, SafeMigrationObservedState.Different)]
    public void SparseIndexRenameRequiresIndependentDestinationEvidence(
        SafeMigrationObservedState liveState,
        bool? targetExists,
        SafeMigrationObservedState expectedState
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(projection, Index("ix_source"), SafeMigrationObservedState.Missing);

        var live = new SafeMigrationProviderAnalysis(liveState, SafeMigrationRepairCapability.None, false, "test_live")
        {
            RenameTargetExists = targetExists,
        };

        // Act
        var analysis = projection.Project(new SafeMigrationOperation(
            new RenameIndexIntent("ix_source", "items", "ix_target"), SafeMigrationPolicy.ThrowIfDifferent), live);

        // Assert
        Assert.Equal(expectedState, analysis.ObservedState);
    }

    /// <summary>The missing source wins when accepted drops removed both rename endpoints.</summary>
    /// <param name="indexRename">Whether the request renames an index instead of a column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseRenameWithBothEndpointsDroppedRemainsMissing(bool indexRename)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        SafeMigrationIntent sourceDrop = indexRename
            ? new DropIndexIntent("source", "items")
            : new DropColumnIntent("source", "items");
        SafeMigrationIntent targetDrop = indexRename
            ? new DropIndexIntent("target", "items")
            : new DropColumnIntent("target", "items");
        SafeMigrationIntent rename = indexRename
            ? new RenameIndexIntent("source", "items", "target")
            : new RenameColumnIntent("source", "items", "target");

        ObserveAccepted(projection, sourceDrop, SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, targetDrop, SafeMigrationObservedState.Matching);

        // Act
        var analysis = Project(projection, rename, SafeMigrationObservedState.Different);
        var decision = SafeMigrationDecisionPlanner.Plan(rename.Kind, analysis.ObservedState,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
    }

    /// <summary>Accepted exact index renames retain both endpoint lifetimes for subsequent physical drops.</summary>
    [Fact]
    public void SparseIndexRenameProjectsSourceMissingAndDestinationPresent()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(projection, Index("ix_source"), SafeMigrationObservedState.Missing);
        ObserveAccepted(projection, new RenameIndexIntent("ix_source", "items", "ix_target"),
            SafeMigrationObservedState.Matching);

        // Act
        var source = Project(projection, new DropIndexIntent("ix_source", "items"),
            SafeMigrationObservedState.Matching);

        var target = Project(projection, new DropIndexIntent("ix_target", "items"), SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, source.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, target.ObservedState);
    }

    /// <summary>Physical index drops invalidate semantic aliases, not a recreated physical lifetime.</summary>
    [Fact]
    public void SparseIndexDropAndReensureCreatesNewPhysicalLifetime()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(projection, Index("ix_alias"), SafeMigrationObservedState.Matching, "ix_physical");
        ObserveAccepted(projection, new DropIndexIntent("ix_physical", "items"), SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, Index("ix_physical"), SafeMigrationObservedState.Matching, "ix_physical");

        // Act
        var physical = Project(projection, new DropIndexIntent("ix_physical", "items"),
            SafeMigrationObservedState.Missing);
        var alias = Project(projection, new DropIndexIntent("ix_alias", "items"), SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, physical.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Missing, alias.ObservedState);
    }

    /// <summary>Accepted source columns do not invent destination absence on incomplete table projections.</summary>
    /// <param name="liveState">The immutable rename classification.</param>
    /// <param name="targetExists">Independent immutable destination presence.</param>
    /// <param name="expectedState">The expected ordered classification.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Different, null, SafeMigrationObservedState.Different)]
    [InlineData(SafeMigrationObservedState.Matching, null, SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationObservedState.Missing, null, SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(SafeMigrationObservedState.Missing, false, SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationObservedState.Missing, true, SafeMigrationObservedState.Different)]
    public void SparseColumnRenameRequiresIndependentDestinationEvidence(
        SafeMigrationObservedState liveState,
        bool? targetExists,
        SafeMigrationObservedState expectedState
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("source")),
            SafeMigrationObservedState.Matching);

        var live = new SafeMigrationProviderAnalysis(liveState, SafeMigrationRepairCapability.None, false, "test_live")
        {
            RenameTargetExists = targetExists,
        };

        // Act
        var analysis = projection.Project(new SafeMigrationOperation(
            new RenameColumnIntent("source", "items", "target"), SafeMigrationPolicy.ThrowIfDifferent), live);

        // Assert
        Assert.Equal(expectedState, analysis.ObservedState);
    }

    /// <summary>A complete table image proves destination absence without historical rename target evidence.</summary>
    /// <param name="indexRename">Whether the exact member being renamed is an index.</param>
    /// <param name="targetPresent">Whether the complete current image contains the destination.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CompleteTableRenameUsesCurrentDestinationEvidence(bool indexRename, bool targetPresent)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();

        var columns = targetPresent && !indexRename
            ? new[] { Column("source"), Column("code"), Column("target") }
            : new[] { Column("source"), Column("code") };

        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", columns),
            SafeMigrationTableMode.StrictDefinition));

        if (indexRename)
        {
            ObserveAccepted(projection, Index("source"), SafeMigrationObservedState.Missing);

            if (targetPresent)
            {
                ObserveAccepted(projection, new EnsureIndexIntent(new ExpectedIndexDefinition("target", "items",
                    [new ExpectedIndexKeyDefinition(column: "source")])), SafeMigrationObservedState.Missing);
            }
        }

        SafeMigrationIntent rename = indexRename
            ? new RenameIndexIntent("source", "items", "target")
            : new RenameColumnIntent("source", "items", "target");
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "test_unobserved_destination");

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(rename, SafeMigrationPolicy.ThrowIfDifferent), live);

        // Assert
        Assert.Equal(targetPresent ? SafeMigrationObservedState.Different : SafeMigrationObservedState.Matching,
            analysis.ObservedState);
    }
}
