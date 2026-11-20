namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>
    /// Invalidates the unique-index alias bound to a removed physical unique constraint under shared provider
    /// identity.
    /// </summary>
    [Fact]
    public void DroppingPhysicalUniqueConstraintInvalidatesItsUniqueIndexAlias()
    {
        // Arrange
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

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(index, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Invalidates the unique-constraint alias when the same shared physical key is removed through the index
    /// contract.
    /// </summary>
    [Fact]
    public void DroppingPhysicalUniqueIndexInvalidatesItsUniqueConstraintAlias()
    {
        // Arrange
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

        // Act
        var analysis = projection.Project(
            new SafeMigrationOperation(constraint, SafeMigrationPolicy.ThrowIfDifferent),
            live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal("projected_missing", analysis.Code);
    }

    /// <summary>
    /// Recognizes an accepted unique constraint as the one equivalent index lifetime on shared-namespace providers.
    /// </summary>
    [Fact]
    public void CreatedUniqueConstraintSatisfiesEquivalentUniqueIndexWithoutASecondPhysicalObject()
    {
        // Arrange
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

        // Act
        var analysis = Project(
            projection,
            index,
            SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.True(analysis.PostconditionSatisfied);
    }

    /// <summary>
    /// Rejects shared-key replacement after DML consumes the earlier physical-removal data proof.
    /// </summary>
    [Fact]
    public void DataMutationAfterCrossKindDropInvalidatesReplacementProof()
    {
        // Arrange
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

        // Act
        var analysis = Project(
            projection,
            replacement,
            SafeMigrationObservedState.Matching);

        // Assert
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
