namespace Doka.EntityFrameworkCore.SafeMigrations;

public sealed partial class SafeMigrationRunner
{
    /// <summary>
    /// Captures the provider's compact live classifications before any ordered projection is reduced.
    /// </summary>
    /// <param name="context">The borrowed context inside the established analysis scope.</param>
    /// <param name="operations">The original stream, including provider-owned operations.</param>
    /// <param name="safeOperations">The compact safe subset supplied to providers without target-model support.</param>
    /// <param name="mode">Whether final renamed contracts require provider revalidation.</param>
    /// <param name="targetModels">Authored target models aligned with the original stream, when available.</param>
    /// <param name="cancellationToken">The token forwarded to every provider classification and revalidation.</param>
    /// <returns>Live classifications and the exact provider identity views retained by ordered reduction.</returns>
    private async Task<ClassifiedOperationStream> ClassifyOperationStreamAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationOperation[] safeOperations,
        SafeMigrationReportMode mode,
        List<IModel?>? targetModels,
        CancellationToken cancellationToken
    )
    {
        // Providers classify the safe subset in one batch. The projection then
        // advances sequentially without rereading the database, which preflight cannot mutate.
        IReadOnlyList<SafeMigrationProviderAnalysis> liveAnalyses;
        using (SafeMigrationTelemetry.StartAnalysisStage("provider-classification", safeOperations.Length))
        {
            if (_providerAnalyzer is ISafeMigrationProviderTargetModelAnalyzer targetModelAnalyzer)
            {
                liveAnalyses = await targetModelAnalyzer.AnalyzeAsync(
                    context,
                    operations,
                    targetModels,
                    cancellationToken);
            }
            else
            {
                liveAnalyses = await _providerAnalyzer.AnalyzeAsync(context, safeOperations, cancellationToken);
            }
        }

        if (liveAnalyses.Count != safeOperations.Length)
        {
            throw new InvalidOperationException("The SafeMigrations analyzer returned an inconsistent result count.");
        }

        var objectIdentityNormalizer = _providerAnalyzer as ISafeMigrationProviderObjectIdentityNormalizer;
        var postflightIdentityNormalizer = mode == SafeMigrationReportMode.Postflight
            ? objectIdentityNormalizer?.GetPostflightIdentityNormalizer(operations)
            : null;

        IReadOnlyDictionary<int, ModelManagedDataIntent>? renamedManagedIntents = null;
        if (mode == SafeMigrationReportMode.Postflight)
        {
            var revalidated = await RevalidateRenamedContractsAsync(
                context,
                operations,
                targetModels,
                liveAnalyses,
                postflightIdentityNormalizer,
                cancellationToken);

            liveAnalyses = revalidated.Analyses;
            renamedManagedIntents = revalidated.ManagedIntents;
        }

        return new ClassifiedOperationStream(
            liveAnalyses,
            objectIdentityNormalizer,
            postflightIdentityNormalizer,
            renamedManagedIntents);
    }

    /// <summary>
    /// Retains provider-owned evidence without copying result collections or introducing a run context.
    /// </summary>
    /// <param name="Analyses">The compact safe-operation results, after required renamed-contract revalidation.</param>
    /// <param name="ObjectIdentityNormalizer">
    /// The live provider normalizer established by initial classification.
    /// </param>
    /// <param name="PostflightIdentityNormalizer">
    /// The qualified final-catalog view captured before revalidation.
    /// </param>
    /// <param name="RenamedManagedIntents">Certified renamed row contracts keyed by original operation ordinal.</param>
    private readonly record struct ClassifiedOperationStream(
        IReadOnlyList<SafeMigrationProviderAnalysis> Analyses,
        ISafeMigrationProviderObjectIdentityNormalizer? ObjectIdentityNormalizer,
        ISafeMigrationProviderObjectIdentityNormalizer? PostflightIdentityNormalizer,
        IReadOnlyDictionary<int, ModelManagedDataIntent>? RenamedManagedIntents
    );
}
