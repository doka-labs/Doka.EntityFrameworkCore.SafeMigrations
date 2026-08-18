namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Runs read-only preflight and postflight analysis outside EF migration history.</summary>
public interface ISafeMigrationRunner
{
    /// <summary>Analyzes all operations from migrations not recorded as applied.</summary>
    Task<SafeMigrationRunReport> AnalyzePendingMigrationsAsync(
        DbContext context,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken = default
    );

    /// <summary>Analyzes an explicit ordered operation sequence.</summary>
    Task<SafeMigrationRunReport> AnalyzeAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken = default
    );

    /// <summary>Verifies final target conditions for an operation sequence.</summary>
    Task<SafeMigrationRunReport> VerifyAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationRunOptions options,
        CancellationToken cancellationToken = default
    );
}
