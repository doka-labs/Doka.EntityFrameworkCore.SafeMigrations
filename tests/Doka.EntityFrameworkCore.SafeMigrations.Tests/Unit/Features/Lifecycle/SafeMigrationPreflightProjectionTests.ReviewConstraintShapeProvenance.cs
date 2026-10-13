namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>A semantic strict-table match cannot fabricate an authored physical constraint drop target.</summary>
    /// <param name="kind">The constraint family with shape-only table evidence.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public void StrictTableShapeDoesNotCreatePhysicalConstraintNames(SafeMigrationOperationKind kind)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection { CurrentOperationOrdinal = 0, };
        ObserveAccepted(projection, ReviewConstraintShapeTable(kind), SafeMigrationObservedState.Matching);
        var drop = new SafeMigrationOperation(ReviewConstraintShapeDrop(kind, "authored_alias"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Missing);
        projection.CurrentOperationOrdinal = 1;

        // Act
        var analysis = projection.Project(drop, live);
        var decision = SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, analysis.ObservedState, drop.Policy);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
        Assert.Null(analysis.ProviderDeferredOriginOrdinal);
    }

    /// <summary>
    /// An accepted physical drop invalidates shape-only identity rather than recycling stale live state.
    /// </summary>
    /// <param name="kind">The constraint family whose physical name was not resolved by table matching.</param>
    /// <param name="liveState">The immutable later ensure classification captured before the drop.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint, SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint, SafeMigrationObservedState.Different)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint, SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint, SafeMigrationObservedState.Different)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey, SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey, SafeMigrationObservedState.Different)]
    public void StrictTableShapePhysicalDropRequiresFreshIdentityWithExactOrigin(
        SafeMigrationOperationKind kind,
        SafeMigrationObservedState liveState
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection { CurrentOperationOrdinal = 0, };
        ObserveAccepted(projection, ReviewConstraintShapeTable(kind), SafeMigrationObservedState.Matching);
        projection.CurrentOperationOrdinal = 1;
        ObserveAccepted(projection, ReviewConstraintShapeDrop(kind, "physical_constraint"),
            SafeMigrationObservedState.Matching);

        var ensure = new SafeMigrationOperation(ReviewConstraintShapeEnsure(kind, "authored_alias"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // WHY: A state without a resolved physical name is not permission to
        // equate this semantic alias with the actual object removed at ordinal 1.
        var live = Live(liveState, matchedObjectName: null);
        projection.CurrentOperationOrdinal = 2;

        // Act
        var analysis = projection.Project(ensure, live);
        var decision = SafeMigrationDecisionPlanner.Plan(ensure.Intent.Kind, analysis.ObservedState, ensure.Policy);

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_constraint_identity_unknown", analysis.Code);
        Assert.Equal(1, analysis.ProviderDeferredOriginOrdinal);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, decision.Action);
        Assert.False(analysis.PostconditionSatisfied);
        Assert.False(analysis.IsOpaqueProjectionUnknown);
        Assert.Equal(SafeMigrationRepairCapability.None, analysis.RepairCapability);
    }

    /// <summary>
    /// Repeated real strict-table no-ops preserve an independently bound physical alias and its later drop.
    /// </summary>
    /// <param name="kind">The constraint family with exact standalone physical evidence.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public void RepeatedStrictTableNoOpRetainsResolvedConstraintAliasLedger(SafeMigrationOperationKind kind)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection { CurrentOperationOrdinal = 0, };
        var table = ReviewConstraintShapeTable(kind);
        ObserveAccepted(projection, table, SafeMigrationObservedState.Matching);
        projection.CurrentOperationOrdinal = 1;
        ObserveAccepted(projection, ReviewConstraintShapeEnsure(kind, "authored_alias"),
            SafeMigrationObservedState.Matching, "physical_constraint");

        var repeat = new SafeMigrationOperation(table, SafeMigrationPolicy.ThrowIfDifferent);
        var tableLive = Live(SafeMigrationObservedState.Matching);
        var physicalDrop = new SafeMigrationOperation(ReviewConstraintShapeDrop(kind, "physical_constraint"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var dropLive = Live(SafeMigrationObservedState.Missing);
        var ensure = new SafeMigrationOperation(ReviewConstraintShapeEnsure(kind, "authored_alias"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        projection.CurrentOperationOrdinal = 2;
        var repeatedTableAnalysis = projection.Project(repeat, tableLive);
        var repeatedTableDecision = SafeMigrationDecisionPlanner.Plan(repeat.Intent.Kind,
            repeatedTableAnalysis.ObservedState, repeat.Policy);

        projection.Observe(repeat, tableLive, repeatedTableAnalysis, repeatedTableDecision);
        projection.CurrentOperationOrdinal = 3;
        var dropAnalysis = projection.Project(physicalDrop, dropLive);
        var dropDecision = SafeMigrationDecisionPlanner.Plan(physicalDrop.Intent.Kind,
            dropAnalysis.ObservedState, physicalDrop.Policy);

        projection.Observe(physicalDrop, dropLive, dropAnalysis, dropDecision);
        projection.CurrentOperationOrdinal = 4;
        var replacement = projection.Project(ensure,
            Live(SafeMigrationObservedState.Matching, "physical_constraint"));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, repeatedTableAnalysis.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, repeatedTableDecision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, dropAnalysis.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, dropDecision.Action);
        Assert.Equal(SafeMigrationObservedState.Missing, replacement.ObservedState);
        Assert.Null(replacement.ProviderDeferredOriginOrdinal);
    }

    /// <summary>
    /// Actual table creation proves authored physical constraint names without a separate catalog binding.
    /// </summary>
    /// <param name="kind">The named constraint physically created with its table.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public void CreatedTableRetainsAuthoredPhysicalConstraintNameProof(SafeMigrationOperationKind kind)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection { CurrentOperationOrdinal = 0, };
        Apply(projection, ReviewConstraintShapeTable(kind));
        var drop = new SafeMigrationOperation(ReviewConstraintShapeDrop(kind, "authored_alias"),
            SafeMigrationPolicy.ThrowIfDifferent);

        projection.CurrentOperationOrdinal = 1;

        // Act
        var analysis = projection.Project(drop, Live(SafeMigrationObservedState.Missing));
        var decision = SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, analysis.ObservedState, drop.Policy);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, decision.Action);
        Assert.Null(analysis.ProviderDeferredOriginOrdinal);
    }

    /// <summary>An independently accepted NoOp receipt cannot erase an unresolved physical-removal origin.</summary>
    /// <param name="kind">The constraint family carrying an unresolved removal.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public void StrictTableNoOpObserverDefensivelyPreservesConstraintTombstoneLedger(SafeMigrationOperationKind kind)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection { CurrentOperationOrdinal = 0, };
        var table = ReviewConstraintShapeTable(kind);
        ObserveAccepted(projection, table, SafeMigrationObservedState.Matching);
        projection.CurrentOperationOrdinal = 1;
        ObserveAccepted(projection, ReviewConstraintShapeDrop(kind, "physical_constraint"),
            SafeMigrationObservedState.Matching);

        var receipt = Live(SafeMigrationObservedState.Matching);
        var noOp = SafeMigrationDecisionPlanner.Plan(SafeMigrationOperationKind.EnsureTable,
            receipt.ObservedState, SafeMigrationPolicy.ThrowIfDifferent);

        var operation = new SafeMigrationOperation(table, SafeMigrationPolicy.ThrowIfDifferent);
        var ensure = new SafeMigrationOperation(ReviewConstraintShapeEnsure(kind, "authored_alias"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        projection.CurrentOperationOrdinal = 2;
        // WHY: This isolates Observe's receipt invariant, not a claim that
        // Project would accept an old strict table shape after a physical drop.
        projection.Observe(operation, receipt, receipt, noOp);
        projection.CurrentOperationOrdinal = 3;
        var analysis = projection.Project(ensure, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, noOp.Action);
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_constraint_identity_unknown", analysis.Code);
        Assert.Equal(1, analysis.ProviderDeferredOriginOrdinal);
        Assert.False(analysis.PostconditionSatisfied);
    }

    /// <summary>
    /// Repeated semantic table matches still certify an unchanged constraint shape before any physical mutation.
    /// </summary>
    /// <param name="kind">The unchanged constraint family with unresolved physical naming.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public void RepeatedStrictTableShapeMatchesRetainUnchangedConstraintSemantics(SafeMigrationOperationKind kind)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection { CurrentOperationOrdinal = 0, };
        var table = ReviewConstraintShapeTable(kind);
        ObserveAccepted(projection, table, SafeMigrationObservedState.Matching);
        projection.CurrentOperationOrdinal = 1;
        ObserveAccepted(projection, table, SafeMigrationObservedState.Matching);
        var ensure = new SafeMigrationOperation(ReviewConstraintShapeEnsure(kind, "authored_alias"),
            SafeMigrationPolicy.ThrowIfDifferent);

        projection.CurrentOperationOrdinal = 2;

        // Act
        var analysis = projection.Project(ensure, Live(SafeMigrationObservedState.Missing));
        var decision = SafeMigrationDecisionPlanner.Plan(ensure.Intent.Kind, analysis.ObservedState, ensure.Policy);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
        Assert.Null(analysis.ProviderDeferredOriginOrdinal);
    }

    /// <summary>
    /// An unrelated unresolved removal cannot discard another member's exact physical-name certificate.
    /// </summary>
    /// <param name="kind">The family containing an unresolved member and an independently bound member.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public void UnresolvedConstraintDropPreservesOtherExactPhysicalBindings(SafeMigrationOperationKind kind)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection { CurrentOperationOrdinal = 0, };
        ObserveAccepted(projection, ReviewConstraintShapeTable(kind, includeOther: true),
            SafeMigrationObservedState.Matching);

        var known = ReviewConstraintShapeEnsure(kind, "other_alias", "code");
        projection.CurrentOperationOrdinal = 1;
        ObserveAccepted(projection, known, SafeMigrationObservedState.Matching, "other_physical");
        projection.CurrentOperationOrdinal = 2;
        ObserveAccepted(projection, ReviewConstraintShapeDrop(kind, "unresolved_physical"),
            SafeMigrationObservedState.Matching);

        var ensure = new SafeMigrationOperation(known, SafeMigrationPolicy.ThrowIfDifferent);
        var drop = new SafeMigrationOperation(ReviewConstraintShapeDrop(kind, "other_physical"),
            SafeMigrationPolicy.ThrowIfDifferent);

        projection.CurrentOperationOrdinal = 3;

        // Act
        var ensureAnalysis = projection.Project(ensure, Live(SafeMigrationObservedState.Different));
        var dropAnalysis = projection.Project(drop, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, ensureAnalysis.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, dropAnalysis.ObservedState);
        Assert.Null(ensureAnalysis.ProviderDeferredOriginOrdinal);
        Assert.Null(dropAnalysis.ProviderDeferredOriginOrdinal);
    }

    /// <summary>
    /// A stale native match cannot resurrect a removed physical member when an equivalent member remains.
    /// </summary>
    /// <param name="kind">The family containing two independently bound equivalent physical members.</param>
    [Theory]
    [InlineData(SafeMigrationOperationKind.EnsureUniqueConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureCheckConstraint)]
    [InlineData(SafeMigrationOperationKind.EnsureForeignKey)]
    public void EquivalentConstraintAliasRebindsOnlyToSurvivingPhysicalMember(SafeMigrationOperationKind kind)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection { CurrentOperationOrdinal = 0, };
        var first = ReviewConstraintShapeEnsure(kind, "first_alias");
        ObserveAccepted(projection, first, SafeMigrationObservedState.Matching, "physical_first");
        projection.CurrentOperationOrdinal = 1;
        ObserveAccepted(projection, ReviewConstraintShapeEnsure(kind, "second_alias"),
            SafeMigrationObservedState.Matching, "physical_second");

        projection.CurrentOperationOrdinal = 2;
        ObserveAccepted(projection, ReviewConstraintShapeDrop(kind, "physical_first"),
            SafeMigrationObservedState.Matching);

        var ensure = new SafeMigrationOperation(first, SafeMigrationPolicy.ThrowIfDifferent);
        var stale = Live(SafeMigrationObservedState.Matching, "physical_first");

        // Act
        projection.CurrentOperationOrdinal = 3;
        var analysis = projection.Project(ensure, stale);
        var decision = SafeMigrationDecisionPlanner.Plan(ensure.Intent.Kind, analysis.ObservedState, ensure.Policy);

        // WHY: Matching now comes from the remaining second object. Observing
        // the immutable first object's name must not create another physical lifetime.
        projection.Observe(ensure, stale, analysis, decision);
        projection.CurrentOperationOrdinal = 4;
        var firstDrop = Project(projection, ReviewConstraintShapeDrop(kind, "physical_first"),
            SafeMigrationObservedState.Matching);

        projection.CurrentOperationOrdinal = 5;
        var secondDrop = Project(projection, ReviewConstraintShapeDrop(kind, "physical_second"),
            SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Missing, firstDrop.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, secondDrop.ObservedState);
        Assert.Null(analysis.ProviderDeferredOriginOrdinal);
    }

    /// <summary>Builds a single-family strict shape without resolving any authored symbol to a physical name.</summary>
    /// <param name="kind">The represented constraint family.</param>
    /// <param name="includeOther">Whether a distinct second member supplies an independent-binding control.</param>
    /// <returns>The strict table intent containing the chosen semantic members.</returns>
    private static EnsureTableIntent ReviewConstraintShapeTable(
        SafeMigrationOperationKind kind,
        bool includeOther = false
    )
    {
        var ensure = ReviewConstraintShapeEnsure(kind, "authored_alias");
        var other = includeOther ? ReviewConstraintShapeEnsure(kind, "other_alias", "code") : null;
        ExpectedColumnDefinition[] columns = includeOther ? [Column("id"), Column("code")] : [Column("id")];
        var definition = new ExpectedTableDefinition(
            "items",
            columns,
            uniqueConstraints: ensure is EnsureUniqueConstraintIntent unique
                ? other is EnsureUniqueConstraintIntent otherUnique
                    ? [unique.Definition, otherUnique.Definition] : [unique.Definition]
                : [],
            checkConstraints: ensure is EnsureCheckConstraintIntent check
                ? other is EnsureCheckConstraintIntent otherCheck
                    ? [check.Definition, otherCheck.Definition] : [check.Definition]
                : [],
            foreignKeys: ensure is EnsureForeignKeyIntent foreignKey
                ? other is EnsureForeignKeyIntent otherForeignKey
                    ? [foreignKey.Definition, otherForeignKey.Definition] : [foreignKey.Definition]
                : []);

        return new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition);
    }

    /// <summary>Builds identical family semantics under a requested logical or physical constraint symbol.</summary>
    /// <param name="kind">The represented constraint family.</param>
    /// <param name="name">The authored alias or independently proven physical name.</param>
    /// <param name="column">The distinct local member used by the semantic definition.</param>
    /// <returns>The chosen ensure intent with the requested owner-local semantics.</returns>
    private static SafeMigrationIntent ReviewConstraintShapeEnsure(
        SafeMigrationOperationKind kind,
        string name,
        string column = "id"
    ) =>
        kind switch
        {
            SafeMigrationOperationKind.EnsureUniqueConstraint => new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition(name, "items", [column])),
            SafeMigrationOperationKind.EnsureCheckConstraint => new EnsureCheckConstraintIntent(
                new ExpectedCheckConstraintDefinition(name, "items", column + " > 0")),
            SafeMigrationOperationKind.EnsureForeignKey => new EnsureForeignKeyIntent(
                new ExpectedForeignKeyDefinition(name, "items", [column], "parents", [column])),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>
    /// Builds an exact-name drop without treating the shape's logical alias as its physical identity.
    /// </summary>
    /// <param name="kind">The represented constraint family.</param>
    /// <param name="name">The name requested for physical removal.</param>
    /// <returns>The chosen physical-name drop intent.</returns>
    private static SafeMigrationIntent ReviewConstraintShapeDrop(SafeMigrationOperationKind kind, string name) =>
        kind switch
        {
            SafeMigrationOperationKind.EnsureUniqueConstraint => new DropUniqueConstraintIntent(name, "items"),
            SafeMigrationOperationKind.EnsureCheckConstraint => new DropCheckConstraintIntent(name, "items"),
            SafeMigrationOperationKind.EnsureForeignKey => new DropForeignKeyIntent(name, "items"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
