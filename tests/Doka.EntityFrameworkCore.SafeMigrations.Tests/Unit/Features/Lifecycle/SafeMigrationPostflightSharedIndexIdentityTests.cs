namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Pins final-writer reduction to provider-certified shared index/UNIQUE physical identities.</summary>
public sealed class SafeMigrationPostflightSharedIndexIdentityTests
{
    /// <summary>Both shared namespace and member casing require provider opt-in before a drop is superseded.</summary>
    /// <param name="shared">Whether indexes and UNIQUE constraints share the physical namespace.</param>
    /// <param name="insensitive">Whether native index member names are case-insensitive.</param>
    /// <param name="dropConstraint">Whether removal uses the UNIQUE rather than ordinary index API.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void SharedNamespaceAndIndexComparerControlCrossKindFinalWriter(
        bool shared,
        bool insensitive,
        bool dropConstraint
    )
    {
        // Arrange
        var contract = new SharedIndexIdentityContract(shared, insensitive);
        SafeMigrationIntent drop = dropConstraint
            ? new DropUniqueConstraintIntent("IX_Code", "items")
            : new DropIndexIntent("IX_Code", "items");

        SafeMigrationIntent ensure = dropConstraint
            ? new EnsureIndexIntent(new ExpectedIndexDefinition("ix_code", "items",
                [new ExpectedIndexKeyDefinition("code")], unique: true))
            : new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("ix_code", "items", ["code"]));

        MigrationOperation[] operations = [Operation(drop), Operation(ensure)];
        SafeMigrationProviderAnalysis[] analyses =
        [
            Analysis(SafeMigrationObservedState.Matching),
            Analysis(SafeMigrationObservedState.Matching, "ix_code"),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, contract, contract, analyses);

        // Assert
        Assert.Equal(shared && insensitive, projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Matching aliases may share one certified physical index without merging generic providers.</summary>
    /// <param name="shared">Whether the provider explicitly shares index and UNIQUE namespaces.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingPhysicalAliasesCanonicalizeOnlyWithinCertifiedSharedNamespace(bool shared)
    {
        // Arrange
        var contract = new SharedIndexIdentityContract(shared, insensitive: true);
        MigrationOperation[] operations =
        [
            Operation(new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_alias", "items", ["code"]))),
            Operation(new EnsureIndexIntent(new ExpectedIndexDefinition("ix_alias", "items",
                [new ExpectedIndexKeyDefinition("code")], unique: true))),
        ];

        SafeMigrationProviderAnalysis[] analyses =
        [
            Analysis(SafeMigrationObservedState.Matching, "IX_Physical"),
            Analysis(SafeMigrationObservedState.Matching, "ix_physical"),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, contract, contract, analyses);

        // Assert
        Assert.Equal(shared, projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Diagnostic or unresolved names cannot certify physical identity or hide an earlier contract.</summary>
    /// <param name="state">The final catalog classification of the earlier alias.</param>
    /// <param name="physicalName">The resolved or merely diagnostic physical name.</param>
    /// <param name="superseded">Whether this evidence may establish shared physical identity.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Matching, "ix_physical", true)]
    [InlineData(SafeMigrationObservedState.Matching, null, false)]
    [InlineData(SafeMigrationObservedState.Different, "ix_physical", false)]
    [InlineData(SafeMigrationObservedState.Missing, "ix_physical", false)]
    [InlineData(SafeMigrationObservedState.Unsupported, "ix_physical", false)]
    public void UnprovenSharedAliasesRetainTheirOwnFinalPostcondition(
        SafeMigrationObservedState state,
        string? physicalName,
        bool superseded
    )
    {
        // Arrange
        var contract = new SharedIndexIdentityContract(shared: true, insensitive: true);
        MigrationOperation[] operations =
        [
            Operation(new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("uq_alias", "items", ["code"]))),
            Operation(new EnsureIndexIntent(new ExpectedIndexDefinition("ix_physical", "items",
                [new ExpectedIndexKeyDefinition("code")], unique: true))),
        ];

        SafeMigrationProviderAnalysis[] analyses =
        [
            Analysis(state, physicalName), Analysis(SafeMigrationObservedState.Matching, "ix_physical"),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, contract, contract, analyses);

        // Assert
        Assert.Equal(superseded, projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Member identity opt-in cannot merge different table owners or database qualifiers.</summary>
    /// <param name="differentSchema">Whether schema rather than raw table casing separates the physical owners.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedIndexPostflightRetainsExactPhysicalOwnerBoundary(bool differentSchema)
    {
        // Arrange
        var contract = new SharedIndexIdentityContract(shared: true, insensitive: true);
        MigrationOperation[] operations =
        [
            Operation(new DropIndexIntent("IX_Code", "Items", "owner_a")),
            Operation(new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("ix_code",
                differentSchema ? "Items" : "items", ["code"], differentSchema ? "owner_b" : "owner_a"))),
        ];

        // Act
        var projection = new SafeMigrationPostflightProjection(operations, contract, contract);

        // Assert
        Assert.False(projection.IsSuperseded(0));
        Assert.False(projection.IsSuperseded(1));
    }

    /// <summary>Creates a strict named safe operation without adding provider-specific annotations.</summary>
    private static SafeMigrationOperation Operation(SafeMigrationIntent intent) =>
        new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>Creates exact final-catalog evidence without inventing a historical physical binding.</summary>
    private static SafeMigrationProviderAnalysis Analysis(
        SafeMigrationObservedState state,
        string? physicalName = null
    ) =>
        new(state, SafeMigrationRepairCapability.None, state == SafeMigrationObservedState.Matching, "test_final")
        {
            MatchedObjectName = physicalName,
        };

    /// <summary>Certifies only native namespace/member identity, never key shape or row safety.</summary>
    private sealed class SharedIndexIdentityContract(bool shared, bool insensitive) :
        ISafeMigrationProviderObjectIdentityNormalizer,
        ISafeMigrationProjectedKeyAnalyzer
    {
        /// <inheritdoc />
        public bool SharesUniqueConstraintAndIndexIdentity => shared;
        /// <inheritdoc />
        public StringComparer IdentifierComparer => StringComparer.Ordinal;
        /// <inheritdoc />
        public StringComparer IndexIdentifierComparer =>
            insensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        /// <inheritdoc />
        public string NormalizeIdentifier(string identifier) => identifier;
        /// <inheritdoc />
        public string? NormalizeSchema(string? schema) => schema;
        /// <inheritdoc />
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
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
