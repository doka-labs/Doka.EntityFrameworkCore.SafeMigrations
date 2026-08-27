namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Selects the safe table semantics written into newly scaffolded migrations.
/// </summary>
public enum SafeMigrationScaffoldingMode
{
    /// <summary>
    /// Generates strict, idempotent table creation. An existing table must
    /// already match the complete expected definition.
    /// </summary>
    Strict = 0,

    /// <summary>
    /// Generates object-granular convergence for legacy databases whose table
    /// shapes may differ between application instances.
    /// </summary>
    LegacyConvergence = 1,
}
