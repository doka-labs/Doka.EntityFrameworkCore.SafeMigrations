namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Marks a generated legacy-convergence rollback that must fail closed because
/// object ownership cannot be reconstructed safely.
/// </summary>
internal sealed class SafeMigrationLegacyRollbackOperation : MigrationOperation;
