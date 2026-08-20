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
    /// <param name="context">The configured DbContext whose database is inspected.</param>
    /// <param name="cancellationToken">The token used to cancel the asynchronous operation.</param>
    /// <returns>The live provider and server environment.</returns>
    Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Analyzes an ordered operation batch against one consistent current
    /// database observation.
    /// </summary>
    /// <param name="context">The configured DbContext whose database is inspected.</param>
    /// <param name="operations">The ordered migration operations.</param>
    /// <param name="cancellationToken">The token used to cancel the asynchronous operation.</param>
    /// <returns>The ordered provider classifications for the supplied safe operations.</returns>
    Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Finds additive live objects outside complete table definitions supplied
    /// by ensure-table intents. The operation must be read-only.
    /// </summary>
    /// <param name="context">The configured DbContext whose database is inspected.</param>
    /// <param name="operations">The ordered migration operations.</param>
    /// <param name="cancellationToken">The token used to cancel the asynchronous operation.</param>
    /// <returns>The additive live objects outside the supplied expected catalog.</returns>
    Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken = default
    );
}
