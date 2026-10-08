namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationProviderAnalysisTests
{
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

        var copy = original.WithDifferences(differences, additionalProof);

        Assert.NotSame(original, copy);
        Assert.Equal(existingProof || additionalProof, copy.RequiresLiveDataProof);
        Assert.Equal(existingProof, original.RequiresLiveDataProof);
        Assert.Equal(differences, copy.Differences);
        Assert.Equal("column_max_length", Assert.Single(original.Differences).Facet);

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

    [Fact]
    public void DiagnosticEnrichmentSnapshotsInputWithoutMutatingTheOriginal()
    {
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

        var copy = original.WithDifferences(differences, requiresLiveDataProof: true);
        differences[0] = originalDifference;

        Assert.Same(replacement, Assert.Single(copy.Differences));
        Assert.Same(originalDifference, Assert.Single(original.Differences));
        Assert.True(copy.RequiresLiveDataProof);
        Assert.False(original.RequiresLiveDataProof);
        Assert.True(((ICollection<SafeMigrationFacetDifference>)copy.Differences).IsReadOnly);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SafeMigrationFacetDifference.MaximumDifferenceCount)]
    public void DiagnosticEnrichmentAcceptsBoundedEvidence(
        int count
    )
    {
        var original = Analysis();
        var differences = Enumerable.Repeat(
            new SafeMigrationFacetDifference("column_max_length", "200", "100"),
            count);

        var copy = original.WithDifferences(differences, requiresLiveDataProof: false);

        Assert.Equal(count, copy.Differences.Count);
        Assert.Empty(original.Differences);
    }

    [Fact]
    public void DiagnosticEnrichmentRejectsNullEvidence()
    {
        var original = Analysis();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            original.WithDifferences(null!, requiresLiveDataProof: false));

        Assert.Equal("differences", exception.ParamName);
        Assert.Empty(original.Differences);
    }

    [Fact]
    public void DiagnosticEnrichmentRejectsNullEntries()
    {
        var original = Analysis();
        SafeMigrationFacetDifference[] differences = [null!];

        var exception = Assert.Throws<ArgumentException>(() =>
            original.WithDifferences(differences, requiresLiveDataProof: false));

        Assert.Equal("differences", exception.ParamName);
        Assert.Empty(original.Differences);
    }

    [Fact]
    public void DiagnosticEnrichmentRejectsUnboundedEvidence()
    {
        var original = Analysis();
        var differences = Enumerable.Repeat(
            new SafeMigrationFacetDifference("column_max_length", "200", "100"),
            SafeMigrationFacetDifference.MaximumDifferenceCount + 1);

        var exception = Assert.Throws<ArgumentException>(() =>
            original.WithDifferences(differences, requiresLiveDataProof: false));

        Assert.Equal("differences", exception.ParamName);
        Assert.Empty(original.Differences);
    }

    private static SafeMigrationProviderAnalysis Analysis() => new(
        SafeMigrationObservedState.Different,
        SafeMigrationRepairCapability.None,
        postconditionSatisfied: false,
        "different");
}
