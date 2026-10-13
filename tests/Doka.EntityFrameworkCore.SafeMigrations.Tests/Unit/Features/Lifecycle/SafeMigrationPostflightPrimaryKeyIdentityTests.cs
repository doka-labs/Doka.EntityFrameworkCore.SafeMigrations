namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies PK final writers against exact names, certified aliases and provider singleton identity.</summary>
public sealed class SafeMigrationPostflightPrimaryKeyIdentityTests
{
    /// <summary>A wrong-name no-op drop cannot hide any earlier named PK contract.</summary>
    /// <param name="ensureState">The actual final catalog state of the earlier Ensure contract.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationObservedState.Different)]
    [InlineData(SafeMigrationObservedState.Missing)]
    public void WrongNamedPrimaryKeyDropCannotSupersedeEarlierEnsure(SafeMigrationObservedState ensureState)
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Operation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_expected", "items", ["id"]))),
            Operation(new DropPrimaryKeyIntent("pk_wrong", "items")),
        ];
        SafeMigrationProviderAnalysis[] analyses =
        [
            Analysis(ensureState, ensureState == SafeMigrationObservedState.Matching ? "pk_expected" : "pk_wrong"),
            Analysis(SafeMigrationObservedState.Missing),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, liveAnalyses: analyses);

        // Assert
        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Actual removal supersedes its earlier exact Ensure, not the replacement's new name.</summary>
    [Fact]
    public void NamedPrimaryKeyReplacementUsesExactRemovalAndNewCreationNames()
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Operation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_old", "items", ["id"]))),
            Operation(new DropPrimaryKeyIntent("pk_old", "items")),
            Operation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_new", "items", ["code"]))),
        ];
        SafeMigrationProviderAnalysis[] analyses =
        [
            Analysis(SafeMigrationObservedState.Different, "pk_new"),
            Analysis(SafeMigrationObservedState.Missing),
            Analysis(SafeMigrationObservedState.Matching, "pk_new"),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, liveAnalyses: analyses);

        // Assert
        Assert.True(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
        Assert.False(projection.IsSuperseded(2));
    }

    /// <summary>Only independently verified Matching aliases may share final physical PK identity.</summary>
    [Fact]
    public void MatchingPrimaryKeyAliasesShareVerifiedPhysicalIdentityAcrossProviderOperations()
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Operation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_first_alias", "items", ["id"]))),
            new AlterTableOperation { Name = "other_items", Comment = "unrelated" },
            Operation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_second_alias", "items", ["id"]))),
        ];
        SafeMigrationProviderAnalysis[] analyses =
        [
            Analysis(SafeMigrationObservedState.Matching, "pk_physical"),
            Analysis(SafeMigrationObservedState.Matching, "pk_physical"),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, liveAnalyses: analyses);

        // Assert
        Assert.True(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
        Assert.False(projection.IsSuperseded(2));
    }

    /// <summary>An absent final PK cannot prove the historical binding of a different authored alias.</summary>
    [Fact]
    public void UnresolvedHistoricalPrimaryKeyAliasRetainsUnsatisfiedFinalContract()
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Operation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_alias", "items", ["id"]))),
            Operation(new DropPrimaryKeyIntent("pk_physical", "items")),
        ];
        SafeMigrationProviderAnalysis[] analyses =
        [
            Analysis(SafeMigrationObservedState.Missing),
            Analysis(SafeMigrationObservedState.Missing),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, liveAnalyses: analyses);

        // Assert
        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Provider singleton identity retains intentional MySQL owner-level PK replacement reduction.</summary>
    [Fact]
    public void NameIndependentPrimaryKeyDropSupersedesLogicalEnsureName()
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Operation(new EnsurePrimaryKeyIntent(new ExpectedPrimaryKeyDefinition("pk_authored", "items", ["id"]))),
            Operation(new DropPrimaryKeyIntent("another_authored_name", "items")),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations,
            projectedKeyAnalyzer: new SingletonPrimaryKeyAnalyzer());

        // Assert
        Assert.True(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Misaligned certificates fail before reduction can hide an unsatisfied postcondition.</summary>
    [Fact]
    public void PrimaryKeyPhysicalCertificatesRejectMismatchedSafeSubsetCount()
    {
        // Arrange
        MigrationOperation[] operations =
        [
            Operation(new DropPrimaryKeyIntent("pk_items", "items")),
        ];

        // Act
        var exception = Assert.Throws<ArgumentException>(() =>
            new SafeMigrationPostflightProjection(operations, liveAnalyses: []));

        // Assert
        Assert.Equal("liveAnalyses", exception.ParamName);
    }

    /// <summary>Creates one safe writer with a strict conflict policy.</summary>
    private static SafeMigrationOperation Operation(SafeMigrationIntent intent) =>
        new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Creates final evidence, including diagnostic names that are not identity certificates.</summary>
    private static SafeMigrationProviderAnalysis Analysis(
        SafeMigrationObservedState state,
        string? physicalName = null
    ) =>
        new(state, SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching, "test_final_state")
        {
            MatchedObjectName = physicalName,
        };

    /// <summary>Certifies only name-independent PK identity without adding shape or data proof.</summary>
    private sealed class SingletonPrimaryKeyAnalyzer : ISafeMigrationProjectedKeyAnalyzer
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
