namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Provides a stable reason identity, understandable explanation and safe next step.</summary>
/// <remarks>Explanations describe established evidence; they never grant permission to bypass a guard.</remarks>
[PublicAPI]
public readonly struct SafeMigrationDiagnosticExplanation
{
    // WHY: Explicit nullable storage distinguishes an uninitialized struct from its non-null display fallback.
    // ReSharper disable once ReplaceWithFieldKeyword
    private readonly string? _reasonCode;

    // WHY: Retain nullable backing storage while the public getter guarantees a non-null default explanation.
    // ReSharper disable once ReplaceWithFieldKeyword
    private readonly string? _reason;

    // WHY: Default structs must retain the unavailable-evidence fallback instead of an initialized next step.
    // ReSharper disable once ReplaceWithFieldKeyword
    private readonly string? _nextStep;

    /// <summary>Initializes a payload-free explanation from the shared diagnostic vocabulary.</summary>
    /// <param name="reasonCode">The stable diagnostic reason identity.</param>
    /// <param name="reason">The constant description of established or explicitly unavailable evidence.</param>
    /// <param name="nextStep">The constant safe next step, or an empty string when no action is needed.</param>
    internal SafeMigrationDiagnosticExplanation(
        string reasonCode,
        string reason,
        string nextStep
    )
    {
        _reasonCode = reasonCode;
        _reason = reason;
        _nextStep = nextStep;
    }

    /// <summary>Gets a stable reason code; a default value explicitly identifies unavailable evidence.</summary>
    public string ReasonCode => _reasonCode ?? "diagnostic_reason_unavailable";

    /// <summary>Gets the explanation; a default value never claims a specific observed defect.</summary>
    public string Reason => _reason ?? "A more specific cause is not established by the available evidence.";

    /// <summary>Gets a safe next step, or an empty string for a state requiring no corrective action.</summary>
    public string NextStep => _nextStep ?? "Inspect the operation and protected diagnostic evidence before retrying.";
}
