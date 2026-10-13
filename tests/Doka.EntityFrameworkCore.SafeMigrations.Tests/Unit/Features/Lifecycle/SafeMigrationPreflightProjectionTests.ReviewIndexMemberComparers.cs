namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Index-only casing shares tombstones without changing another provider's identity rules.</summary>
    /// <param name="insensitive">Whether the provider explicitly opts into CI index member names.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexMemberComparerControlsDropAndRenameLifetime(bool insensitive)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ReviewIndexMemberNormalizer(insensitive));

        ObserveAccepted(projection, new DropIndexIntent("IX_Code", "items"), SafeMigrationObservedState.Matching);
        var rename = new SafeMigrationOperation(new RenameIndexIntent("ix_code", "items", "ix_target"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var live = Live(SafeMigrationObservedState.Matching).WithRenameSourceSupported(true);

        // Act
        var repeatedDrop = Project(projection, new DropIndexIntent("ix_code", "items"),
            SafeMigrationObservedState.Matching);

        var renamed = projection.Project(rename, live);

        // Assert
        Assert.Equal(insensitive ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching,
            repeatedDrop.ObservedState);
        Assert.Equal(insensitive ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Matching,
            renamed.ObservedState);
    }

    /// <summary>Index member folding cannot transfer a physical drop to a case-distinct table owner.</summary>
    [Fact]
    public void IndexMemberComparerRetainsExactTableOwnerIdentity()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ReviewIndexMemberNormalizer(insensitiveIndexes: true));

        ObserveAccepted(projection, new DropIndexIntent("IX_Code", "Items"), SafeMigrationObservedState.Matching);

        // Act
        var own = Project(projection, new DropIndexIntent("ix_code", "Items"), SafeMigrationObservedState.Matching);
        var other = Project(projection, new DropIndexIntent("ix_code", "items"), SafeMigrationObservedState.Matching);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, own.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, other.ObservedState);
    }

    /// <summary>Shared UNIQUE/index aliases consume the same CI removal certificate in both directions.</summary>
    /// <param name="dropConstraint">Whether the removal uses the constraint rather than index API.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedUniqueIndexMemberComparerInvalidatesCrossKindAliases(bool dropConstraint)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(projectedKeyAnalyzer: new SharedPhysicalKeyAnalyzer(),
            objectIdentityNormalizer: new ReviewIndexMemberNormalizer(insensitiveIndexes: true));

        ObserveColumnsOnExistingTable(projection, "items", Column("Code"));
        SafeMigrationIntent accepted = dropConstraint
            ? new EnsureIndexIntent(new ExpectedIndexDefinition("ix_alias", "items",
                [new ExpectedIndexKeyDefinition("Code")], unique: true))
            : new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("uq_alias", "items", ["Code"]));

        SafeMigrationIntent drop = dropConstraint
            ? new DropUniqueConstraintIntent("ux_code", "items")
            : new DropIndexIntent("ux_code", "items");

        SafeMigrationIntent replacement = dropConstraint
            ? new EnsureUniqueConstraintIntent(new ExpectedUniqueConstraintDefinition("uq_next", "items", ["Code"]))
            : new EnsureIndexIntent(new ExpectedIndexDefinition("ix_next", "items",
                [new ExpectedIndexKeyDefinition("Code")], unique: true));

        ObserveAccepted(projection, accepted, SafeMigrationObservedState.Matching, "UX_Code");
        ObserveAccepted(projection, drop, SafeMigrationObservedState.Matching);

        // Act
        var analysis = projection.Project(new SafeMigrationOperation(replacement, SafeMigrationPolicy.ThrowIfDifferent),
            Live(SafeMigrationObservedState.Matching, "UX_CODE"));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, SafeMigrationDecisionPlanner.Plan(replacement.Kind,
            analysis.ObservedState, SafeMigrationPolicy.ThrowIfDifferent).Action);
    }

    /// <summary>Candidate-key membership follows column-specific identity rather than table-name comparison.</summary>
    /// <param name="insensitiveColumns">Whether the provider opts into CI column identities.</param>
    /// <param name="differentColumn">Whether the principal request names a genuinely different column.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CandidateKeySameColumnsUsesColumnComparer(bool insensitiveColumns, bool differentColumn)
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new ReviewIndexMemberNormalizer(insensitiveColumns: insensitiveColumns));

        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("parents", [Column("Id")]),
            SafeMigrationTableMode.StrictDefinition));
        ObserveAccepted(projection, new EnsurePrimaryKeyIntent(
            new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["Id"])), SafeMigrationObservedState.Missing);
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("children", [NullableColumn("ParentId")]),
            SafeMigrationTableMode.StrictDefinition));
        var intent = new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition("fk_case", "children", ["ParentId"],
            "parents", [differentColumn ? "MissingId" : "ID"]));

        // Act
        var analysis = Project(projection, intent, SafeMigrationObservedState.PrerequisiteMissing);

        // Assert
        Assert.Equal(insensitiveColumns && !differentColumn ? SafeMigrationObservedState.Missing
            : SafeMigrationObservedState.PrerequisiteMissing, analysis.ObservedState);
    }

    /// <summary>Isolates physical owner identity from explicitly opted-in native member comparisons.</summary>
    private sealed class ReviewIndexMemberNormalizer(
        bool insensitiveIndexes = false,
        bool insensitiveColumns = false
    ) : ISafeMigrationProviderObjectIdentityNormalizer
    {
        /// <inheritdoc />
        public StringComparer IdentifierComparer => StringComparer.Ordinal;
        /// <inheritdoc />
        public StringComparer IndexIdentifierComparer =>
            insensitiveIndexes ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        /// <inheritdoc />
        public StringComparer ColumnIdentifierComparer =>
            insensitiveColumns ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        /// <inheritdoc />
        public string NormalizeIdentifier(string identifier) => identifier;
        /// <inheritdoc />
        public string? NormalizeSchema(string? schema) => schema;
        /// <inheritdoc />
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
    }
}
