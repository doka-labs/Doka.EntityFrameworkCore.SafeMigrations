namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Protects complete diagnostic-copy metadata, immutable evidence snapshots and monotonic live-proof requirements.
/// </summary>
public sealed class SafeMigrationProviderAnalysisTests
{
    /// <summary>
    /// Preserves every reflected analysis property and combines live-proof requirements monotonically during
    /// enrichment.
    /// </summary>
    /// <param name="existingProof">
    /// Whether the original analysis already requires fresh live-data proof.
    /// </param>
    /// <param name="additionalProof">
    /// Whether enrichment adds a fresh live-data-proof requirement.
    /// </param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DiagnosticEnrichmentPreservesAllMetadataAndNeverClearsRequiredProof(
        bool existingProof,
        bool additionalProof
    )
    {
        // Arrange
        var original = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.Safe,
            postconditionSatisfied: false,
            "unsupported_contract",
            SafeMigrationOperationalImpact.Unknown,
            [new SafeMigrationFacetDifference("column_max_length", "200", "100")])
        {
            RequiresLiveDataProof = existingProof,
            IsOpaqueProjectionUnknown = true,
            IsInvariantUnsupported = true,
            ProviderDeferredOriginOrdinal = 7,
            IndexPhysicalEnvironment = new SafeMigrationIndexPhysicalEnvironment(3072),
            MatchedObjectName = "physical_index",
            ModelManagedDataEvidence = new SafeMigrationModelManagedDataEvidence(
                [SafeMigrationModelManagedRowState.Target],
                [0]),
        };

        SafeMigrationFacetDifference[] differences =
        [
            new("column_nullability", "not_null", "nullable"),
        ];

        // Act
        var copy = original.WithDifferences(differences, additionalProof);

        // Assert
        Assert.NotSame(original, copy);
        Assert.Equal(existingProof || additionalProof, copy.RequiresLiveDataProof);
        Assert.Equal(existingProof, original.RequiresLiveDataProof);
        Assert.Equal(differences, copy.Differences);
        Assert.Equal("column_max_length",
            Assert.Single(original.Differences)
                .Facet);

        // WHY: Checking every property makes future analysis metadata part of
        // this preservation contract without another manually curated list.
        foreach (var property in typeof(SafeMigrationProviderAnalysis).GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (property.Name is nameof(SafeMigrationProviderAnalysis.Differences)
                or nameof(SafeMigrationProviderAnalysis.RequiresLiveDataProof))
            {
                continue;
            }

            Assert.Equal(property.GetValue(original), property.GetValue(copy));
        }
    }

    /// <summary>
    /// Snapshots replacement evidence while leaving the original analysis and its live-proof requirement unchanged.
    /// </summary>
    [Fact]
    public void DiagnosticEnrichmentSnapshotsInputWithoutMutatingTheOriginal()
    {
        // Arrange
        var originalDifference = new SafeMigrationFacetDifference("column_max_length", "200", "100");
        var replacement = new SafeMigrationFacetDifference("column_nullability", "not_null", "nullable");
        var original = new SafeMigrationProviderAnalysis(
            SafeMigrationObservedState.Different,
            SafeMigrationRepairCapability.None,
            postconditionSatisfied: false,
            "different",
            SafeMigrationOperationalImpact.NotApplicable,
            [originalDifference]);

        SafeMigrationFacetDifference[] differences = [replacement];

        // Act
        var copy = original.WithDifferences(differences, requiresLiveDataProof: true);
        differences[0] = originalDifference;

        // Assert
        Assert.Same(replacement, Assert.Single(copy.Differences));
        Assert.Same(originalDifference, Assert.Single(original.Differences));
        Assert.True(copy.RequiresLiveDataProof);
        Assert.False(original.RequiresLiveDataProof);
        Assert.True(((ICollection<SafeMigrationFacetDifference>)copy.Differences).IsReadOnly);
    }

    /// <summary>
    /// Accepts empty and exact-bound diagnostic collections without modifying the source analysis.
    /// </summary>
    /// <param name="count">
    /// The independently supplied bounded diagnostic evidence count.
    /// </param>
    [Theory]
    [InlineData(0)]
    [InlineData(SafeMigrationFacetDifference.MaximumDifferenceCount)]
    public void DiagnosticEnrichmentAcceptsBoundedEvidence(
        int count
    )
    {
        // Arrange
        var original = Analysis();
        var differences = Enumerable.Repeat(
            new SafeMigrationFacetDifference("column_max_length", "200", "100"),
            count);

        // Act
        var copy = original.WithDifferences(differences, requiresLiveDataProof: false);

        // Assert
        Assert.Equal(count, copy.Differences.Count);
        Assert.Empty(original.Differences);
    }

    /// <summary>
    /// Rejects a missing evidence collection and leaves the original analysis's differences empty.
    /// </summary>
    [Fact]
    public void DiagnosticEnrichmentRejectsNullEvidence()
    {
        // Arrange
        var original = Analysis();

        // Act
        var exception = Record.Exception(() =>
            original.WithDifferences(null!, requiresLiveDataProof: false));

        // Assert
        Assert.Equal("differences",
            Assert.IsType<ArgumentNullException>(exception)
                .ParamName);

        Assert.Empty(original.Differences);
    }

    /// <summary>
    /// Rejects null diagnostic entries rather than publishing an incomplete enriched analysis.
    /// </summary>
    [Fact]
    public void DiagnosticEnrichmentRejectsNullEntries()
    {
        // Arrange
        var original = Analysis();
        SafeMigrationFacetDifference[] differences = [null!];

        // Act
        var exception = Record.Exception(() =>
            original.WithDifferences(differences, requiresLiveDataProof: false));

        // Assert
        Assert.Equal("differences",
            Assert.IsType<ArgumentException>(exception)
                .ParamName);

        Assert.Empty(original.Differences);
    }

    /// <summary>
    /// Rejects evidence beyond the bounded diagnostic count without truncating or mutating the original analysis.
    /// </summary>
    [Fact]
    public void DiagnosticEnrichmentRejectsUnboundedEvidence()
    {
        // Arrange
        var original = Analysis();
        var differences = Enumerable.Repeat(
            new SafeMigrationFacetDifference("column_max_length", "200", "100"),
            SafeMigrationFacetDifference.MaximumDifferenceCount + 1);

        // Act
        var exception = Record.Exception(() =>
            original.WithDifferences(differences, requiresLiveDataProof: false));

        // Assert
        Assert.Equal("differences",
            Assert.IsType<ArgumentException>(exception)
                .ParamName);

        Assert.Empty(original.Differences);
    }

    private static SafeMigrationProviderAnalysis Analysis() => new(
        SafeMigrationObservedState.Different,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "different");
}
