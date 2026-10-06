namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Contains one provider's immutable live-state classification.</summary>
public sealed class SafeMigrationProviderAnalysis
{
    private bool _requiresLiveDataProof;
    private bool _repairPreservesLiveValueDomain;
    private SafeMigrationIndexPhysicalEnvironment? _indexPhysicalEnvironment;
    private bool? _renameTargetExists;
    private bool? _renameIntermediateTargetExists;
    private string? _renameApplyUnsupportedCode;

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

    /// <summary>Gets whether accepting this repair can mutate rows and fire table triggers.</summary>
    internal bool RepairMutatesData { get; init; }

    /// <summary>
    /// Gets whether this exact same-shape repair remains valid after only unrelated safe column drops.
    /// The provider must validate its rebuild dependencies; Core still owns source and row-proof freshness.
    /// </summary>
    internal bool CanReuseAfterUnrelatedColumnDrops { get; init; }

    /// <summary>Gets whether repair preserves every existing value without a backfill or lossy conversion.</summary>
    internal bool RepairPreservesLiveValueDomain
    {
        get => _repairPreservesLiveValueDomain;
        init => _repairPreservesLiveValueDomain = value;
    }

    /// <summary>Refines a value-domain certificate after exact live facet diagnostics are available.</summary>
    /// <param name="preserves">Whether the complete provider evidence certifies preservation.</param>
    /// <returns>A copy retaining the existing evidence and refined certificate.</returns>
    internal SafeMigrationProviderAnalysis WithRepairPreservesLiveValueDomain(bool preserves)
    {
        var copy = (SafeMigrationProviderAnalysis)MemberwiseClone();

        copy._repairPreservesLiveValueDomain = preserves;

        return copy;
    }

    /// <summary>Gets whether the provider proved that repair preserves existing physical key definitions.</summary>
    internal bool RepairPreservesPhysicalKeys { get; private set; }

    /// <summary>Certifies key preservation after complete provider physical validation.</summary>
    /// <returns>A copy retaining all existing evidence and the physical-key preservation certificate.</returns>
    internal SafeMigrationProviderAnalysis WithRepairPreservesPhysicalKeys()
    {
        var copy = (SafeMigrationProviderAnalysis)MemberwiseClone();

        copy.RepairPreservesPhysicalKeys = true;

        return copy;
    }

    /// <summary>Gets independent destination presence for a rename, including when its source is absent.</summary>
    internal bool? RenameTargetExists
    {
        get => _renameTargetExists;
        init => _renameTargetExists = value;
    }

    /// <summary>Gets presence of the source-schema target name used by staged cross-schema renames.</summary>
    internal bool? RenameIntermediateTargetExists
    {
        get => _renameIntermediateTargetExists;
        init => _renameIntermediateTargetExists = value;
    }

    /// <summary>Gets a provider capability rejection that applies only when a rename must execute.</summary>
    internal string? RenameApplyUnsupportedCode
    {
        get => _renameApplyUnsupportedCode;
        init => _renameApplyUnsupportedCode = value;
    }

    /// <summary>Attaches independently captured destination presence without changing the source analysis.</summary>
    /// <param name="exists">Whether the immutable catalog contains the rename destination.</param>
    /// <param name="intermediateExists">Whether a staged cross-schema rename's intermediate name is occupied.</param>
    /// <param name="applyUnsupportedCode">The capability rejection when a source must actually be renamed.</param>
    /// <returns>A separate analysis preserving every other captured observation.</returns>
    internal SafeMigrationProviderAnalysis WithRenameTargetExists(
        bool exists,
        bool? intermediateExists = null,
        string? applyUnsupportedCode = null
    )
    {
        var copy = (SafeMigrationProviderAnalysis)MemberwiseClone();

        copy._renameTargetExists = exists;
        copy._renameIntermediateTargetExists = intermediateExists;
        copy._renameApplyUnsupportedCode = applyUnsupportedCode;

        return copy;
    }

    /// <summary>Gets captured physical limits and column shapes for projected key validation.</summary>
    internal SafeMigrationIndexPhysicalEnvironment? IndexPhysicalEnvironment
    {
        get => _indexPhysicalEnvironment;
        init => _indexPhysicalEnvironment = value;
    }

    /// <summary>Rebinds key validation to the storage environment of an accepted newly created table.</summary>
    /// <param name="environment">The captured creation environment.</param>
    /// <returns>A copy retaining the existing evidence and selected physical environment.</returns>
    internal SafeMigrationProviderAnalysis WithIndexPhysicalEnvironment(
        SafeMigrationIndexPhysicalEnvironment environment
    )
    {
        var copy = (SafeMigrationProviderAnalysis)MemberwiseClone();

        copy._indexPhysicalEnvironment = environment;

        return copy;
    }

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
