namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    private const string CapturedCheckSource = "leftbound >= 0 AND COALESCE(1e0, 2) = 1e0 "
        + "AND 'KeepCase' = 'KeepCase'";

    /// <summary>
    /// A source-preserving rename can reassert native quoted or bare CHECK members, not other SQL roles.
    /// </summary>
    /// <param name="newlyCreated">Whether the prefix owns a complete fresh table or a matching existing shape.</param>
    /// <param name="targetSql">The complete authored CHECK source after the rename.</param>
    /// <param name="expected">The independently required structural state.</param>
    [Theory]
    [InlineData(true, "score >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'",
        SafeMigrationObservedState.Matching)]
    [InlineData(false, "score >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'",
        SafeMigrationObservedState.Matching)]
    [InlineData(true, "SCORE >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'",
        SafeMigrationObservedState.Matching)]
    [InlineData(false, "SCORE >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'",
        SafeMigrationObservedState.Matching)]
    [InlineData(true, "score >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'keepcase' = 'KeepCase'",
        SafeMigrationObservedState.Different)]
    [InlineData(false, "score >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'keepcase' = 'KeepCase'",
        SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(true, "score >= 0 AND COALESCE(1.0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'",
        SafeMigrationObservedState.Different)]
    [InlineData(false, "score >= 0 AND COALESCE(1.0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'",
        SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(true, "score >= 0 AND coalesce(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'",
        SafeMigrationObservedState.Different)]
    [InlineData(false, "score >= 0 AND coalesce(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'",
        SafeMigrationObservedState.PrerequisiteMissing)]
    [InlineData(true, "score > 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'",
        SafeMigrationObservedState.Different)]
    [InlineData(false, "score > 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'",
        SafeMigrationObservedState.PrerequisiteMissing)]
    public void CapturedCheckReassertionPreservesEveryOtherSourceRole(
        bool newlyCreated,
        string targetSql,
        SafeMigrationObservedState expected)
    {
        // Arrange
        var projection = CapturedCheckTargetProjection(newlyCreated, new CapturedCheckTargetIdentity());
        var target = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items", targetSql, null);
        var operation = new SafeMigrationOperation(new EnsureCheckConstraintIntent(target),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(expected, analysis.ObservedState);
    }

    /// <summary>Explicit raw CHECK constructors retain their opaque boundary after a captured source rename.</summary>
    [Fact]
    public void CapturedCheckReassertionDoesNotApproveExplicitRawSql()
    {
        // Arrange
        var projection = CapturedCheckTargetProjection(true, new CapturedCheckTargetIdentity());
        var target = new ExpectedCheckConstraintDefinition("ck_items", "items",
            "score >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'");

        var operation = new SafeMigrationOperation(new EnsureCheckConstraintIntent(target),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }

    /// <summary>
    /// Quoted and unquoted members cannot be conflated beyond the provider's certified column identity.
    /// </summary>
    [Fact]
    public void CapturedCheckReassertionKeepsCaseSensitiveColumnIdentity()
    {
        // Arrange
        var projection = CapturedCheckTargetProjection(true, new CapturedCheckTargetIdentity
        {
            ColumnIdentifierComparer = StringComparer.Ordinal,
        });

        var target = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items",
            "SCORE >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'", null);

        var operation = new SafeMigrationOperation(new EnsureCheckConstraintIntent(target),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }

    /// <summary>Native double-quote CHECK identities retain the separately certified column-case policy.</summary>
    /// <param name="caseInsensitive">Whether uppercase and lowercase native members identify one column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedCheckReassertionKeepsNativeDoubleQuoteColumnPolicy(bool caseInsensitive)
    {
        // Arrange
        var projection = CapturedCheckTargetProjection(true, new CapturedCheckTargetIdentity
        {
            CapturedSqlIdentifierDelimiter = '"',
            ColumnIdentifierComparer = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal,
        });

        var target = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items",
            "SCORE >= 0 AND COALESCE(1e0, 2) = 1e0 AND 'KeepCase' = 'KeepCase'", null);

        var operation = new SafeMigrationOperation(new EnsureCheckConstraintIntent(target),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.Equal(caseInsensitive ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            analysis.ObservedState);
    }

    /// <summary>Projection-local native quote certificates never change exact public contract identity.</summary>
    [Fact]
    public void CapturedCheckReassertionDoesNotRelaxGlobalSourceIdentity()
    {
        // Arrange
        var quoted = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items", "`score` >= 0", null);
        var bare = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items", "score >= 0", null);

        // Act
        var equal = SafeMigrationDefinitionEquivalence.CheckConstraint(quoted, bare);
        var semanticallyEqual = SafeMigrationSemanticDefinitionComparers.CheckConstraint.Equals(quoted, bare);

        // Assert
        Assert.False(equal);
        Assert.False(semanticallyEqual);
    }

    private static SafeMigrationPreflightProjection CapturedCheckTargetProjection(
        bool newlyCreated,
        CapturedCheckTargetIdentity normalizer)
    {
        var projection = new SafeMigrationPreflightProjection(new RenameAbsenceProjection(),
            objectIdentityNormalizer: normalizer);

        var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_items", "items", CapturedCheckSource, null);

        var table = new ExpectedTableDefinition("items", [Column("leftbound")], checkConstraints: [check]);
        if (newlyCreated)
        {
            Apply(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition));
        }
        else
        {
            ObserveAccepted(projection, new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
                SafeMigrationObservedState.Matching);
        }

        Apply(projection, new RenameColumnIntent("leftbound", "items", "score"));

        return projection;
    }

    private sealed class CapturedCheckTargetIdentity : ISafeMigrationProviderObjectIdentityNormalizer
    {
        public StringComparer IdentifierComparer => StringComparer.Ordinal;

        public StringComparer ColumnIdentifierComparer { get; init; } = StringComparer.OrdinalIgnoreCase;

        public char? CapturedSqlIdentifierDelimiter { get; init; } = '`';

        public string NormalizeIdentifier(string identifier) => identifier;

        public string? NormalizeSchema(string? schema) => schema;

        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
    }
}
