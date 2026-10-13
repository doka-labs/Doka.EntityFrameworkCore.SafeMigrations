namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Named PK drops cannot replace native wrong-name rejection with fabricated physical presence.</summary>
    /// <param name="completeTable">Whether a created table supplies a complete physical image.</param>
    /// <param name="liveState">The provider's exact wrong-name classification.</param>
    [Theory]
    [InlineData(false, SafeMigrationObservedState.Missing)]
    [InlineData(false, SafeMigrationObservedState.Different)]
    [InlineData(false, SafeMigrationObservedState.Unsupported)]
    [InlineData(true, SafeMigrationObservedState.Missing)]
    [InlineData(true, SafeMigrationObservedState.Different)]
    [InlineData(true, SafeMigrationObservedState.Unsupported)]
    public void NamedPrimaryKeyWrongNameDropPreservesPhysicalKey(
        bool completeTable,
        SafeMigrationObservedState liveState
    )
    {
        // Arrange
        var projection = ReviewPrimaryKeyIdentityProjection(completeTable);
        var drop = new SafeMigrationOperation(new DropPrimaryKeyIntent("wrong_name", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(liveState);

        // Act
        var analysis = projection.Project(drop, live);
        var decision = SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, analysis.ObservedState, drop.Policy);

        projection.Observe(drop, live, analysis, decision);

        var retained = Project(projection, new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_physical", "items", ["id"])), SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(liveState, analysis.ObservedState);
        Assert.NotEqual(SafeMigrationAction.Apply, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, retained.ObservedState);
    }

    /// <summary>A semantic Ensure alias never changes the physical name required by later named PK drops.</summary>
    /// <param name="liveDropState">The provider's physical classification for the logical alias.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Missing)]
    [InlineData(SafeMigrationObservedState.Different)]
    public void PrimaryKeySemanticAliasDoesNotBecomePhysicalDropName(SafeMigrationObservedState liveDropState)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var alias = new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_alias", "items", ["id"]));

        ObserveAccepted(projection, alias, SafeMigrationObservedState.Matching, "pk_physical");

        var drop = new SafeMigrationOperation(new DropPrimaryKeyIntent("pk_alias", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(liveDropState);

        // Act
        var analysis = projection.Project(drop, live);
        var decision = SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, analysis.ObservedState, drop.Policy);

        projection.Observe(drop, live, analysis, decision);

        var physical = Project(projection, new DropPrimaryKeyIntent("pk_physical", "items"),
            SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(liveDropState, analysis.ObservedState);
        Assert.NotEqual(SafeMigrationAction.Apply, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, physical.ObservedState);
    }

    /// <summary>An unresolved semantic PK proof cannot turn an authored name into physical-name evidence.</summary>
    /// <param name="fromTable">Whether strict table matching supplied the semantic key proof.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnresolvedPrimaryKeyPhysicalIdentityRetainsNativeWrongNameRejection(bool fromTable)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = new ExpectedPrimaryKeyDefinition("pk_authored", "items", ["id"]);

        if (fromTable)
        {
            ObserveAccepted(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("id")],
                primaryKey: definition), SafeMigrationTableMode.StrictDefinition), SafeMigrationObservedState.Matching);
        }
        else
        {
            ObserveAccepted(projection, new EnsurePrimaryKeyIntent(definition), SafeMigrationObservedState.Matching,
                matchedObjectName: null);
        }

        var live = Live(SafeMigrationObservedState.Different);

        // Act
        var analysis = projection.Project(new SafeMigrationOperation(new DropPrimaryKeyIntent("pk_authored", "items"),
            SafeMigrationPolicy.ThrowIfDifferent), live);

        // Assert
        Assert.Same(live, analysis);
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }

    /// <summary>Exact physical PK removal invalidates semantic aliases and permits a fresh physical lifetime.</summary>
    /// <param name="completeTable">Whether the original PK belongs to a complete created table.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhysicalPrimaryKeyDropAndRecreationRetainsExactNameLifetime(bool completeTable)
    {
        // Arrange
        var projection = ReviewPrimaryKeyIdentityProjection(completeTable);
        var original = new ExpectedPrimaryKeyDefinition("pk_physical", "items", ["id"]);
        var replacement = new ExpectedPrimaryKeyDefinition("pk_recreated", "items", ["id"]);

        ObserveAccepted(projection, new DropPrimaryKeyIntent(original.Name, "items"),
            SafeMigrationObservedState.Matching);

        var replacementOperation = new SafeMigrationOperation(new EnsurePrimaryKeyIntent(replacement),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Matching, original.Name);
        var analysis = projection.Project(replacementOperation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(replacementOperation.Intent.Kind, analysis.ObservedState,
            replacementOperation.Policy);

        // Act
        projection.Observe(replacementOperation, live, analysis, decision);

        var oldName = Project(projection, new DropPrimaryKeyIntent(original.Name, "items"),
            SafeMigrationObservedState.Matching);

        var currentName = Project(projection, new DropPrimaryKeyIntent(replacement.Name, "items"),
            SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Missing, oldName.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, currentName.ObservedState);
    }

    /// <summary>MySQL-style singleton PK identity intentionally ignores the authored logical constraint name.</summary>
    /// <param name="completeTable">Whether table creation rather than a sparse Ensure supplied the PK.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NameIndependentPrimaryKeyIdentityPermitsLogicalNameDrop(bool completeTable)
    {
        // Arrange
        var projection = ReviewPrimaryKeyIdentityProjection(completeTable, nameIndependent: true);
        var operation = new SafeMigrationOperation(new DropPrimaryKeyIntent("any_authored_name", "items"),
            SafeMigrationPolicy.ThrowIfDifferent);
        var live = Live(SafeMigrationObservedState.Matching);

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy);

        projection.Observe(operation, live, analysis, decision);

        var repeated = Project(projection, new DropPrimaryKeyIntent("another_authored_name", "items"),
            SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Missing, repeated.ObservedState);
    }

    /// <summary>Builds complete or sparse PK proof without inventing columns or provider identity.</summary>
    /// <param name="completeTable">Whether to create the complete owner table.</param>
    /// <param name="nameIndependent">Whether the provider explicitly opts into singleton PK identity.</param>
    private static SafeMigrationPreflightProjection ReviewPrimaryKeyIdentityProjection(
        bool completeTable,
        bool nameIndependent = false
    )
    {
        var projection = new SafeMigrationPreflightProjection(projectedKeyAnalyzer:
            nameIndependent ? new ReviewNameIndependentPrimaryKeyAnalyzer() : null);

        var definition = new ExpectedPrimaryKeyDefinition("pk_physical", "items", ["id"]);

        if (completeTable)
        {
            Apply(projection, new EnsureTableIntent(
                new ExpectedTableDefinition("items", [Column("id")], primaryKey: definition),
                SafeMigrationTableMode.StrictDefinition));
        }
        else
        {
            ObserveColumnsOnExistingTable(projection, "items", Column("id"));
            ObserveAccepted(projection, new EnsurePrimaryKeyIntent(definition),
                SafeMigrationObservedState.Matching, definition.Name);
        }

        return projection;
    }

    /// <summary>Opts into only MySQL-style PK singleton identity; it grants no additional shape or row proof.</summary>
    private sealed class ReviewNameIndependentPrimaryKeyAnalyzer : ISafeMigrationProjectedKeyAnalyzer
    {
        /// <inheritdoc />
        public bool SharesUniqueConstraintAndIndexIdentity => false;

        /// <inheritdoc />
        public bool PrimaryKeyUsesNameIndependentIdentity => true;

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedIndex(EnsureIndexIntent intent,
            ISafeMigrationProjectedColumnSource columns, SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis) => projectedAnalysis;

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedPrimaryKey(EnsurePrimaryKeyIntent intent,
            ISafeMigrationProjectedColumnSource columns, SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis) => projectedAnalysis;

        /// <inheritdoc />
        public SafeMigrationProviderAnalysis ValidateProjectedUniqueConstraint(EnsureUniqueConstraintIntent intent,
            ISafeMigrationProjectedColumnSource columns, SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis) => projectedAnalysis;
    }
}
