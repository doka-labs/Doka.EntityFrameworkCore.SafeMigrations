namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Contains one provider's immutable live-state classification.</summary>
public sealed class SafeMigrationProviderAnalysis
{
    private bool _requiresLiveDataProof;
    private bool _repairPreservesLiveValueDomain;
    private SafeMigrationIndexPhysicalEnvironment? _indexPhysicalEnvironment;
    private bool? _renameTargetExists;
    private bool? _renameSourceSupported;
    private bool? _renameTargetSupported;
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

    /// <summary>Gets whether an applicable column add resumes an independently certified staging definition.</summary>
    /// <remarks>
    /// WHY: An owned staging column may already contain explicitly supplied values. Its
    /// completion is not an empty-table certificate for later key or dependency operations.
    /// </remarks>
    internal bool RequiredColumnAddResumesOwnedStaging { get; init; }

    /// <summary>Gets whether opaque earlier operations prevent read-only state projection.</summary>
    internal bool IsOpaqueProjectionUnknown { get; init; }

    /// <summary>Gets whether earlier writes invalidated this managed operation's immutable row evidence.</summary>
    internal bool IsModelManagedProjectionUnknown { get; init; }

    /// <summary>Gets whether global data effects postdate the managed target or its dependency proofs.</summary>
    internal bool HasStaleGlobalModelManagedData { get; init; }

    /// <summary>Gets the affected owner or dependency's latest local invalidation ordinal, when known.</summary>
    internal int? ModelManagedLocalOriginOrdinal { get; init; }

    /// <summary>Gets the preceding provider mutation or deferred DDL that invalidated an ordered proof.</summary>
    /// <remarks>
    /// WHY: A provider-specific DDL effect is not opaque SQL. Its exact stream ordinal
    /// lets Core attribute runtime validation without inventing a raw-SQL origin.
    /// </remarks>
    internal int? ProviderDeferredOriginOrdinal { get; init; }

    /// <summary>Gets provider-certified physical bindings for a matching existing-table constraint contract.</summary>
    /// <remarks>Semantic table matching alone cannot certify authored constraint symbols as physical names.</remarks>
    internal IReadOnlyList<SafeMigrationConstraintIdentityBinding>? TableConstraintIdentities { get; init; }

    /// <summary>Gets whether a fresh native DROP snapshot proves no surviving generated member.</summary>
    /// <remarks>
    /// The complete native count excludes only the exact dropped member. Core may consume this proof
    /// only after an accepted removal whose original owner lifetime has not been structurally changed.
    /// It proves neither row safety, table layout nor absence of ordinary columns.
    /// </remarks>
    internal bool DropLeavesNoGeneratedColumns { get; init; }

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

    /// <summary>Gets whether a current nullable-only repair preserves the column's intrinsic physical layout.</summary>
    /// <remarks>
    /// This is narrower than key or value-domain preservation. Core certifies an accepted unchanged source,
    /// current row and dependency lifetimes, and a provider-qualified non-data nullability transition.
    /// </remarks>
    internal bool RepairPreservesIntrinsicColumnLayout { get; private set; }

    /// <summary>Retains exact native endpoint and key layout after bounded nullable-only qualification.</summary>
    /// <returns>A separate analysis retaining all source evidence and both physical preservation certificates.</returns>
    internal SafeMigrationProviderAnalysis WithRepairPreservesIntrinsicColumnLayout()
    {
        var copy = (SafeMigrationProviderAnalysis)MemberwiseClone();

        // WHY: Widening can preserve values and keys while changing native FK compatibility.
        // This separate certificate must not grant generic row, NULL or empty-table evidence.
        copy.RepairPreservesIntrinsicColumnLayout = true;
        copy.RepairPreservesPhysicalKeys = true;

        return copy;
    }

    /// <summary>Gets independent destination presence for a rename, including when its source is absent.</summary>
    internal bool? RenameTargetExists
    {
        get => _renameTargetExists;
        init => _renameTargetExists = value;
    }

    /// <summary>Gets independently captured support for the rename source's physical owner and object kind.</summary>
    /// <remarks>
    /// Null means no source certificate; an accepted new physical source has separate lifetime proof.
    /// </remarks>
    internal bool? RenameSourceSupported
    {
        get => _renameSourceSupported;
        init => _renameSourceSupported = value;
    }

    /// <summary>Attaches source-kind support without changing destination or other provider certificates.</summary>
    /// <param name="supported">Whether the captured existing source is independently eligible for this rename.</param>
    /// <returns>A copy retaining every other provider observation.</returns>
    internal SafeMigrationProviderAnalysis WithRenameSourceSupported(bool supported)
    {
        var copy = (SafeMigrationProviderAnalysis)MemberwiseClone();

        copy._renameSourceSupported = supported;

        return copy;
    }

    /// <summary>Gets support for the authored destination contract independently of source lifetime.</summary>
    internal bool? RenameTargetSupported
    {
        get => _renameTargetSupported;
        init => _renameTargetSupported = value;
    }

    /// <summary>Attaches destination-contract support without changing source or occupancy evidence.</summary>
    /// <param name="supported">Whether the provider permits this authored destination.</param>
    /// <returns>A copy retaining every other provider observation.</returns>
    internal SafeMigrationProviderAnalysis WithRenameTargetSupported(bool supported)
    {
        var copy = (SafeMigrationProviderAnalysis)MemberwiseClone();

        copy._renameTargetSupported = supported;

        return copy;
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
    /// <param name="intermediateExists">Whether a staged cross-schema renames intermediate name is occupied.</param>
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
