namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Runs read-only preflight and postflight analysis outside EF migration history.</summary>
public interface ISafeMigrationRunner
{
    /// <summary>Analyzes all operations from migrations not recorded as applied.</summary>
    /// <param name="context">The configured DbContext whose database is inspected.</param>
    /// <param name="options">The immutable metadata and validation options for the run.</param>
    /// <param name="cancellationToken">The token used to cancel the asynchronous operation.</param>
    /// <returns>The immutable preflight report.</returns>
    Task<SafeMigrationRunReport> AnalyzePendingMigrationsAsync(
        DbContext context,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken = default
    );

    /// <summary>Analyzes an explicit ordered operation sequence.</summary>
    /// <param name="context">The configured DbContext whose database is inspected.</param>
    /// <param name="operations">The ordered migration operations.</param>
    /// <param name="options">The immutable metadata and validation options for the run.</param>
    /// <param name="cancellationToken">The token used to cancel the asynchronous operation.</param>
    /// <returns>The immutable preflight report.</returns>
    Task<SafeMigrationRunReport> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken = default
    );

    /// <summary>Verifies final target conditions for an operation sequence.</summary>
    /// <param name="context">The configured DbContext whose database is inspected.</param>
    /// <param name="operations">The ordered migration operations.</param>
    /// <param name="options">The immutable metadata and validation options for the run.</param>
    /// <param name="cancellationToken">The token used to cancel the asynchronous operation.</param>
    /// <returns>The immutable postflight verification report.</returns>
    Task<SafeMigrationRunReport> VerifyAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken = default
    );
}
