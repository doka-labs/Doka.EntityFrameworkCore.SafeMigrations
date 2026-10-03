namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>
/// Bounds the SQL Server catalog capture independently from the shared defaults.
/// </summary>
/// <remarks>
/// WHY: Native batching separates statement width from execute count. A sequential
/// connection executes each statement separately, so it retains the shared wider
/// statement bound instead of multiplying executes. Both shapes retain 256 classifiers
/// per bounded group; these dispatch limits do not establish engine performance.
/// </remarks>
internal static class SqlServerCatalogQueryLimits
{
    /// <summary>The maximum classifier count inside one optimizer plan.</summary>
    public const int MaximumOperationsPerStatement = 8;

    /// <summary>The maximum statement count inside one transport batch.</summary>
    public const int MaximumStatementsPerBatch = 32;

    /// <summary>The classifier count per sequential execute, preserving the shared statement bound.</summary>
    public const int MaximumSequentialOperationsPerStatement =
        SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement;

    /// <summary>The sequential execute count per bounded group, preserving the shared batch bound.</summary>
    public const int MaximumSequentialStatementsPerBatch = SafeMigrationCatalogQueryLimits.MaximumStatementsPerBatch;

    /// <summary>The maximum classifier count inside one transport batch.</summary>
    public const int MaximumOperationsPerBatch = MaximumOperationsPerStatement * MaximumStatementsPerBatch;
}
