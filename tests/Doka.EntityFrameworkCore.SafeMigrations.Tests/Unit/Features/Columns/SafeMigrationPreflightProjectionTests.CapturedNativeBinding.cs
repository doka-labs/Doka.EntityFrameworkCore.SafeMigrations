namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationPreflightProjectionTests
{
    /// <summary>Native folding changes captured unquoted roles in complete and sparse accepted column views.</summary>
    /// <param name="quoted">Whether the source identifier is explicitly quoted.</param>
    /// <param name="tableOwned">Whether the column is part of the complete authored table declaration.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NativeCapturedBindingsPreserveQuotedIdentityAndOriginalSource(bool quoted, bool tableOwned)
    {
        // Arrange
        var sql = quoted ? "\"Left_Bound\" + 1" : "Left_Bound + 1";
        var computed = CaptureProjectionComputed(sql);
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new CapturedNativeBindingNormalizer());

        ExpectedColumnDefinition[] columns = tableOwned
            ? [Column("left_bound"), Column("Left_Bound"), computed]
            : [Column("left_bound"), Column("Left_Bound")];

        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", columns),
            SafeMigrationTableMode.StrictDefinition));
        if (!tableOwned)
        {
            Apply(projection, new EnsureColumnIntent("items", computed));
        }

        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", computed),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var exists = ((ISafeMigrationProjectedColumnSource)projection)
            .TryGetProjectedColumn("items", null, "computed_value", out var stored);

        var replay = projection.Project(operation, Live(SafeMigrationObservedState.Missing));

        // Assert
        Assert.True(exists);
        Assert.NotNull(stored);
        var native = new HashSet<string>(StringComparer.Ordinal);
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(stored.ComputedExpression!, native);
        Assert.Equal(quoted ? "Left_Bound" : "left_bound", Assert.Single(native));
        Assert.Same(sql, stored.CapturedComputedSql);
        var original = new HashSet<string>(StringComparer.Ordinal);
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(computed.ComputedExpression!, original);
        Assert.Equal("Left_Bound", Assert.Single(original));
        Assert.Equal(SafeMigrationObservedState.Matching, replay.ObservedState);
    }

    /// <summary>A quoted mixed-case member cannot alias an unquoted source that natively resolves elsewhere.</summary>
    [Fact]
    public void NativeCapturedBindingDoesNotMergeQuotedAndUnquotedMixedCaseSources()
    {
        // Arrange
        var projection = new SafeMigrationPreflightProjection(
            objectIdentityNormalizer: new CapturedNativeBindingNormalizer());

        var quoted = CaptureProjectionComputed("\"Left_Bound\" + 1");
        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items",
            [Column("left_bound"), Column("Left_Bound"), quoted]), SafeMigrationTableMode.StrictDefinition));
        var target = CaptureProjectionComputed("Left_Bound + 1");
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", target),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analysis = projection.Project(operation, Live(SafeMigrationObservedState.Matching));

        // Assert
        Assert.Equal(SafeMigrationObservedState.Different, analysis.ObservedState);
    }

    /// <summary>Provider guards retain exact reference keys while Core uses native prerequisite roles.</summary>
    /// <param name="reject">Whether the exact captured proof explicitly rejects the CHECK operation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCapturedCheckViewsNeverReplaceProviderCallbackIdentity(bool reject)
    {
        // Arrange
        var guard = new CapturedReferenceGuard(reject);
        var projection = new SafeMigrationPreflightProjection(projectedDependencyAnalyzer: guard,
            objectIdentityNormalizer: new CapturedNativeBindingNormalizer());

        Apply(projection, new EnsureTableIntent(new ExpectedTableDefinition("items", [Column("left_bound")]),
            SafeMigrationTableMode.StrictDefinition));
        var check = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_items", "items", "Left_Bound > 0", null);
        var operation = new SafeMigrationOperation(new EnsureCheckConstraintIntent(check),
            SafeMigrationPolicy.ThrowIfDifferent);

        guard.Expected = operation;
        var live = Live(SafeMigrationObservedState.PrerequisiteMissing);

        // Act
        var analysis = projection.Project(operation, live);
        var decision = SafeMigrationDecisionPlanner.Plan(operation.Intent.Kind, analysis.ObservedState,
            operation.Policy, analysis.RepairCapability);

        projection.Observe(operation, live, analysis, decision);

        // Assert
        Assert.True(guard.CapturedReferenceRetained);
        Assert.True(guard.ProjectedReferenceRetained);
        Assert.Equal(!reject, guard.AcceptedReferenceRetained);
        Assert.Equal(reject ? SafeMigrationObservedState.Unsupported : SafeMigrationObservedState.Missing,
            analysis.ObservedState);
        Assert.Equal(reject ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.Apply, decision.Action);
    }

    private sealed class CapturedNativeBindingNormalizer : ISafeMigrationProviderObjectIdentityNormalizer
    {
        public StringComparer IdentifierComparer => StringComparer.Ordinal;
        public char? CapturedSqlIdentifierDelimiter => '"';
        public bool FoldUnquotedCapturedSqlIdentifiers => true;
        public string NormalizeIdentifier(string identifier) => identifier;
        public string? NormalizeSchema(string? schema) => schema;
        public bool IsObjectIdentityMismatch(SafeMigrationProviderAnalysis analysis) => false;
    }

    private sealed class CapturedReferenceGuard(bool reject) : ISafeMigrationProjectedDependencyAnalyzer
    {
        public SafeMigrationOperation? Expected { get; set; }
        public bool CapturedReferenceRetained { get; private set; }
        public bool ProjectedReferenceRetained { get; private set; }
        public bool AcceptedReferenceRetained { get; private set; }

        public SafeMigrationProviderAnalysis? ValidateCapturedProjection(SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis liveAnalysis, ISafeMigrationProjectedColumnSource columns)
        {
            if (operation.Intent is EnsureCheckConstraintIntent)
            {
                CapturedReferenceRetained = ReferenceEquals(operation, Expected);
            }

            return null;
        }

        public SafeMigrationProviderAnalysis ValidateProjectedOperation(SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis projectedAnalysis, ISafeMigrationProjectedColumnSource columns)
        {
            if (operation.Intent is not EnsureCheckConstraintIntent)
            {
                return projectedAnalysis;
            }

            ProjectedReferenceRetained = ReferenceEquals(operation, Expected)
                && ReferenceEquals(operation.Intent, Expected!.Intent);

            // WHY: This is intentionally reference-bound. Replacing the
            // provider's captured operation would skip its real rejection,
            // making this regression fail instead of silently passing.
            return reject && ProjectedReferenceRetained
                ? new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
                    SafeMigrationRepairCapability.None, false, "native_check_guard_rejected")
                : projectedAnalysis;
        }

        public void ObserveAcceptedOperation(SafeMigrationOperation operation,
            SafeMigrationProviderAnalysis liveAnalysis, SafeMigrationProviderAnalysis analysis,
            SafeMigrationDecision decision)
        {
            if (operation.Intent is EnsureCheckConstraintIntent)
            {
                AcceptedReferenceRetained = ReferenceEquals(operation, Expected);
            }
        }

        public void ObserveProviderOperation(MigrationOperation operation)
        {
        }
    }
}
