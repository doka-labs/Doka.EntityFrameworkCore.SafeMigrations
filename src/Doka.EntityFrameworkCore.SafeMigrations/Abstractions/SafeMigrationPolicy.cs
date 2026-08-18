namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Defines how an existing object whose definition differs from the expected
/// definition is handled.
/// </summary>
public enum SafeMigrationPolicy
{
    /// <summary>
    /// Applies existence semantics only. An existing object is not repaired.
    /// </summary>
    ExistenceOnly = 0,

    /// <summary>
    /// Rejects an existing object whose definition differs.
    /// </summary>
    ThrowIfDifferent = 1,

    /// <summary>
    /// Applies only a provider-approved, lossless repair and rejects every
    /// other difference.
    /// </summary>
    RepairIfSafe = 2,
}
