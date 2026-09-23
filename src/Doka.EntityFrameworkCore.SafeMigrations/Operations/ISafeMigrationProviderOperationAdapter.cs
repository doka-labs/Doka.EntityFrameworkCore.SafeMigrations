namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Adapts provider-owned metadata that Core cannot interpret without knowing
/// the active provider contract.
/// </summary>
internal interface ISafeMigrationProviderOperationAdapter
{
    /// <summary>Attempts to convert one provider operation to a closed SafeMigrations operation.</summary>
    /// <param name="operation">The original EF Core migration operation.</param>
    /// <param name="safeOperation">The converted operation when this adapter owns the operation shape.</param>
    /// <returns><see langword="true"/> when the operation was converted.</returns>
    bool TryNormalize(
        MigrationOperation operation,
        [NotNullWhen(true)] out SafeMigrationOperation? safeOperation
    );

    /// <summary>
    /// Determines whether one provider operation is safe to execute unchanged
    /// and does not require object-level SafeMigrations state analysis.
    /// </summary>
    /// <param name="operation">The original EF Core migration operation.</param>
    /// <returns><see langword="true"/> only for a completely validated provider contract.</returns>
    bool IsCertifiedPassthrough(
        MigrationOperation operation
    );
}
