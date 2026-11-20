namespace Doka.EntityFrameworkCore.SafeMigrations;

public sealed partial class SafeMigrationRunner
{
    /// <summary>
    /// Reads final unexpected-object evidence only after ordered reduction has completed successfully.
    /// </summary>
    /// <param name="context">The borrowed context inside the established analysis scope.</param>
    /// <param name="operations">The untouched complete stream used for inventory ownership.</param>
    /// <param name="mode">The preflight or postflight report mode.</param>
    /// <param name="options">The caller's instance and target identities.</param>
    /// <param name="modelFingerprint">The already validated canonical model fingerprint.</param>
    /// <param name="contractFingerprint">The authored operation-stream fingerprint.</param>
    /// <param name="generatedAtUtc">The original invocation timestamp.</param>
    /// <param name="environment">The validated provider and engine identity.</param>
    /// <param name="reduction">The complete ordered assessments and their aggregate status.</param>
    /// <param name="cancellationToken">The actual token forwarded to final inventory I/O.</param>
    /// <returns>The immutable public report with unchanged identifiers, codes and ordering.</returns>
    private async Task<SafeMigrationRunReport> CreateRunReportAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationReportMode mode,
        SafeMigrationRunOptions options,
        string modelFingerprint,
        string contractFingerprint,
        DateTimeOffset generatedAtUtc,
        SafeMigrationProviderEnvironment environment,
        OrderedAssessmentResult reduction,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<SafeMigrationUnexpectedObject> unexpectedObjects;
        using (SafeMigrationTelemetry.StartAnalysisStage("unexpected-inventory", operations.Count))
        {
            unexpectedObjects = await _providerAnalyzer.FindUnexpectedObjectsAsync(
                context,
                operations,
                cancellationToken);
        }

        return new SafeMigrationRunReport(
            mode,
            reduction.Status,
            generatedAtUtc,
            options.InstanceId,
            environment,
            options.TargetMigrationId,
            modelFingerprint,
            contractFingerprint,
            reduction.Assessments,
            unexpectedObjects);
    }
}
