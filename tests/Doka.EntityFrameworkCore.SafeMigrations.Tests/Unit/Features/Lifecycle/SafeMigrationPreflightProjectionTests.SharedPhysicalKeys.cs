namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    [Fact]
    public void DroppingPhysicalUniqueConstraintInvalidatesItsUniqueIndexAlias()
    {
        var projection = MySqlProjection();
        var constraint = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_items_code_alias", "items", ["code"]));

        ObserveColumnsOnExistingTable(projection, "items", Column("code"));
        ObserveAccepted(
            projection,
            constraint,
            SafeMigrationObservedState.Matching,
            "uq_items_code");
        ObserveAccepted(
            projection,
            new DropUniqueConstraintIntent("uq_items_code", "items"),
            SafeMigrationObservedState.Matching);

        var index = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ix_items_code_alias",
                "items",
                [new ExpectedIndexKeyDefinition(column: "code")],
                unique: true));

        var live = Live(SafeMigrationObservedState.Matching, "uq_items_code");
        var analysis = projection.Project(
            new SafeMigrationOperation(index, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void DroppingPhysicalUniqueIndexInvalidatesItsUniqueConstraintAlias()
    {
        var projection = MySqlProjection();
        var index = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ix_items_code_alias",
                "items",
                [new ExpectedIndexKeyDefinition(column: "code")],
                unique: true));

        ObserveColumnsOnExistingTable(projection, "items", Column("code"));
        ObserveAccepted(
            projection,
            index,
            SafeMigrationObservedState.Matching,
            "uq_items_code");
        ObserveAccepted(
            projection,
            new DropIndexIntent("uq_items_code", "items"),
            SafeMigrationObservedState.Matching);

        var constraint = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_items_code_alias", "items", ["code"]));

        var live = Live(SafeMigrationObservedState.Matching, "uq_items_code");
        var analysis = projection.Project(
            new SafeMigrationOperation(constraint, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    [Fact]
    public void CreatedUniqueConstraintSatisfiesEquivalentUniqueIndexWithoutASecondPhysicalObject()
    {
        var projection = MySqlProjection();
        var constraint = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_items_code", "items", ["code"]));

        ObserveColumnsOnExistingTable(projection, "items", Column("code"));
        ObserveAccepted(projection, constraint, SafeMigrationObservedState.Missing);

        var index = new EnsureIndexIntent(
            new ExpectedIndexDefinition(
                "ix_items_code_alias",
                "items",
                [new ExpectedIndexKeyDefinition(column: "code")],
                unique: true));

        var analysis = Project(
            projection,
            index,
            SafeMigrationObservedState.Missing);

        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
    }

    [Fact]
    public void DataMutationAfterCrossKindDropInvalidatesReplacementProof()
    {
        var projection = MySqlProjection();
        var constraint = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_items_code", "items", ["code"]));

        ObserveColumnsOnExistingTable(projection, "items", Column("code"));
        ObserveAccepted(
            projection,
            constraint,
            SafeMigrationObservedState.Matching,
            "uq_items_code");
        ObserveAccepted(
            projection,
            new DropIndexIntent("uq_items_code", "items"),
            SafeMigrationObservedState.Matching);
        projection.ObserveProviderPostcondition(
            new InsertDataOperation
            {
                Table = "audit",
                Columns = ["id"],
                Values = new object?[,] { { 1 } },
            });

        var replacement = new EnsureUniqueConstraintIntent(
            new ExpectedUniqueConstraintDefinition("uq_items_code", "items", ["code"]));

        var analysis = Project(
            projection,
            replacement,
            SafeMigrationObservedState.Matching);

        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
        Assert.Equal("projected_data_state_unknown", analysis.Code);
    }

    private static SafeMigrationPreflightProjection MySqlProjection() => new(
        projectedKeyAnalyzer: new SharedPhysicalKeyAnalyzer());

    private sealed class SharedPhysicalKeyAnalyzer : ISafeMigrationProjectedKeyAnalyzer
    {
        public bool SharesUniqueConstraintAndIndexIdentity => true;

        public SafeMigrationProviderAnalysis ValidateProjectedIndex(
            EnsureIndexIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => projectedAnalysis;

        public SafeMigrationProviderAnalysis ValidateProjectedPrimaryKey(
            EnsurePrimaryKeyIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => projectedAnalysis;

        public SafeMigrationProviderAnalysis ValidateProjectedUniqueConstraint(
            EnsureUniqueConstraintIntent intent,
            ISafeMigrationProjectedColumnSource columns,
            SafeMigrationProviderAnalysis liveAnalysis,
            SafeMigrationProviderAnalysis projectedAnalysis
        ) => projectedAnalysis;
    }
}
