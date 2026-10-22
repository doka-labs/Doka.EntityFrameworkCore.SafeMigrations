namespace Doka.EntityFrameworkCore.SafeMigrations;

public sealed partial class SafeMigrationRunner
{
    /// <summary>Rechecks transformed table and managed-row contracts after final identity is verified.</summary>
    private async Task<PostflightRenameRevalidation> RevalidateRenamedContractsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        List<IModel?>? targetModels,
        IReadOnlyList<SafeMigrationProviderAnalysis> analyses,
        ISafeMigrationProviderObjectIdentityNormalizer? identityNormalizer,
        CancellationToken cancellationToken
    )
    {
        var probes = SafeMigrationPostflightProjection.CreateRenamedTableProbes(
            operations,
            analyses,
            identityNormalizer);

        var managedProbes = SafeMigrationPostflightProjection.CreateRenamedManagedDataProbes(
            operations,
            analyses,
            identityNormalizer);

        Dictionary<int, ModelManagedDataIntent>? managedIntents = null;
        if (managedProbes is not null)
        {
            managedIntents = new Dictionary<int, ModelManagedDataIntent>(managedProbes.Count);
            foreach (var probe in managedProbes)
            {
                managedIntents.Add(probe.OperationOrdinal, (ModelManagedDataIntent)probe.Operation.Intent);
            }

            if (probes is null)
            {
                probes = managedProbes;
            }
            else
            {
                probes.AddRange(managedProbes);
            }
        }

        if (probes is null)
        {
            return new PostflightRenameRevalidation(analyses, null);
        }

        // WHY: Identity qualification belongs to the provider's first catalog
        // batch. Reclassify all affected contracts together afterward, never one
        // query per rename and never by suppressing their unaffected facets.
        // Keep the immutable full-stream identity snapshot: analyzing only these
        // probes may replace mutable provider qualification for unrelated owners.
        var probeOperations = probes
            .Select(static probe => probe.Operation)
            .ToArray();

        IReadOnlyList<SafeMigrationProviderAnalysis> final;
        using (SafeMigrationTelemetry.StartAnalysisStage("provider-classification", probes.Count))
        {
            final = _providerAnalyzer is ISafeMigrationProviderTargetModelAnalyzer targetAnalyzer
                ? await targetAnalyzer.AnalyzeAsync(
                    context,
                    probeOperations,
                    targetModels is null
                        ? null
                        : probes
                            .Select(probe => targetModels[probe.OperationOrdinal])
                            .ToList(),
                    cancellationToken)
                : await _providerAnalyzer.AnalyzeAsync(context, probeOperations, cancellationToken);
        }

        if (final.Count != probes.Count)
        {
            throw new InvalidOperationException(
                "The SafeMigrations table analyzer returned an inconsistent result count.");
        }

        var updated = analyses.ToArray();
        // WHY: Map each compact probe result back to its original safe-operation ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < probes.Count; ordinal++)
        {
            updated[probes[ordinal].SafeOrdinal] = final[ordinal];
        }

        return new PostflightRenameRevalidation(updated, managedIntents);
    }

    /// <summary>Retains original report operations while final-writer identity uses certified renamed keys.</summary>
    private readonly record struct PostflightRenameRevalidation(
        IReadOnlyList<SafeMigrationProviderAnalysis> Analyses,
        IReadOnlyDictionary<int, ModelManagedDataIntent>? ManagedIntents
    );
}
