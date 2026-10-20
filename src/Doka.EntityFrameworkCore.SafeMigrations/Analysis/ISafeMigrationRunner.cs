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
    /// <remarks>
    /// Effective final postconditions are checked against the live catalog, without replaying preflight.
    /// Later safe writers supersede earlier writes to the same provider-proven physical resource.
    /// A certified column rename also transforms an earlier known strict-table contract for a fresh
    /// complete comparison; unrelated facets and the original report identity remain unchanged.
    /// Opaque or unproven rename lineage cannot transform that contract. Otherwise, supply a reviewed
    /// final-state contract and validate the report against that contract's fingerprint.
    /// </remarks>
    /// <param name="context">The configured DbContext whose database is inspected.</param>
    /// <param name="operations">The ordered operations whose postconditions must all hold in the final state.</param>
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
