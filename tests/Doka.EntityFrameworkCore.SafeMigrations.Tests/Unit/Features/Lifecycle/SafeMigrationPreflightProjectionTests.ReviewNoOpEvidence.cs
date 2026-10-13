namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Existence-only acceptance cannot invent column shape or replace prior exact evidence.</summary>
    /// <param name="sourceKind">No accepted source, a sparse column proof, or a complete table image.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExistenceOnlyColumnDifferencePreservesActualEvidence(int sourceKind)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var actual = NullableColumn("value");
        var target = Column("value");

        if (sourceKind == 1)
        {
            ObserveAccepted(projection, new EnsureColumnIntent("items", actual), SafeMigrationObservedState.Matching);
        }
        else if (sourceKind == 2)
        {
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [actual]),
                SafeMigrationTableMode.StrictDefinition));
        }

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.ExistenceOnly);
        var live = Live(SafeMigrationObservedState.Different);
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy);

        // Act
        projection.Observe(operation, live, analysis, decision);

        var strict = Project(projection, new EnsureColumnIntent("items", target), SafeMigrationObservedState.Different);
        var sourceExists = ((ISafeMigrationProjectedColumnSource)projection)
            .TryGetProjectedColumn("items", null, "value", out var source);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Different, strict.ObservedState);
        Assert.Equal(sourceKind != 0, sourceExists);
        Assert.Equal(sourceKind != 0 ? actual : null, source);
    }

    /// <summary>Existence-only acceptance cannot fabricate key membership for later strict operations.</summary>
    /// <param name="sourceKind">No accepted key, a sparse key proof, or a complete table image.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExistenceOnlyPrimaryKeyDifferencePreservesActualEvidence(int sourceKind)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var actual = new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id"]);
        var target = new ExpectedPrimaryKeyDefinition("pk_items", "items", ["code"]);

        if (sourceKind == 1)
        {
            ObserveAccepted(projection, new EnsurePrimaryKeyIntent(actual), SafeMigrationObservedState.Matching);
        }
        else if (sourceKind == 2)
        {
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("id"), Column("code")],
                primaryKey: actual), SafeMigrationTableMode.StrictDefinition));
        }

        var operation = new SafeMigrationOperation(new EnsurePrimaryKeyIntent(target),
            SafeMigrationPolicy.ExistenceOnly);

        var live = Live(SafeMigrationObservedState.Different);
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy);

        // Act
        projection.Observe(operation, live, analysis, decision);

        var strict = Project(projection, new EnsurePrimaryKeyIntent(target), SafeMigrationObservedState.Different);
        var membership = ((ISafeMigrationProjectedColumnSource)projection)
            .IsProjectedPrimaryKeyColumn("items", null, "code", StringComparer.Ordinal);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Different, strict.ObservedState);
        Assert.Equal(sourceKind != 0 ? false : (bool?)null, membership);
    }

    /// <summary>Projected Matching remains valid when immutable live evidence still describes old shape.</summary>
    [Fact]
    public void ProjectedMatchingNoOpRetainsAcceptedRepairAgainstDifferentLiveState()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var target = Column("value");
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.RepairIfSafe);

        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, false, "test_safe_repair");
        var repair = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, live.ObservedState,
            operation.Policy, live.RepairCapability);

        projection.Observe(operation, live, live, repair);

        var matching = projection.Project(operation, live);
        var noOp = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, matching.ObservedState, operation.Policy);

        // Act
        projection.Observe(operation, live, matching, noOp);

        var retained = Project(projection, new EnsureColumnIntent("items", target),
            SafeMigrationObservedState.Different);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, repair.Action);
        Assert.Equal(SafeMigrationAction.NoOp, noOp.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, retained.ObservedState);
    }

    /// <summary>Existence-only drift cannot rebind a logical alias to a different accepted physical object.</summary>
    /// <param name="family">The projected constraint or index family.</param>
    [Theory]
    [InlineData("index")]
    [InlineData("unique")]
    [InlineData("check")]
    [InlineData("foreign")]
    public void ExistenceOnlyDifferentDoesNotRebindAcceptedSemanticAlias(string family)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var (original, other, desired) = ReviewAliasContracts(family);

        ObserveAccepted(projection, original, SafeMigrationObservedState.Matching, "physical_original");
        ObserveAccepted(projection, other, SafeMigrationObservedState.Missing);

        var operation = new SafeMigrationOperation(desired, SafeMigrationPolicy.ExistenceOnly);
        var live = Live(SafeMigrationObservedState.Different);
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(desired.Kind, analysis.ObservedState, operation.Policy);

        // Act
        projection.Observe(operation, live, analysis, decision);

        var desiredState = Project(projection, desired, SafeMigrationObservedState.Different);
        var originalState = Project(projection, original, SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Different, desiredState.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, originalState.ObservedState);
    }

    /// <summary>An existence-only conflict cannot resurrect a removed column's earlier physical lifetime.</summary>
    [Fact]
    public void ExistenceOnlyDifferentPreservesRecreatedColumnLifetime()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var recreated = NullableColumn("value");

        ObserveAccepted(projection, new EnsureColumnIntent("items", Column("value")),
            SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new DropColumnIntent("value", "items"), SafeMigrationObservedState.Matching);
        ObserveAccepted(projection, new EnsureColumnIntent("items", recreated), SafeMigrationObservedState.Matching);

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", Column("value")),
            SafeMigrationPolicy.ExistenceOnly);
        var live = Live(SafeMigrationObservedState.Different);
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy);

        // Act
        projection.Observe(operation, live, analysis, decision);

        var actual = Project(projection, new EnsureColumnIntent("items", recreated),
            SafeMigrationObservedState.Matching);
        var incompatible = Project(projection, operation.Intent, SafeMigrationObservedState.Different);

        // Assert
        Assert.Equal(SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, actual.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Different, incompatible.ObservedState);
    }

    /// <summary>An accepted standalone CHECK has an owner even without prior table or column convergence.</summary>
    /// <param name="sameExpression">Whether the next request repeats the accepted expression.</param>
    [Theory]
    [InlineData(false, SafeMigrationObservedState.Different)]
    [InlineData(true, SafeMigrationObservedState.Matching)]
    public void StandaloneCheckEnsureRecordsAcceptedPhysicalDefinition(
        bool sameExpression,
        SafeMigrationObservedState expectedState
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var first = ExpectedCheckConstraintDefinition.FromExpression("ck_items", "items", Positive("id"));
        var second = sameExpression
            ? first
            : ExpectedCheckConstraintDefinition.FromExpression("ck_items", "items", Positive("other"));

        ObserveAccepted(projection, new EnsureCheckConstraintIntent(first), SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(projection, new EnsureCheckConstraintIntent(second), SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(expectedState, analysis.ObservedState);
    }

    /// <summary>A standalone CHECK alias shares its provider-resolved physical object's lifetime.</summary>
    [Fact]
    public void StandaloneCheckAliasIsInvalidatedWithItsPhysicalObject()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection();
        var definition = ExpectedCheckConstraintDefinition.FromExpression("ck_alias", "items", Positive("id"));

        ObserveAccepted(projection, new EnsureCheckConstraintIntent(definition), SafeMigrationObservedState.Matching,
            matchedObjectName: "ck_physical");
        ObserveAccepted(projection, new DropCheckConstraintIntent("ck_physical", "items"),
            SafeMigrationObservedState.Matching);

        // Act
        var analysis = projection.Project(new SafeMigrationOperation(new EnsureCheckConstraintIntent(definition),
                SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Matching, "ck_physical"));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>Builds two accepted physical shapes and an incompatible logical-alias request for one family.</summary>
    /// <param name="family">The constraint or index family.</param>
    private static (SafeMigrationIntent Original, SafeMigrationIntent Other, SafeMigrationIntent Desired)
        ReviewAliasContracts(string family) => family switch
        {
            "index" => (
                new EnsureIndexIntent(new ExpectedIndexDefinition("alias", "items",
                    [new ExpectedIndexKeyDefinition(column: "id")])),
                new EnsureIndexIntent(new ExpectedIndexDefinition("physical_other", "items",
                    [new ExpectedIndexKeyDefinition(column: "other")])),
                new EnsureIndexIntent(new ExpectedIndexDefinition("alias", "items",
                    [new ExpectedIndexKeyDefinition(column: "other")]))),
            "unique" => (
                new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("alias", "items", ["id"])),
                new EnsureUniqueConstraintIntent(
                    new ExpectedUniqueConstraintDefinition("physical_other", "items", ["other"])),
                new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("alias", "items", ["other"]))),
            "check" => (
                new EnsureCheckConstraintIntent(
                    ExpectedCheckConstraintDefinition.FromExpression("alias", "items", Positive("id"))),
                new EnsureCheckConstraintIntent(
                    ExpectedCheckConstraintDefinition.FromExpression("physical_other", "items", Positive("other"))),
                new EnsureCheckConstraintIntent(
                    ExpectedCheckConstraintDefinition.FromExpression("alias", "items", Positive("other")))),
            "foreign" => (
                new EnsureForeignKeyIntent(
                    new ExpectedForeignKeyDefinition("alias", "items", ["id"], "parents", ["id"])),
                new EnsureForeignKeyIntent(
                    new ExpectedForeignKeyDefinition("physical_other", "items", ["other"], "parents", ["id"])),
                new EnsureForeignKeyIntent(
                    new ExpectedForeignKeyDefinition("alias", "items", ["other"], "parents", ["id"]))),
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown review regression family."),
        };
}
