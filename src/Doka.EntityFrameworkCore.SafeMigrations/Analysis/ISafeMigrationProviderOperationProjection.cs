namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes explicitly certified provider operations whose bounded effects
/// preserve existing table-scoped preflight evidence.
/// </summary>
internal interface ISafeMigrationProviderOperationProjection
{
    /// <summary>
    /// Determines whether the operation provably preserves every existing
    /// table, column, constraint, index, and row observation.
    /// </summary>
    /// <param name="operation">The provider operation to classify.</param>
    /// <returns>
    /// <see langword="true"/> only when the provider guarantees that all
    /// existing table-scoped evidence remains valid; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    bool PreservesExistingTableState(
        MigrationOperation operation
    );

    /// <summary>
    /// Determines whether a column rename preserves the absence of every
    /// unrelated column, even when dependent schema objects may change.
    /// </summary>
    /// <param name="operation">The accepted column rename.</param>
    /// <returns><see langword="true"/> only for a provider with this guarantee.</returns>
    bool PreservesUnrelatedColumnAbsence(
        RenameColumnIntent operation
    ) => false;

    /// <summary>
    /// Determines whether a provider analysis already incorporates the ordered
    /// effects of preceding operations and must remain authoritative.
    /// </summary>
    /// <param name="operation">The safe operation being projected.</param>
    /// <param name="analysis">The provider analysis captured for the operation.</param>
    /// <returns>
    /// <see langword="true"/> only when generic projection must retain the
    /// provider result; otherwise, <see langword="false"/>.
    /// </returns>
    bool IsSequenceAwareAnalysis(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis
    ) => false;
}
