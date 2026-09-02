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

        ObservedState = observedState;
        RepairCapability = repairCapability;
        PostconditionSatisfied = postconditionSatisfied;
        Code = code;
    }

    /// <summary>Gets the classified live state.</summary>
    public SafeMigrationObservedState ObservedState { get; }

    /// <summary>Gets the proven repair capability.</summary>
    public SafeMigrationRepairCapability RepairCapability { get; }

    /// <summary>Gets whether the final target condition currently holds.</summary>
    public bool PostconditionSatisfied { get; }

    /// <summary>Gets a stable, low-cardinality provider code.</summary>
    public string Code { get; }

    internal SafeMigrationModelManagedDataEvidence? ModelManagedDataEvidence { get; init; }
}
