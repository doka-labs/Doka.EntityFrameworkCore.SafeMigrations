namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Contains one provider's immutable live-state classification.</summary>
public sealed class SafeMigrationProviderAnalysis
{
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

        var differenceSnapshot = (differences ?? []).ToArray();
        if (differenceSnapshot.Any(static difference => difference is null))
        {
            throw new ArgumentException("Differences cannot contain null values.", nameof(differences));
        }

        if (differenceSnapshot.Length > SafeMigrationFacetDifference.MaximumDifferenceCount)
        {
            throw new ArgumentException(
                "An analysis cannot contain more than "
                + $"{SafeMigrationFacetDifference.MaximumDifferenceCount} differences.",
                nameof(differences));
        }

        ObservedState = observedState;
        RepairCapability = repairCapability;
        PostconditionSatisfied = postconditionSatisfied;
        Code = code;
        OperationalImpact = operationalImpact;
        Differences = Array.AsReadOnly(differenceSnapshot);
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
    public IReadOnlyList<SafeMigrationFacetDifference> Differences { get; }

    internal bool RequiresLiveDataProof { get; init; }

    internal SafeMigrationModelManagedDataEvidence? ModelManagedDataEvidence { get; init; }
}
