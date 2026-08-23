namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Defines the provider-neutral action selected for a classified operation.
/// </summary>
public enum SafeMigrationAction
{
    /// <summary>Apply the requested target operation.</summary>
    Apply = 0,

    /// <summary>Do not change the target database.</summary>
    NoOp = 1,

    /// <summary>Apply a provider-approved, lossless repair.</summary>
    Repair = 2,

    /// <summary>Reject because the active engine cannot represent the operation.</summary>
    RejectUnsupported = 3,

    /// <summary>Reject because the observed definition differs.</summary>
    RejectDifferent = 4,

    /// <summary>Reject because existing data prevents the operation.</summary>
    RejectDataBlocked = 5,

    /// <summary>Reject because a required parent object is absent.</summary>
    RejectPrerequisiteMissing = 6,
}
