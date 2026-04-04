namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Defines the conflict-handling strategy for the extended safe migration execution pipeline.
/// </summary>
public enum SafeMigrationConflictMode
{
    /// <summary>
    /// Applies idempotent existence checks only and skips definition comparison.
    /// </summary>
    None = 0,

    /// <summary>
    /// Throws when an existing object does not match the expected definition.
    /// </summary>
    ThrowIfDifferent = 1,

    /// <summary>
    /// Repairs approved, non-destructive drift cases when possible and rejects unsupported or unsafe differences.
    /// </summary>
    RepairIfPossible = 2,
}
