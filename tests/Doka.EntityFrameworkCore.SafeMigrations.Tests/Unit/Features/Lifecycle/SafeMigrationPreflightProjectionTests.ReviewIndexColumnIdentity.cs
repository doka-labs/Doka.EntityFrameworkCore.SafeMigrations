namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Typed key/include casing follows the provider column comparer in complete and sparse views.</summary>
    /// <param name="complete">Whether the owner has a complete accepted creation definition.</param>
    /// <param name="insensitive">Whether column member identity is explicitly case-insensitive.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProjectedIndexTypedColumnCasingRequiresProviderOptIn(bool complete, bool insensitive)
    {
        // Arrange
        var projection = ReviewIndexColumnProjection(complete, insensitive);
        var original = new ExpectedIndexDefinition("IX_Code", "items",
            [new ExpectedIndexKeyDefinition("Code")], includedColumns: ["Payload"]);

        ObserveAccepted(projection, new EnsureIndexIntent(original), SafeMigrationObservedState.Missing);
        var requested = new EnsureIndexIntent(new ExpectedIndexDefinition("ix_code", "items",
            [new ExpectedIndexKeyDefinition("code")], includedColumns: ["payload"]));

        // Act
        var analysis = Project(projection, requested, SafeMigrationObservedState.Different);
        var decision = SafeMigrationDecisionPlanner.Plan(requested.Kind, analysis.ObservedState,
            SafeMigrationPolicy.ThrowIfDifferent);

        // Assert
        Assert.Equal(insensitive ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            analysis.ObservedState);
        Assert.Equal(insensitive ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent, decision.Action);
    }

    /// <summary>Semantic alias lookup hashes typed key/include members with the same comparer as equality.</summary>
    /// <param name="complete">Whether the owner has complete rather than sparse physical evidence.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectedIndexColumnAliasesShareConsistentSemanticHash(bool complete)
    {
        // Arrange
        var projection = ReviewIndexColumnProjection(complete, insensitive: true);
        ObserveAccepted(projection, new EnsureIndexIntent(new ExpectedIndexDefinition("physical_key", "items",
            [new ExpectedIndexKeyDefinition("Code")], includedColumns: ["Payload"])),
            SafeMigrationObservedState.Missing);

        var alias = new EnsureIndexIntent(new ExpectedIndexDefinition("logical_alias", "items",
            [new ExpectedIndexKeyDefinition("code")], includedColumns: ["payload"]));

        // Act
        var analysis = Project(projection, alias, SafeMigrationObservedState.Missing);

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, analysis.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, SafeMigrationDecisionPlanner.Plan(alias.Kind,
            analysis.ObservedState, SafeMigrationPolicy.ThrowIfDifferent).Action);
    }

    /// <summary>Shared UNIQUE/index physical maps compare typed key columns consistently in both directions.</summary>
    /// <param name="complete">Whether the owner has a complete accepted definition.</param>
    /// <param name="constraintFirst">Whether a UNIQUE constraint supplies the original index shape.</param>
    /// <param name="insensitive">Whether column identity explicitly folds case.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void SharedUniqueIndexColumnCasingUsesOnePhysicalContract(
        bool complete,
        bool constraintFirst,
        bool insensitive
    )
    {
        // Arrange
        var projection = ReviewIndexColumnProjection(complete, insensitive, shared: true);
        SafeMigrationIntent original = constraintFirst
            ? new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("physical_key", "items", ["Code"]))
            : new EnsureIndexIntent(new ExpectedIndexDefinition("physical_key", "items",
                [new ExpectedIndexKeyDefinition("Code")], unique: true));

        SafeMigrationIntent requested = constraintFirst
            ? new EnsureIndexIntent(new ExpectedIndexDefinition("physical_key", "items",
                [new ExpectedIndexKeyDefinition("code")], unique: true))
            : new EnsureUniqueConstraintIntent(
                new ExpectedUniqueConstraintDefinition("physical_key", "items", ["code"]));

        ObserveAccepted(projection, original, SafeMigrationObservedState.Missing);

        // Act
        var analysis = Project(projection, requested, SafeMigrationObservedState.Different);

        // Assert
        Assert.Equal(insensitive ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            analysis.ObservedState);
    }

    /// <summary>Column folding cannot change SQL, collation identities, order or owner boundaries.</summary>
    /// <param name="facet">The exact semantic facet that differs despite typed column casing opt-in.</param>
    [Theory]
    [InlineData("expression")]
    [InlineData("structured_expression")]
    [InlineData("collation")]
    [InlineData("filter")]
    [InlineData("operator_class")]
    [InlineData("method")]
    [InlineData("order")]
    [InlineData("owner")]
    [InlineData("schema")]
    public void ColumnAwareIndexSemanticHashPreservesNonColumnFacets(string facet)
    {
        // Arrange
        var comparer = SafeMigrationSemanticDefinitionComparers.IndexForColumns(StringComparer.OrdinalIgnoreCase);
        var baseline = ReviewIndexColumnFacet(facet, changed: false);
        var changed = ReviewIndexColumnFacet(facet, changed: true);
        var accepted = new HashSet<ExpectedIndexDefinition>(comparer) { baseline, };

        // Act
        var equivalent = comparer.Equals(baseline, changed);
        var present = accepted.Contains(changed);

        // Assert
        Assert.False(equivalent);
        Assert.False(present);
    }

    /// <summary>Case-only typed members remain equal and hash-identical without folding SQL symbols.</summary>
    [Fact]
    public void ColumnAwareIndexSemanticHashMatchesTypedColumnEquality()
    {
        // Arrange
        var comparer = SafeMigrationSemanticDefinitionComparers.IndexForColumns(StringComparer.OrdinalIgnoreCase);
        var original = new ExpectedIndexDefinition("physical", "Items",
            [new ExpectedIndexKeyDefinition("Code")], "Owner", includedColumns: ["Payload"]);

        var alias = new ExpectedIndexDefinition("alias", "Items",
            [new ExpectedIndexKeyDefinition("code")], "Owner", includedColumns: ["payload"]);

        var accepted = new HashSet<ExpectedIndexDefinition>(comparer) { original, };

        // Act
        var equal = comparer.Equals(original, alias);
        var sameHash = comparer.GetHashCode(original) == comparer.GetHashCode(alias);
        var present = accepted.Contains(alias);

        // Assert
        Assert.True(equal);
        Assert.True(sameHash);
        Assert.True(present);
    }

    /// <summary>Shared candidate-key aliases use column-aware hashing without losing column order.</summary>
    /// <param name="reordered">Whether the requested key reverses its member order.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColumnAwareUniqueSemanticHashPreservesOrderedTypedMembers(bool reordered)
    {
        // Arrange
        var comparer = SafeMigrationSemanticDefinitionComparers.UniqueForColumns(StringComparer.OrdinalIgnoreCase);
        var original = new ExpectedUniqueConstraintDefinition("physical", "Items", ["Code", "Payload"], "Owner");
        var alias = new ExpectedUniqueConstraintDefinition("alias", "Items",
            reordered ? ["payload", "code"] : ["code", "payload"], "Owner");

        var accepted = new HashSet<ExpectedUniqueConstraintDefinition>(comparer) { original, };

        // Act
        var equal = comparer.Equals(original, alias);
        var present = accepted.Contains(alias);

        // Assert
        Assert.Equal(!reordered, equal);
        Assert.Equal(!reordered, present);
    }

    /// <summary>Constructs current owner proof without inventing matching indexes or row-safety capabilities.</summary>
    private static SafeMigrationPreflightProjection ReviewIndexColumnProjection(
        bool complete,
        bool insensitive,
        bool shared = false
    )
    {
        var projection = new SafeMigrationPreflightProjection(
            projectedKeyAnalyzer: shared ? new SharedPhysicalKeyAnalyzer() : null,
            objectIdentityNormalizer: new ReviewIndexMemberNormalizer(
                insensitiveIndexes: true, insensitiveColumns: insensitive));

        if (complete)
        {
            Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items",
                [Column("Code"), Column("Payload")]), SafeMigrationTableMode.StrictDefinition));
        }
        else
        {
            ObserveColumnsOnExistingTable(projection, "items", Column("Code"), Column("Payload"));
        }

        return projection;
    }

    /// <summary>Varies one non-column facet while keeping the same typed member contract.</summary>
    private static ExpectedIndexDefinition ReviewIndexColumnFacet(string facet, bool changed)
    {
        ExpectedIndexKeyDefinition[] keys = facet switch
        {
            "expression" => [new ExpectedIndexKeyDefinition(expression: changed ? "lower(Code)" : "LOWER(Code)")],
            "structured_expression" => [new ExpectedIndexKeyDefinition(
                structuredExpression: new SafeMigrationSqlIdentifierExpression([changed ? "code" : "Code"]))],
            "collation" => [new ExpectedIndexKeyDefinition("Code",
                collation: new SafeMigrationCollationIdentifier(changed ? "binary" : "Binary"))],
            "operator_class" => [new ExpectedIndexKeyDefinition("Code",
                operatorClass: changed ? "text_ops" : "TEXT_OPS")],
            "order" => changed
                ? [new ExpectedIndexKeyDefinition("Payload"), new ExpectedIndexKeyDefinition("Code")]
                : [new ExpectedIndexKeyDefinition("Code"), new ExpectedIndexKeyDefinition("Payload")],
            _ => [new ExpectedIndexKeyDefinition("Code")],
        };

        return new ExpectedIndexDefinition("physical", facet == "owner" && changed ? "items" : "Items", keys,
            facet == "schema" && changed ? "owner" : "Owner",
            filter: facet == "filter" ? changed ? "code IS NOT NULL" : "Code IS NOT NULL" : null,
            method: facet == "method" ? changed ? "btree" : "BTREE" : null);
    }
}
