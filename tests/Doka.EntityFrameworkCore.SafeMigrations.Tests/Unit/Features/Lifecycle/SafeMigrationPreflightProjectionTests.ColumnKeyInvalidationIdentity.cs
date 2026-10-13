namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Uncertified repairs invalidate every affected index and UNIQUE alias using column identity.</summary>
    /// <param name="complete">Whether the physical owner has a complete accepted creation definition.</param>
    /// <param name="constraint">Whether a UNIQUE constraint supplies the accepted physical key.</param>
    /// <param name="mixedCase">Whether the repair spells the affected column differently.</param>
    /// <param name="insensitive">Whether the provider explicitly folds column identities.</param>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public void UnmarkedRepairInvalidatesColumnAwarePhysicalKeyAliases(
        bool complete,
        bool constraint,
        bool mixedCase,
        bool insensitive
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            projectedKeyAnalyzer: new SharedPhysicalKeyAnalyzer(),
            objectIdentityNormalizer: new ReviewIndexMemberNormalizer(insensitiveColumns: insensitive));

        if (complete)
        {
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("Items", [Column("Code")]),
                SafeMigrationTableMode.StrictDefinition));
        }

        var original = ColumnInvalidationKey("logical_original", "Items", constraint);
        var alias = ColumnInvalidationKey("logical_alias", "Items", constraint);
        var acceptedState = complete ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching;

        ObserveAccepted(projection, original, acceptedState, complete ? null : "physical_key");
        ObserveAccepted(projection, alias, SafeMigrationObservedState.Matching,
            complete ? "logical_original" : "physical_key");
        var repair = new SafeMigrationOperation(new EnsureColumnIntent("Items",
                new ExpectedColumnDefinition(mixedCase ? "code" : "Code", typeof(int),
                    isNullable: true, storeType: "int")),
            SafeMigrationPolicy.RepairIfSafe);

        var repairAnalysis = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, postconditionSatisfied: false, "uncertified_key_layout_repair");

        var repairDecision = SafeMigrationDecisionPlanner.Plan(repair.Intent.Kind, repairAnalysis.ObservedState,
            repair.Policy, repairAnalysis.RepairCapability);

        var invalidated = !mixedCase || insensitive;
        var expected = invalidated
            ? SafeMigrationObservedState.Missing
            : SafeMigrationObservedState.Matching;

        // Act
        projection.Observe(repair, repairAnalysis, repairAnalysis, repairDecision);
        var originalResult = Project(projection, original, SafeMigrationObservedState.Different);
        var aliasResult = Project(projection, alias, SafeMigrationObservedState.Different);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, repairDecision.Action);
        Assert.False(repairAnalysis.RepairPreservesPhysicalKeys);
        Assert.Equal(expected, originalResult.ObservedState);
        Assert.Equal(expected, aliasResult.ObservedState);
        Assert.Equal("Code", ColumnInvalidationKeyMember(original));
        Assert.Equal("Code", ColumnInvalidationKeyMember(alias));
    }

    /// <summary>Case-insensitive members cannot transfer invalidation to another owner, schema or column.</summary>
    /// <param name="constraint">Whether the accepted key is a UNIQUE constraint rather than an index.</param>
    /// <param name="boundary">The independently distinct physical identity in the repair.</param>
    [Theory]
    [InlineData(false, "owner")]
    [InlineData(false, "schema")]
    [InlineData(false, "column")]
    [InlineData(true, "owner")]
    [InlineData(true, "schema")]
    [InlineData(true, "column")]
    public void ColumnKeyInvalidationRetainsExactPhysicalOwnerBoundary(bool constraint, string boundary)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            projectedKeyAnalyzer: new SharedPhysicalKeyAnalyzer(),
            objectIdentityNormalizer: new ReviewIndexMemberNormalizer(insensitiveColumns: true));

        var original = ColumnInvalidationKey("physical_key", "Items", constraint, "Owner");

        ObserveAccepted(projection, original, SafeMigrationObservedState.Matching);
        var repair = new SafeMigrationOperation(new EnsureColumnIntent(boundary == "owner" ? "items" : "Items",
                new ExpectedColumnDefinition(boundary == "column" ? "OtherCode" : "code", typeof(int),
                    isNullable: true, storeType: "int"),
                boundary == "schema" ? "owner" : "Owner"),
            SafeMigrationPolicy.RepairIfSafe);

        var analysis = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.Safe, postconditionSatisfied: false, "other_owner_repair");

        var decision = SafeMigrationDecisionPlanner.Plan(repair.Intent.Kind, analysis.ObservedState,
            repair.Policy, analysis.RepairCapability);

        // Act
        projection.Observe(repair, analysis, analysis, decision);
        var result = Project(projection, original, SafeMigrationObservedState.Different);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, decision.Action);
        Assert.Equal(SafeMigrationObservedState.Matching, result.ObservedState);
    }

    /// <summary>Creates immutable cross-kind key definitions without seeding row-safety certificates.</summary>
    private static SafeMigrationIntent ColumnInvalidationKey(
        string name,
        string table,
        bool constraint,
        string? schema = null
    ) => constraint
        ? new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition(name, table, ["Code"], schema))
        : new EnsureIndexIntent(new ExpectedIndexDefinition(name, table,
            [new ExpectedIndexKeyDefinition("Code")], schema, unique: true));

    /// <summary>Reads the caller-owned definition to guard against mutation while retiring physical aliases.</summary>
    private static string ColumnInvalidationKeyMember(SafeMigrationIntent intent) => intent switch
    {
        EnsureIndexIntent index => index.Definition.Keys[0].Column!,
        EnsureUniqueConstraintIntent constraint => constraint.Definition.Columns[0],
        _ => throw new InvalidOperationException("Only index and UNIQUE definitions are part of this fixture."),
    };
}
