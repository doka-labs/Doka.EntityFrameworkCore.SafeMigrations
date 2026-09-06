namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes provider-owned migration operations whose postconditions can be
/// projected without invalidating existing table-scoped preflight evidence.
/// </summary>
internal interface ISafeMigrationProviderOperationProjection
{
    /// <summary>
    /// Determines whether the operation provably preserves every existing
    /// table, column, constraint, index, and row observation.
    /// </summary>
    /// <param name="operation">The provider-owned operation to classify.</param>
    /// <returns>
    /// <see langword="true"/> only when the provider guarantees that all
    /// existing table-scoped evidence remains valid; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    bool PreservesExistingTableState(
        MigrationOperation operation
    );
}
