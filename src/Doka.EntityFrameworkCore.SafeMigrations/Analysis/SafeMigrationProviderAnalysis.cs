namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Contains one provider's immutable live-state classification.</summary>
public sealed class SafeMigrationProviderAnalysis
{
    private bool _requiresLiveDataProof;

    /// <summary>Initializes a provider analysis.</summary>
    /// <param name="observedState">The provider-classified live state.</param>
    /// <param name="repairCapability">The provider-proven repair capability.</param>
    /// <param name="postconditionSatisfied">Whether the operation's final target condition currently holds.</param>
    /// <param name="code">The stable low-cardinality result code.</param>
    public SafeMigrationProviderAnalysis(
        SafeMigrationObservedState observedState,
        SafeMigrationRepairCapability repairCapability,
        bool postconditionSatisfied,
        string code
    ) : this(
        observedState,
        repairCapability,
        postconditionSatisfied,
        code,
        SafeMigrationOperationalImpact.NotApplicable,
        differences: null)
    {
    }

    /// <summary>Initializes a provider analysis with bounded diagnostic evidence.</summary>
    /// <param name="observedState">The provider-classified live state.</param>
    /// <param name="repairCapability">The provider-proven repair capability.</param>
    /// <param name="postconditionSatisfied">Whether the operation's final target condition currently holds.</param>
    /// <param name="code">The stable low-cardinality result code.</param>
    /// <param name="operationalImpact">The provider-proven execution-impact classification.</param>
    /// <param name="differences">The bounded typed facet differences.</param>
    public SafeMigrationProviderAnalysis(
        SafeMigrationObservedState observedState,
        SafeMigrationRepairCapability repairCapability,
        bool postconditionSatisfied,
        string code,
        SafeMigrationOperationalImpact operationalImpact,
        IEnumerable<SafeMigrationFacetDifference>? differences
    )
    {
        if (!Enum.IsDefined(observedState))
        {
            throw new ArgumentOutOfRangeException(nameof(observedState));
        }

        if (!Enum.IsDefined(repairCapability))
        {
            throw new ArgumentOutOfRangeException(nameof(repairCapability));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (!Enum.IsDefined(operationalImpact))
        {
            throw new ArgumentOutOfRangeException(nameof(operationalImpact));
        }

        ObservedState = observedState;
        RepairCapability = repairCapability;
        PostconditionSatisfied = postconditionSatisfied;
        Code = code;
        OperationalImpact = operationalImpact;
        Differences = SnapshotDifferences(differences ?? []);
    }

    /// <summary>Gets the classified live state.</summary>
    public SafeMigrationObservedState ObservedState { get; }

    /// <summary>Gets the proven repair capability.</summary>
    public SafeMigrationRepairCapability RepairCapability { get; }

    /// <summary>Gets whether the final target condition currently holds.</summary>
    public bool PostconditionSatisfied { get; }

    /// <summary>Gets a stable, low-cardinality provider code.</summary>
    public string Code { get; }

    /// <summary>Gets the provider-proven automatic-repair impact classification.</summary>
    public SafeMigrationOperationalImpact OperationalImpact { get; }

    /// <summary>Gets the bounded typed facet differences.</summary>
    public IReadOnlyList<SafeMigrationFacetDifference> Differences { get; private set; }

    /// <summary>Gets whether accepted projection still requires a live row-safety proof.</summary>
    internal bool RequiresLiveDataProof
    {
        get => _requiresLiveDataProof;
        init => _requiresLiveDataProof = value;
    }

    /// <summary>Gets whether opaque earlier operations prevent read-only state projection.</summary>
    internal bool IsOpaqueProjectionUnknown { get; init; }

    /// <summary>Gets whether preceding operations cannot repair this unsupported contract.</summary>
    internal bool IsInvariantUnsupported { get; init; }

    /// <summary>Gets captured physical limits and column shapes for projected key validation.</summary>
    internal SafeMigrationIndexPhysicalEnvironment? IndexPhysicalEnvironment { get; init; }

    /// <summary>Gets the resolved physical name behind an exact or semantic match.</summary>
    internal string? MatchedObjectName { get; init; }

    /// <summary>Gets captured row and dependency evidence for model-managed data projection.</summary>
    internal SafeMigrationModelManagedDataEvidence? ModelManagedDataEvidence { get; init; }

    /// <summary>Copies the analysis with replacement diagnostics while preserving every other observation.</summary>
    /// <param name="differences">The bounded diagnostic evidence to snapshot.</param>
    /// <param name="requiresLiveDataProof">Whether enrichment adds a live-data-proof requirement.</param>
    /// <returns>A separate analysis that retains all metadata and any existing proof requirement.</returns>
    internal SafeMigrationProviderAnalysis WithDifferences(
        IEnumerable<SafeMigrationFacetDifference> differences,
        bool requiresLiveDataProof
    )
    {
        ArgumentNullException.ThrowIfNull(differences);

        var differenceSnapshot = SnapshotDifferences(differences);

        // WHY: Diagnostic enrichment must preserve every provider observation,
        // including future metadata. A field-wise copy avoids a fragile manual
        // member list; only the bounded diagnostics and proof flag are changed.
        var copy = (SafeMigrationProviderAnalysis)MemberwiseClone();

        copy.Differences = differenceSnapshot;
        copy._requiresLiveDataProof |= requiresLiveDataProof;

        return copy;
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<SafeMigrationFacetDifference> SnapshotDifferences(
        IEnumerable<SafeMigrationFacetDifference> differences
    )
    {
        var snapshot = differences.ToArray();
        if (snapshot.Any(static difference => difference is null))
        {
            throw new ArgumentException("Differences cannot contain null values.", nameof(differences));
        }

        if (snapshot.Length > SafeMigrationFacetDifference.MaximumDifferenceCount)
        {
            throw new ArgumentException(
                "An analysis cannot contain more than "
                + $"{SafeMigrationFacetDifference.MaximumDifferenceCount} differences.",
                nameof(differences));
        }

        return Array.AsReadOnly(snapshot);
    }
}
