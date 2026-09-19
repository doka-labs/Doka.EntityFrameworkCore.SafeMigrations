namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Analyzes ordered operations against the cumulative migration target model
/// that applies at each operation.
/// </summary>
internal interface ISafeMigrationProviderTargetModelAnalyzer
{
    /// <summary>Analyzes operations with their operation-specific target models.</summary>
    Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<IModel?>? targetModels,
        CancellationToken cancellationToken = default
    );
}
