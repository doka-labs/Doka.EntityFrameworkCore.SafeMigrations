namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Defines the legacy strict-mode behavior for safe migration operations.
/// </summary>
public enum SafeMigrationStrictMode
{
    /// <summary>
    /// Applies idempotent existence checks only and does not compare definitions.
    /// </summary>
    None = 0,

    /// <summary>
    /// Verifies the existing object definition and throws when it differs from the expected definition.
    /// </summary>
    ThrowIfDifferent = 1,
}
