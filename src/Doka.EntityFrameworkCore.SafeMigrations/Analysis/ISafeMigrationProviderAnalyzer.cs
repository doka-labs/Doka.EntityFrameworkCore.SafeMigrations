namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Classifies ordered safe-operation batches against the live provider catalog
/// without executing target DDL or writing EF migration history.
/// </summary>
public interface ISafeMigrationProviderAnalyzer
{
    /// <summary>Gets the stable provider identifier.</summary>
    string ProviderId { get; }

    /// <summary>Reads the live provider and server metadata without changing database state.</summary>
    Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Analyzes an ordered operation batch against one consistent current
    /// database observation.
    /// </summary>
    Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Finds additive live objects outside complete table definitions supplied
    /// by ensure-table intents. The operation must be read-only.
    /// </summary>
    Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    );
}
