namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Native restoration cannot erase exact accepted identity, aliases, tombstones or shape drift.</summary>
    /// <param name="scenario">The accepted index identity state to request.</param>
    /// <param name="expected">The resulting ordered identity state.</param>
    [Theory]
    [InlineData("exact", SafeMigrationObservedState.Matching)]
    [InlineData("different", SafeMigrationObservedState.Different)]
    [InlineData("alias", SafeMigrationObservedState.Matching)]
    [InlineData("dropped", SafeMigrationObservedState.Missing)]
    [InlineData("unrelated_owner", null)]
    [InlineData("opaque", null)]
    public void ProjectedIndexIdentityRetainsAcceptedPhysicalState(
        string scenario,
        SafeMigrationObservedState? expected
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("left_value")),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("right_value")),
            SafeMigrationObservedState.Matching);

        var index = new ExpectedIndexDefinition("ix_items", "items",
            [new ExpectedIndexKeyDefinition(column: "left_value")]);

        Apply(projection, new EnsureIndexIntent(index));
        if (scenario == "dropped")
        {
            Apply(projection, new DropIndexIntent("ix_items", "items"));
        }
        else if (scenario == "opaque")
        {
            projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;", });
        }

        var target = new ExpectedIndexDefinition(scenario == "alias" ? "ix_alias" : "ix_items",
            scenario == "unrelated_owner" ? "other_items" : "items",
            [new ExpectedIndexKeyDefinition(column: scenario == "different" ? "right_value" : "left_value")]);

        // Act
        var analysis = ((ISafeMigrationProjectedColumnSource)projection).GetProjectedIndexIdentityAnalysis(
            target, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(expected, analysis?.ObservedState);
    }

    /// <summary>Native backing-name proof keeps accepted PK/UNIQUE index collisions distinct from absence.</summary>
    /// <param name="primary">Whether the accepted key is a primary key rather than UNIQUE.</param>
    /// <param name="nativeBackingNames">Whether this provider proves equal constraint and backing-index names.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ProjectedIndexIdentityIncludesOnlyCertifiedConstraintBackingNames(bool primary, bool nativeBackingNames)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition("items",
            [Column("value"), Column("derived"), Column("retired")],
            primaryKey: primary ? new ExpectedPrimaryKeyDefinition("ix_result", "items", ["value"]) : null,
            uniqueConstraints: primary ? [] :
                [new ExpectedUniqueConstraintDefinition("ix_result", "items", ["value"])]);

        Apply(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        Apply(projection, new DropColumnIntent("retired", "items"));
        var target = new ExpectedIndexDefinition("ix_result", "items",
            [new ExpectedIndexKeyDefinition(column: "derived")]);

        // Act
        var analysis = ((ISafeMigrationProjectedColumnSource)projection).GetProjectedIndexIdentityAnalysis(
            target, Live(SafeMigrationObservedState.Missing), nativeBackingNames);

        // Assert
        Assert.Equal(nativeBackingNames ? SafeMigrationObservedState.Different : null, analysis?.ObservedState);
    }

    /// <summary>Removed accepted keys cannot keep a stale backing-index namespace collision alive.</summary>
    /// <param name="primary">Whether the removed key is PRIMARY rather than UNIQUE.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DroppedConstraintDoesNotRetainBackingIndexCollision(bool primary)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var table = new ExpectedTableDefinition("items", [Column("value"), Column("derived")],
            primaryKey: primary ? new ExpectedPrimaryKeyDefinition("ix_result", "items", ["value"]) : null,
            uniqueConstraints: primary ? [] :
                [new ExpectedUniqueConstraintDefinition("ix_result", "items", ["value"])]);

        Apply(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        Apply(projection, primary ? new DropPrimaryKeyIntent("ix_result", "items")
            : new DropUniqueConstraintIntent("ix_result", "items"));

        var target = new ExpectedIndexDefinition("ix_result", "items",
            [new ExpectedIndexKeyDefinition(column: "derived")]);

        // Act
        var analysis = ((ISafeMigrationProjectedColumnSource)projection).GetProjectedIndexIdentityAnalysis(
            target, Live(SafeMigrationObservedState.Missing), includeConstraintBackingIndexNames: true);

        // Assert
        Assert.NotEqual(SafeMigrationObservedState.Different, analysis?.ObservedState);
    }

    /// <summary>A logical UNIQUE alias does not acquire its independently proven physical backing-index name.</summary>
    /// <param name="physicalName">Whether the request uses the actual native name rather than its alias.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstraintBackingIndexCollisionUsesPhysicalNameNotSemanticAlias(bool physicalName)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("value")),
            SafeMigrationObservedState.Matching);

        ObserveAccepted(projection, new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition(
                "logical_key", "items", ["value"])), SafeMigrationObservedState.Matching, "physical_key");

        var target = new ExpectedIndexDefinition(physicalName ? "physical_key" : "logical_key", "items",
            [new ExpectedIndexKeyDefinition(column: "value")]);

        // Act
        var analysis = ((ISafeMigrationProjectedColumnSource)projection).GetProjectedIndexIdentityAnalysis(
            target, Live(SafeMigrationObservedState.Missing), includeConstraintBackingIndexNames: true);

        // Assert
        Assert.Equal(physicalName ? SafeMigrationObservedState.Different : null, analysis?.ObservedState);
    }
}
