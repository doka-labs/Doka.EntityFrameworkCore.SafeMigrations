namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>A complete owner image cannot erase independent schema-global destination occupancy.</summary>
    /// <param name="completeTable">Whether accepted source creation supplies a complete table image.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchemaGlobalIndexRenameRetainsIndependentDestinationConflict(bool completeTable)
    {
        // Arrange
        var projection = ReviewNamespaceProjection(completeTable);
        var live = ReviewNamespaceRenameAnalysis(SafeMigrationObservedState.Missing,
            targetExists: true, sourceSupported: false);

        // Act
        var analysis = ReviewNamespaceRename(projection, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }

    /// <summary>New physical relations under another owner supersede immutable target absence.</summary>
    /// <param name="kind">The accepted physical relation occupying the requested name.</param>
    [Theory]
    [InlineData("index")]
    [InlineData("table")]
    [InlineData("primary")]
    [InlineData("unique")]
    public void SchemaGlobalIndexRenameDetectsProjectedCrossOwnerRelation(string kind)
    {
        // Arrange
        var projection = ReviewNamespaceProjection(completeTable: true);

        ReviewNamespaceCreateOccupant(projection, kind);

        var live = ReviewNamespaceRenameAnalysis(SafeMigrationObservedState.Matching,
            targetExists: false, sourceSupported: true);

        // Act
        var analysis = ReviewNamespaceRename(projection, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }

    /// <summary>Constraint symbols without certified backing names are not schema-global index relations.</summary>
    /// <param name="kind">The constraint symbol being introduced under another owner.</param>
    /// <param name="backingNames">Whether PK/UNIQUE names identify equally named backing indexes.</param>
    [Theory]
    [InlineData("check", true)]
    [InlineData("foreign", true)]
    [InlineData("primary", false)]
    [InlineData("unique", false)]
    public void SchemaGlobalIndexRenameDoesNotInventConstraintRelationNames(string kind, bool backingNames)
    {
        // Arrange
        var projection = ReviewNamespaceProjection(completeTable: true, backingNames: backingNames);

        ReviewNamespaceCreateOccupant(projection, kind);

        var live = ReviewNamespaceRenameAnalysis(SafeMigrationObservedState.Matching,
            targetExists: false, sourceSupported: true);

        // Act
        var analysis = ReviewNamespaceRename(projection, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
    }

    /// <summary>An accepted exact physical drop on another owner frees the shared destination namespace.</summary>
    [Fact]
    public void SchemaGlobalIndexRenameUsesCrossOwnerPhysicalDropEvidence()
    {
        // Arrange
        var projection = ReviewNamespaceProjection(completeTable: false);

        ReviewNamespaceCreateOccupant(projection, "index");
        ObserveAccepted(projection, new DropIndexIntent("target", "other_items"), SafeMigrationObservedState.Matching);

        var live = ReviewNamespaceRenameAnalysis(SafeMigrationObservedState.Different,
            targetExists: true, sourceSupported: true);

        // Act
        var analysis = ReviewNamespaceRename(projection, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
    }

    /// <summary>A new relation lifetime always wins over an older cross-owner index-removal tombstone.</summary>
    /// <param name="kind">The new physical relation that reuses the removed index name.</param>
    [Theory]
    [InlineData("index")]
    [InlineData("table")]
    [InlineData("primary")]
    [InlineData("unique")]
    public void SchemaGlobalIndexRenameRejectsRecreatedDestinationAfterDrop(string kind)
    {
        // Arrange
        var projection = ReviewNamespaceProjection(completeTable: false);

        ReviewNamespaceCreateOccupant(projection, "index");
        ObserveAccepted(projection, new DropIndexIntent("target", "other_items"), SafeMigrationObservedState.Matching);
        ReviewNamespaceCreateOccupant(projection, kind, owner: "replacement_items");

        var live = ReviewNamespaceRenameAnalysis(SafeMigrationObservedState.Different,
            targetExists: true, sourceSupported: true);

        // Act
        var analysis = ReviewNamespaceRename(projection, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }

    /// <summary>A physical name in another schema or owner-local provider namespace does not collide.</summary>
    /// <param name="schemaScoped">Whether the provider uses schema-global index names.</param>
    /// <param name="otherSchema">The other owner's schema qualifier.</param>
    [Theory]
    [InlineData(true, "archive")]
    [InlineData(false, null)]
    public void IndexRenamePreservesSchemaAndOwnerNamespaceIsolation(bool schemaScoped, string? otherSchema)
    {
        // Arrange
        var projection = ReviewNamespaceProjection(completeTable: false, schemaScoped: schemaScoped);

        ReviewNamespaceCreateOccupant(projection, "index", schema: otherSchema);

        var live = ReviewNamespaceRenameAnalysis(SafeMigrationObservedState.Matching,
            targetExists: false, sourceSupported: true);

        // Act
        var analysis = ReviewNamespaceRename(projection, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
    }

    /// <summary>Target removal cannot discharge an unproven or independently rejected rename source kind.</summary>
    /// <param name="sourceSupported">The provider's independent ordinary-index source certificate.</param>
    /// <param name="expectedState">The expected projected state after target removal.</param>
    [Theory]
    [InlineData(null, SafeMigrationObservedState.Different)]
    [InlineData(false, SafeMigrationObservedState.Different)]
    [InlineData(true, SafeMigrationObservedState.Matching)]
    public void IndexTargetDropRequiresIndependentSourceEligibility(
        bool? sourceSupported,
        SafeMigrationObservedState expectedState
    )
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ReviewIndexNamespaceNormalizer());


        ObserveAccepted(projection, new EnsureIndexIntent(new ExpectedIndexDefinition("source", "items",
            [new ExpectedIndexKeyDefinition(column: "id")])), SafeMigrationObservedState.Matching, "source");
        ReviewNamespaceCreateOccupant(projection, "index");
        ObserveAccepted(projection, new DropIndexIntent("target", "other_items"), SafeMigrationObservedState.Matching);

        var live = ReviewNamespaceRenameAnalysis(SafeMigrationObservedState.Different,
            targetExists: true, sourceSupported);

        // Act
        var analysis = ReviewNamespaceRename(projection, live);

        // Assert
        Assert.Equal(expectedState, analysis.ObservedState);

        if (sourceSupported != true)
        {
            Assert.Same(live, analysis);
        }
    }

    /// <summary>Fresh ordinary index creation replaces an old wrong-kind source lifetime.</summary>
    /// <param name="completeTable">Whether the fresh ordinary source belongs to a complete current table.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexRenameUsesFreshCreatedSourceInsteadOfOldWrongKind(bool completeTable)
    {
        // Arrange
        var projection = ReviewNamespaceProjection(completeTable);
        var live = ReviewNamespaceRenameAnalysis(SafeMigrationObservedState.Different,
            targetExists: false, sourceSupported: false);

        // Act
        var analysis = ReviewNamespaceRename(projection, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
    }

    /// <summary>Removing an owned table retains known ordinary and constraint-backed index absence.</summary>
    /// <param name="kind">The owned physical index that disappears with the removed table.</param>
    [Theory]
    [InlineData("index")]
    [InlineData("primary")]
    [InlineData("unique")]
    public void SchemaGlobalIndexRenameUsesDroppedOwnerIndexProvenance(string kind)
    {
        // Arrange
        var projection = ReviewNamespaceProjection(completeTable: false);

        ReviewNamespaceCreateOccupant(projection, kind);
        ObserveAccepted(projection, new DropTableIntent("other_items"), SafeMigrationObservedState.Matching);

        var live = ReviewNamespaceRenameAnalysis(SafeMigrationObservedState.Different,
            targetExists: true, sourceSupported: true);

        // Act
        var analysis = ReviewNamespaceRename(projection, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
    }

    /// <summary>Source absence remains a replay NoOp even when the global destination is occupied.</summary>
    [Fact]
    public void SchemaGlobalIndexRenameChecksRemovedSourceBeforeDestination()
    {
        // Arrange
        var projection = ReviewNamespaceProjection(completeTable: false);

        ObserveAccepted(projection, new DropIndexIntent("source", "items"), SafeMigrationObservedState.Matching);
        ReviewNamespaceCreateOccupant(projection, "index");

        var live = ReviewNamespaceRenameAnalysis(SafeMigrationObservedState.Different,
            targetExists: true, sourceSupported: true);

        // Act
        var analysis = ReviewNamespaceRename(projection, live);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
    }

    /// <summary>Source-kind enrichment preserves every rename certificate without mutating the original.</summary>
    [Fact]
    public void IndexSourceEligibilityCopyPreservesDestinationAndPhysicalIdentity()
    {
        // Arrange
        var original = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None, false, "test_source_kind")
        {
            RenameTargetExists = true,
            RenameIntermediateTargetExists = false,
            MatchedObjectName = "source",
        };

        // Act
        var copy = original.WithRenameSourceSupported(false);

        // Assert
        Assert.NotSame(original, copy);
        Assert.Null(original.RenameSourceSupported);
        Assert.Equal(false, copy.RenameSourceSupported);
        Assert.Equal(original.RenameTargetExists, copy.RenameTargetExists);
        Assert.Equal(original.RenameIntermediateTargetExists, copy.RenameIntermediateTargetExists);
        Assert.Equal(original.MatchedObjectName, copy.MatchedObjectName);
        Assert.Equal(original.Code, copy.Code);
    }

    /// <summary>Creates a known ordinary source and a provider-scoped index namespace.</summary>
    private static SafeMigrationPreflightProjection ReviewNamespaceProjection(
        bool completeTable,
        bool schemaScoped = true,
        bool backingNames = true
    )
    {
        var projection = new SafeMigrationPreflightProjection(objectIdentityNormalizer:
            new ReviewIndexNamespaceNormalizer(schemaScoped, backingNames));

        if (completeTable)
        {
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("id")]),
                SafeMigrationTableMode.StrictDefinition));
        }

        ObserveAccepted(projection, new EnsureIndexIntent(new ExpectedIndexDefinition("source", "items",
            [new ExpectedIndexKeyDefinition(column: "id")])), SafeMigrationObservedState.Missing);

        return projection;
    }

    /// <summary>Creates one physical relation or nonrelation constraint with the requested target symbol.</summary>
    private static void ReviewNamespaceCreateOccupant(
        SafeMigrationPreflightProjection projection,
        string kind,
        string owner = "other_items",
        string? schema = null
    )
    {
        if (kind == "index")
        {
            ObserveAccepted(projection, new EnsureIndexIntent(new ExpectedIndexDefinition("target", owner,
                [new ExpectedIndexKeyDefinition(column: "id")], schema)), SafeMigrationObservedState.Missing);

            return;
        }

        var table = kind == "table" ? "target" : owner;
        var definition = new ExpectedTableDefinition(table, [Column("id")], schema,
            primaryKey: kind == "primary" ? new ExpectedPrimaryKeyDefinition("target", table, ["id"], schema) : null,
            uniqueConstraints: kind == "unique"
                ? [new ExpectedUniqueConstraintDefinition("target", table, ["id"], schema)] : [],
            checkConstraints: kind == "check"
                ? [ExpectedCheckConstraintDefinition.FromExpression("target", table, Positive("id"), schema)] : [],
            foreignKeys: kind == "foreign"
                ? [new ExpectedForeignKeyDefinition("target", table, ["id"], "parents", ["id"], schema)] : []);

        Apply(projection, new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition));
    }

    /// <summary>Projects the fixed ordinary source into the provider's exact destination namespace.</summary>
    private static SafeMigrationProviderAnalysis ReviewNamespaceRename(
        SafeMigrationPreflightProjection projection,
        SafeMigrationProviderAnalysis live
    ) => projection.Project(new SafeMigrationOperation(new RenameIndexIntent("source", "items", "target"),
        SafeMigrationPolicy.ThrowIfDifferent), live);

    /// <summary>Creates independent immutable destination and source-kind certificates.</summary>
    private static SafeMigrationProviderAnalysis ReviewNamespaceRenameAnalysis(
        SafeMigrationObservedState state,
        bool targetExists,
        bool? sourceSupported
    ) => new(state, SafeMigrationRepairCapability.None, false, "test_index_rename")
    {
        RenameTargetExists = targetExists,
        RenameSourceSupported = sourceSupported,
    };

    /// <summary>Models verified PostgreSQL or SQLite schema scope without broadening object identity.</summary>
    private sealed class ReviewIndexNamespaceNormalizer(bool schemaScoped = true, bool backingNames = true)
        : ISafeMigrationProviderObjectIdentityNormalizer
    {
        /// <inheritdoc />
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        /// <inheritdoc />
        public bool IndexUsesSchemaScopedIdentity => schemaScoped;

        /// <inheritdoc />
        public bool KeyConstraintsUseSchemaScopedBackingIndexNames => backingNames;

        /// <inheritdoc />
        public string NormalizeIdentifier(string identifier) => identifier;

        /// <inheritdoc />
        public string? NormalizeSchema(string? schema) => schema ?? "public";

        /// <inheritdoc />
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
    }
}
